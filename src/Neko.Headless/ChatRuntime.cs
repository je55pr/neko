using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ConversationUpdateFeed> _updates = new();
    private readonly ConcurrentDictionary<string, Task> _pumps = new();
    private readonly CancellationTokenSource _lifetime = new();

    public ChatRuntime(IConversationProvider provider, ChatJournal journal)
    {
        _provider = provider;
        _commands = new ConversationCommandCoordinator(provider);
        _journal = journal;
        // The previous process may have died after a remote submission. Mark
        // in-flight operations unresolved until the provider can reconcile them.
        foreach (var record in _journal.List())
        {
            if (record.Status is "sending" or "generating" or "stopping")
                _journal.Update(record.Id, current => current with { Status = "uncertain" });
            else if (record.Status == "unavailable")
                // Older builds set this after a failed read without sending.
                _journal.Update(record.Id, current => current with { Status = "idle" });
        }
    }

    public IReadOnlyList<ChatRecord> List() => _journal.List();
    public ChatRecord? Get(string id) => _journal.Get(id);

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
                // A read can fail transiently (e.g. rate limiting). No send
                // occurred, so keep the chat idle for a later guarded attempt.
                return new SendReceipt(SendDisposition.Rejected, "provider_history_unavailable")
                {
                    // History preflight failed before any submission. A later
                    // explicit attempt is safe, but never submit without a fresh read.
                    Retryable = true,
                    RetryAfterSeconds = (_provider as IProviderRateLimitStatus)?
                        .RateLimitRetryAfterSeconds
                };
            }
            // Bind the new stream before making "sending" observable, so
            // watchers cannot replay a terminal event from the previous turn.
            var feed=new ConversationUpdateFeed();
            _updates.TryGetValue(id,out var previousFeed);
            _updates[id]=feed;
            try
            {
                _journal.Update(id, latest => latest with {
                    Status="sending",History=upstream
                });
            }
            catch
            {
                if(previousFeed is not null)_updates[id]=previousFeed;
                else _updates.TryRemove(id,out _);
                feed.Publish(new PublicConversationUpdate(id,
                    PublicUpdateKind.Final,PublicUpdateOperation.Fail));
                throw;
            }

            SendReceipt receipt;
            try { receipt = await _commands.SendAsync(prepared.Message, cancellation); }
            catch (Exception) { receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"); }

            switch (receipt.Disposition)
            {
                case SendDisposition.Rejected:
                    _journal.Update(id, latest => latest with { Status = "idle" });
                    feed.Publish(new PublicConversationUpdate(id,PublicUpdateKind.Final,
                        PublicUpdateOperation.Fail));
                    break;
                case SendDisposition.Accepted:
                    _journal.Update(id, latest => latest with { Status = "generating" });
                    StartPump(id,feed);
                    break;
                default:
                    _journal.Update(id, latest => latest with { Status = "uncertain" });
                    feed.Publish(new PublicConversationUpdate(id,PublicUpdateKind.Final,
                        PublicUpdateOperation.Fail));
                    break;
            }
            return receipt;
        }
        finally { gate.Release(); }
    }

    public async Task<StopReceipt> StopAsync(string id, CancellationToken cancellation)
    {
        // Unsupported Stop is a pure no-op, including for the on-disk journal.
        if (!_provider.SupportsStop)
            return new StopReceipt(StopDisposition.Unsupported, "stop_not_supported");
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var current = _journal.Get(id);
            if (current is null)
                return new StopReceipt(StopDisposition.NotActive, "unknown_conversation");
            if (current.Status != "generating")
                return new StopReceipt(StopDisposition.NotActive, "conversation_not_generating");

            var transition=_journal.Update(id,latest =>
                latest.Status=="generating"?
                    latest with {Status="stopping"}:latest);
            if(transition?.Status!="stopping")
                return new StopReceipt(StopDisposition.NotActive,
                    "conversation_already_finished");
            StopReceipt receipt;
            try { receipt = await _commands.StopAsync(id, cancellation); }
            catch (Exception) { receipt = new StopReceipt(StopDisposition.Uncertain, "stop_unknown"); }

            if (receipt.Disposition is StopDisposition.NotActive or StopDisposition.Unsupported)
                _journal.Update(id, latest => latest.Status == "stopping"
                    ? latest with { Status = "generating" } : latest);
            else if (receipt.Disposition == StopDisposition.Uncertain)
                _journal.Update(id, latest => latest.Status=="stopping"
                    ? latest with { Status = "stop_uncertain" }: latest);
            // A requested stop remains pending until an authoritative terminal event.
            return receipt;
        }
        finally { gate.Release(); }
    }

    public async Task<HistoryReadResult> ReadAsync(string id, CancellationToken cancellation)
    {
        // Local registration, not provider availability, determines whether an ID is known.
        if (_journal.Get(id) is null) return new HistoryReadResult(false, null);
        try
        {
            var current = await _provider.ReadAsync(id, cancellation);
            if (current is not null)
            {
                _journal.Update(id, latest => latest with { History = current });
                return new HistoryReadResult(true, current);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { /* Preserve the most recent local checkpoint. */ }
        // Re-read after awaiting the provider: the stream pump may have saved
        // a transcript while this request was waiting for an unavailable read.
        return new HistoryReadResult(true, _journal.Get(id)?.History);
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        if (_journal.Get(id) is null) yield break;
        if (!_updates.TryGetValue(id, out var feed))
        {
            var record = _journal.Get(id);
            yield return new PublicConversationUpdate(
                id, PublicUpdateKind.Final,
                record?.Status == "idle"
                    ? PublicUpdateOperation.Complete : PublicUpdateOperation.Fail);
            yield break;
        }
        await foreach (var update in feed.WatchAsync(cancellation))
            yield return update;
    }

    private void StartPump(string id,ConversationUpdateFeed? currentFeed=null)
    {
        var feed = currentFeed??new ConversationUpdateFeed();
        _updates[id] = feed;
        _pumps[id] = Task.Run(async () =>
        {
            var terminal = false;
            try
            {
                await foreach (var update in _provider.WatchAsync(id, _lifetime.Token))
                {
                    if (update.Kind != PublicUpdateKind.Final ||
                        update.Operation is not (PublicUpdateOperation.Complete or PublicUpdateOperation.Fail))
                    {
                        feed.Publish(update);
                        continue;
                    }
                    terminal = true;
                    VisibleConversation? snapshot = null;
                    try { snapshot = await _provider.ReadAsync(id, _lifetime.Token); }
                    catch (Exception) { /* Keep existing snapshot. */ }
                    _journal.Update(id, latest => latest with {
                        Status = update.Operation == PublicUpdateOperation.Complete ? "idle" : "uncertain",
                        History = snapshot ?? latest.History
                    });
                    feed.Publish(update);
                    break;
                }
            }
            catch (Exception) { /* The absence of a terminal receipt is uncertain. */ }
            finally
            {
                if (!terminal)
                {
                    _journal.Update(id, current => current with { Status = "uncertain" });
                    feed.Publish(new PublicConversationUpdate(
                        Guid.NewGuid().ToString("D"), PublicUpdateKind.Final, PublicUpdateOperation.Fail));
                }
                // The terminal event also releases late subscribers.
            }
        });
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try
        {
            // Do not release provider resources while a stream consumer is still
            // accessing them. A noncooperative adapter has a bounded grace period.
            Task.WhenAll(_pumps.Values).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { /* Cancellation is expected on shutdown. */ }
        _lifetime.Dispose();
        if (_provider is IDisposable disposable) disposable.Dispose();
    }
}
