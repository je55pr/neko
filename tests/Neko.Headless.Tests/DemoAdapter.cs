using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Neko;

namespace Neko.Headless.Tests;

/// <summary>Offline demonstration adapter for exercising the real local API.</summary>
public sealed class DemoFactory : IConversationProviderFactory
{
    public IConversationProvider Create(string settingsPath) => new DemoProvider();
}

internal sealed class DemoProvider : IScopedConversationProvider
{
    private readonly Dictionary<string, List<VisibleConversationMessage>> _history = [];
    private readonly Dictionary<string, Channel<PublicConversationUpdate>> _events = [];

    public Task<SendReceipt> CreateInScopeAsync(
        string scope, PreparedOutboundMessage submission, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected, "scope_required"));
        var id = Guid.NewGuid().ToString("N");
        _history[id] = [];
        _events[id] = Channel.CreateUnbounded<PublicConversationUpdate>();
        Add(id, submission.Text);
        return Task.FromResult(new SendReceipt(SendDisposition.Accepted, ConversationId: id));
    }

    public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation) =>
        Task.FromResult(new SendReceipt(SendDisposition.Rejected, "scope_required"));

    public Task<SendReceipt> SendAsync(string id, string text, CancellationToken cancellation)
    {
        if (!_history.ContainsKey(id))
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected, "unknown_conversation"));
        Add(id, text);
        return Task.FromResult(new SendReceipt(SendDisposition.Accepted));
    }

    private void Add(string id, string text)
    {
        _history[id].Add(new VisibleConversationMessage(Guid.NewGuid().ToString("N"),
            VisibleMessageRole.User, text));
        var reply = "Demo reply to: " + text;
        _history[id].Add(new VisibleConversationMessage(Guid.NewGuid().ToString("N"),
            VisibleMessageRole.Assistant, reply));
        var messageId = Guid.NewGuid().ToString("N");
        _events[id].Writer.TryWrite(new PublicConversationUpdate(
            messageId, PublicUpdateKind.Final, PublicUpdateOperation.Append, reply));
        _events[id].Writer.TryWrite(new PublicConversationUpdate(
            messageId, PublicUpdateKind.Final, PublicUpdateOperation.Complete));
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id, [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (!_events.TryGetValue(id, out var channel)) yield break;
        while (await channel.Reader.WaitToReadAsync(cancellation))
        {
            if (!channel.Reader.TryRead(out var update)) continue;
            yield return update;
            if (update.Operation == PublicUpdateOperation.Complete) yield break;
        }
    }

    public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation) =>
        Task.FromResult<VisibleConversation?>(_history.TryGetValue(id, out var messages)
            ? new VisibleConversation(id, "Demo", messages.ToArray()) : null);
    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
        Task.FromResult<IReadOnlyList<ConversationSummary>>(
            _history.Keys.Select(id => new ConversationSummary(id, "Demo")).ToArray());
    public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
        Task.FromResult(new StopReceipt(StopDisposition.NotActive));
}
