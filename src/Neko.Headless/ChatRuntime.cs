using System.Collections.Concurrent;
using System.Threading.Channels;
using Neko;

namespace Neko.Headless;

/// <summary>
/// Single-process headless conversation owner. Pending operations survive restarts
/// as unresolved until authoritative provider state is reconciled.
/// </summary>
internal sealed class ChatRuntime : IDisposable
{
    private readonly IConversationProvider _provider;
    private readonly ConversationCommandCoordinator _commands;
    private readonly ChatJournal _journal;
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, Channel<PublicConversationUpdate>> _updates = new();
    private readonly CancellationTokenSource _lifetime = new();

    public ChatRuntime(IConversationProvider provider, ChatJournal journal)
    {
        _provider = provider;
        _commands = new ConversationCommandCoordinator(provider);
        _journal = journal;
    }

    public IReadOnlyList<ChatRecord> List() => _journal.List();

    public async Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation)
    {
        await _creationGate.WaitAsync(cancellation);
        try
        {
            var prepared = OutboundMessagePreparation.ForNew(text);
            if (prepared.Message is null)
                return new SendReceipt(SendDisposition.Rejected, "invalid_submission");
            if (!_journal.TryStartCreation())
                return new SendReceipt(SendDisposition.Rejected, "previous_creation_unresolved");

            SendReceipt receipt;
            try { receipt = await _commands.CreateAsync(prepared.Message, cancellation); }
            catch (Exception) { receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"); }

            if (receipt.Disposition == SendDisposition.Rejected)
                _journal.ClearCreation();
            else if (receipt.Disposition == SendDisposition.Accepted &&
                     !string.IsNullOrWhiteSpace(receipt.ConversationId))
            {
                _journal.Set(new ChatRecord(receipt.ConversationId, "generating"));
                _journal.ClearCreation();
                StartPump(receipt.ConversationId);
            }
            // An unconfirmed creation (including a missing conversation ID)
            // retains the on-disk lock. Never blindly create a duplicate.
            return receipt.Disposition == SendDisposition.Accepted &&
                   string.IsNullOrWhiteSpace(receipt.ConversationId)
                ? new SendReceipt(SendDisposition.Uncertain, "conversation_id_unknown")
                : receipt;
        }
        finally { _creationGate.Release(); }
    }

    public async Task<SendReceipt> SendAsync(
        string id, string text, CancellationToken cancellation)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var prepared = OutboundMessagePreparation.ForExisting(id, text);
            if (prepared.Message is null)
                return new SendReceipt(SendDisposition.Rejected, "invalid_submission");
            var current = _journal.Get(id);
            if (current is null)
                return new SendReceipt(SendDisposition.Rejected, "unknown_conversation");
            if (current.Status != "idle")
                return new SendReceipt(SendDisposition.Rejected, "conversation_not_idle");
            // A cached transcript alone does not establish that the adapter can
            // resume the remote conversation after a process restart.
            VisibleConversation? upstream;
            try { upstream = await _provider.ReadAsync(id, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { upstream = null; }
            if (upstream is null)
            {
                _journal.Set(current with { Status = "unavailable" });
                return new SendReceipt(SendDisposition.Rejected, "provider_history_unavailable");
            }
            _journal.Set(current with { Status = "sending", History = upstream });

            SendReceipt receipt;
            try { receipt = await _commands.SendAsync(prepared.Message, cancellation); }
            catch (Exception) { receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"); }

            switch (receipt.Disposition)
            {
                case SendDisposition.Rejected:
                    _journal.Set(current with { Status = "idle" });
                    break;
                case SendDisposition.Accepted:
                    _journal.Set(current with { Status = "generating" });
                    StartPump(id);
                    break;
                default:
                    _journal.Set(current with { Status = "uncertain" });
                    break;
            }
            return receipt;
        }
        finally { gate.Release(); }
    }

    public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
        _journal.Get(id) is null
            ? Task.FromResult(new StopReceipt(StopDisposition.NotActive, "unknown_conversation"))
            : _commands.StopAsync(id, cancellation);

    public async Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation)
    {
        var record = _journal.Get(id);
        if (record is null) return null;
        try
        {
            var current = await _provider.ReadAsync(id, cancellation);
            if (current is not null)
            {
                _journal.Set(record with { History = current });
                return current;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { /* Return the most recent local checkpoint. */ }
        return record.History;
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        if (_journal.Get(id) is null) yield break;
        var channel = _updates.GetOrAdd(id, _ => Channel.CreateUnbounded<PublicConversationUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }));
        await foreach (var update in channel.Reader.ReadAllAsync(cancellation))
        {
            yield return update;
            if (update.Kind == PublicUpdateKind.Final &&
                update.Operation is PublicUpdateOperation.Complete or PublicUpdateOperation.Fail)
                yield break;
        }
    }

    private void StartPump(string id)
    {
        var channel = Channel.CreateUnbounded<PublicConversationUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _updates[id] = channel;
        _ = Task.Run(async () =>
        {
            var terminal = false;
            try
            {
                await foreach (var update in _provider.WatchAsync(id, _lifetime.Token))
                {
                    if (update.Kind != PublicUpdateKind.Final ||
                        update.Operation is not (PublicUpdateOperation.Complete or PublicUpdateOperation.Fail))
                    {
                        channel.Writer.TryWrite(update);
                        continue;
                    }
                    terminal = true;
                    var record = _journal.Get(id);
                    if (record is not null)
                    {
                        VisibleConversation? snapshot = null;
                        try { snapshot = await _provider.ReadAsync(id, _lifetime.Token); }
                        catch (Exception) { /* Keep existing snapshot. */ }
                        _journal.Set(record with {
                            Status = update.Operation == PublicUpdateOperation.Complete ? "idle" : "uncertain",
                            History = snapshot ?? record.History
                        });
                    }
                    channel.Writer.TryWrite(update);
                    break;
                }
            }
            catch (Exception) { /* The absence of a terminal receipt is uncertain. */ }
            finally
            {
                if (!terminal)
                {
                    var current = _journal.Get(id);
                    if (current is not null)
                        _journal.Set(current with { Status = "uncertain" });
                    channel.Writer.TryWrite(new PublicConversationUpdate(
                        Guid.NewGuid().ToString("D"), PublicUpdateKind.Final, PublicUpdateOperation.Fail));
                }
                channel.Writer.TryComplete();
            }
        });
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_provider is IDisposable disposable) disposable.Dispose();
    }
}
