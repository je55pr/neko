using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class HeadlessRuntimeTests
{
    [Fact]
    public async Task ScopedConversationCanBeCreatedCompletedAndContinued()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider();
        using var wrapped = new ScopedConversationProvider(inner, "scope-a");
        using var runtime = new ChatRuntime(wrapped, new ChatJournal(folder.Path));
        var created = await runtime.CreateAsync("first", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, created.Disposition);
        Assert.Equal("scope-a", inner.Scope);
        Assert.Equal(0, inner.UnscopedCreations);
        Assert.Equal("chat-1", created.ConversationId);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("idle", Assert.Single(runtime.List()).Status);

        var sent = await runtime.SendAsync("chat-1", "second", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, sent.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        var history = await runtime.ReadAsync("chat-1", CancellationToken.None);
        Assert.NotNull(history);
        Assert.Equal(4, history.Messages.Count);
        Assert.Equal("second", history.Messages[2].Text);
        Assert.Equal("idle", Assert.Single(runtime.List()).Status);
    }

    [Fact]
    public async Task UnconfirmedSendSurvivesRestartAndNeverAutomaticallyRetries()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path)))
        {
            await first.CreateAsync("first", CancellationToken.None);
            await WaitForTerminal(first, "chat-1");
            inner.UncertainNextSend = true;
            var receipt = await first.SendAsync("chat-1", "potential duplicate", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, receipt.Disposition);
            Assert.Equal("uncertain", Assert.Single(first.List()).Status);
        }
        using var second = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), new ChatJournal(folder.Path));
        var rejected = await second.SendAsync("chat-1", "must not replay", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.Equal("conversation_not_idle", rejected.Reason);
    }

    [Fact]
    public async Task CachedHistoryCannotBeMistakenForAResumableProviderConversation()
    {
        using var folder = new TemporaryDirectory();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), new ChatJournal(folder.Path)))
        {
            await first.CreateAsync("first", CancellationToken.None);
            await WaitForTerminal(first, "chat-1");
        }
        using var second = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), new ChatJournal(folder.Path));
        var cached = await second.ReadAsync("chat-1", CancellationToken.None);
        Assert.Equal(2, cached?.Messages.Count);
        var rejected = await second.SendAsync("chat-1", "do not misroute", CancellationToken.None);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        Assert.Equal("unavailable", Assert.Single(second.List()).Status);
    }

    [Fact]
    public async Task UncertainCreationBlocksFurtherCreationAcrossRestart()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider { UncertainNextCreation = true };
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path)))
        {
            var initial = await first.CreateAsync("first", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, initial.Disposition);
        }
        using var second = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), new ChatJournal(folder.Path));
        var rejected = await second.CreateAsync("second", CancellationToken.None);
        Assert.Equal("previous_creation_unresolved", rejected.Reason);
    }

    private static async Task WaitForTerminal(ChatRuntime runtime, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var update in runtime.WatchAsync(id, timeout.Token))
            if (update.Kind == PublicUpdateKind.Final &&
                update.Operation == PublicUpdateOperation.Complete) return;
        Assert.Fail("No completed reply.");
    }

    private sealed class FakeProvider : IScopedConversationProvider
    {
        public string? Scope;
        public int UnscopedCreations;
        public bool UncertainNextSend;
        public bool UncertainNextCreation;
        private readonly Channel<PublicConversationUpdate> _events = Channel.CreateUnbounded<PublicConversationUpdate>();
        private readonly List<VisibleConversationMessage> _messages = [];
        public Task<SendReceipt> CreateInScopeAsync(
            string scope, PreparedOutboundMessage message, CancellationToken cancellation)
        {
            Scope = scope;
            if (UncertainNextCreation)
                return Task.FromResult(new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"));
            AddExchange(message.Text);
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted, ConversationId: "chat-1"));
        }
        public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation)
        {
            UnscopedCreations++;
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected));
        }
        public Task<SendReceipt> SendAsync(string id, string text, CancellationToken cancellation)
        {
            if (UncertainNextSend) return Task.FromResult(
                new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"));
            AddExchange(text);
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted));
        }
        private void AddExchange(string text)
        {
            _messages.Add(new VisibleConversationMessage(Guid.NewGuid().ToString("N"),
                VisibleMessageRole.User, text));
            _messages.Add(new VisibleConversationMessage(Guid.NewGuid().ToString("N"),
                VisibleMessageRole.Assistant, "reply " + text));
            _events.Writer.TryWrite(new PublicConversationUpdate(
                Guid.NewGuid().ToString("N"), PublicUpdateKind.Final, PublicUpdateOperation.Complete));
        }
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id, [EnumeratorCancellation] CancellationToken cancellation)
        {
            while (await _events.Reader.WaitToReadAsync(cancellation))
            {
                if (!_events.Reader.TryRead(out var update)) continue;
                yield return update;
                if (update.Operation == PublicUpdateOperation.Complete) yield break;
            }
        }
        public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation) =>
            Task.FromResult<VisibleConversation?>(_messages.Count == 0 ? null :
                new VisibleConversation(id, "Test", _messages.ToArray()));
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "neko-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
