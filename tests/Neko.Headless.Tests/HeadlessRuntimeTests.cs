using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
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
        var history = (await runtime.ReadAsync("chat-1", CancellationToken.None)).History;
        Assert.NotNull(history);
        Assert.Equal(4, history.Messages.Count);
        Assert.Equal("second", history.Messages[2].Text);
        Assert.Equal("idle", Assert.Single(runtime.List()).Status);
    }

    [Fact]
    public async Task NewSendWatchersCannotReplayPreviousTurnCompletion()
    {
        using var folder=new TemporaryDirectory();
        var inner=new FakeProvider();
        using var runtime=new ChatRuntime(
            new ScopedConversationProvider(inner,"scope-a"),
            new ChatJournal(folder.Path));
        await runtime.CreateAsync("first",CancellationToken.None);
        await WaitForTerminal(runtime,"chat-1");
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        inner.PendingSend=pending;
        var send=runtime.SendAsync("chat-1","second",CancellationToken.None);
        for(var attempt=0;attempt<50 &&
            runtime.List().Single().Status!="sending";attempt++)
            await Task.Delay(10);
        Assert.Equal("sending",runtime.List().Single().Status);
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var watcher=runtime.WatchAsync("chat-1",cancel.Token)
            .GetAsyncEnumerator(cancel.Token);
        var next=watcher.MoveNextAsync().AsTask();
        await Task.Delay(80);
        Assert.False(next.IsCompleted); // It must not see old Complete.
        pending.SetResult(new SendReceipt(SendDisposition.Rejected,"test_reject"));
        Assert.Equal(SendDisposition.Rejected,(await send).Disposition);
        Assert.True(await next);
        Assert.Equal(PublicUpdateOperation.Fail,watcher.Current.Operation);
        Assert.Equal("idle",runtime.List().Single().Status);
    }

    [Fact]
    public async Task SendPreservesLatestHistoryWhileReceiptIsPending()
    {
        using var folder=new TemporaryDirectory();
        var inner=new FakeProvider();
        using var runtime=new ChatRuntime(
            new ScopedConversationProvider(inner,"scope-a"),
            new ChatJournal(folder.Path));
        await runtime.CreateAsync("first",CancellationToken.None);
        await WaitForTerminal(runtime,"chat-1");
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        inner.PendingSend=pending;
        var send=runtime.SendAsync("chat-1","second",CancellationToken.None);
        for(var attempt=0;attempt<50 &&
            runtime.List().Single().Status!="sending";attempt++)
            await Task.Delay(10);
        inner.InjectMessage("updated during read");
        var read=(await runtime.ReadAsync("chat-1",CancellationToken.None)).History;
        Assert.NotNull(read);
        Assert.Contains(read.Messages,message=>message.Text=="updated during read");
        Assert.Contains(runtime.List().Single().History!.Messages,
            message=>message.Text=="updated during read");
        pending.SetResult(new SendReceipt(SendDisposition.Uncertain,"test_uncertain"));
        Assert.Equal(SendDisposition.Uncertain,(await send).Disposition);
        Assert.Equal("uncertain",runtime.List().Single().Status);
        Assert.NotNull(runtime.List().Single().History);
        Assert.Equal(read.Messages.Count,runtime.List().Single().History!.Messages.Count);
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
        var cached = (await second.ReadAsync("chat-1", CancellationToken.None)).History;
        Assert.Equal(2, cached?.Messages.Count);
        var rejected = await second.SendAsync("chat-1", "do not misroute", CancellationToken.None);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        // The remote read might be temporarily unavailable. A later attempt
        // remains possible, but must repeat all provider checks.
        Assert.Equal("idle", Assert.Single(second.List()).Status);
    }


    [Fact]
    public async Task TransientReadFailureDoesNotPermanentlyBlockAChat()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path));
        await runtime.CreateAsync("initial", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");
        inner.FailNextRead = true;
        var rejected = await runtime.SendAsync("chat-1", "later", CancellationToken.None);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        Assert.Equal("idle", Assert.Single(runtime.List()).Status);
        var next = await runtime.SendAsync("chat-1", "later", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, next.Disposition);
        await WaitForTerminal(runtime, "chat-1");
    }

    [Fact]
    public async Task UnsupportedStopDuringAndAfterGenerationIsHarmless()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider { DeferCompletion = true, SupportsStop = false };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path));
        Assert.Equal(SendDisposition.Accepted,
            (await runtime.CreateAsync("first", CancellationToken.None)).Disposition);
        var generating = runtime.Get("chat-1")!;
        Assert.Equal("generating", generating.Status);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stop = await runtime.StopAsync("chat-1", CancellationToken.None);
            Assert.Equal(StopDisposition.Unsupported, stop.Disposition);
            Assert.Equal("stop_not_supported", stop.Reason);
            Assert.Equal(generating.Status, runtime.Get("chat-1")?.Status);
            Assert.Equal(generating.UpdatedAt, runtime.Get("chat-1")?.UpdatedAt);
        }
        Assert.Equal(0, inner.StopCalls);
        inner.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        var completed = runtime.Get("chat-1")!;
        Assert.Equal("idle", completed.Status);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stop = await runtime.StopAsync("chat-1", CancellationToken.None);
            Assert.Equal(StopDisposition.Unsupported, stop.Disposition);
            Assert.Equal("stop_not_supported", stop.Reason);
            Assert.Equal(completed.Status, runtime.Get("chat-1")?.Status);
            Assert.Equal(completed.UpdatedAt, runtime.Get("chat-1")?.UpdatedAt);
        }
        inner.DeferCompletion = false;
        Assert.Equal(SendDisposition.Accepted,
            (await runtime.SendAsync("chat-1", "follow-up", CancellationToken.None)).Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("idle", runtime.Get("chat-1")?.Status);
        Assert.Equal(4, (await runtime.ReadAsync("chat-1", CancellationToken.None)).History?.Messages.Count);
        Assert.Equal(0, inner.StopCalls);
    }

    [Fact]
    public async Task UncertainStopBlocksFurtherSendsAndSurvivesRestart()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider { DeferCompletion = true, StopUncertain = true };
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path)))
        {
            Assert.Equal(SendDisposition.Accepted,
                (await first.CreateAsync("first", CancellationToken.None)).Disposition);
            var stop = await first.StopAsync("chat-1", CancellationToken.None);
            Assert.Equal(StopDisposition.Uncertain, stop.Disposition);
            Assert.Equal("stop_uncertain", Assert.Single(first.List()).Status);
            Assert.Equal(SendDisposition.Rejected,
                (await first.SendAsync("chat-1", "not safe", CancellationToken.None)).Disposition);
        }
        using var second = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), new ChatJournal(folder.Path));
        Assert.Equal(SendDisposition.Rejected,
            (await second.SendAsync("chat-1", "still not safe", CancellationToken.None)).Disposition);
    }

    [Fact]
    public void HistoryUpdatePreservesMoreRecentStatus()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat", "idle"));
        var prior = journal.Get("chat")!;
        journal.Set(prior with { Status = "sending" });
        journal.Update("chat", latest => latest with {
            History = new VisibleConversation("chat", "title", [])
        });
        Assert.Equal("sending", journal.Get("chat")?.Status);
    }

    [Fact]
    public void RestartMigratesLegacyUnavailableButPreservesUncertain()
    {
        using var folder=new TemporaryDirectory();
        var journal=new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("legacy","unavailable"));
        journal.Set(new ChatRecord("ambiguous","uncertain"));
        using var runtime=new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(),"scope-a"),journal);
        Assert.Equal("idle",runtime.Get("legacy")?.Status);
        Assert.Equal("uncertain",runtime.Get("ambiguous")?.Status);
    }

    [Fact]
    public void RestartReclassifiesInterruptedOperationsAsUncertain()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("interrupted", "generating"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(new FakeProvider(), "scope-a"), journal);
        Assert.Equal("uncertain", Assert.Single(runtime.List()).Status);
    }

    [Fact]
    public void JournalRejectsTwoHostsUsingTheSameDirectory()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        using (journal.AcquireHostLease())
            Assert.Throws<InvalidOperationException>(() => journal.AcquireHostLease());
        using var second = journal.AcquireHostLease();
        Assert.NotNull(second);
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

    private sealed partial class FakeProvider : IScopedConversationProvider, IProviderRateLimitStatus
    {
        public string? Scope;
        public int UnscopedCreations;
        public bool UncertainNextSend;
        public bool UncertainNextCreation;
        public bool DeferCompletion;
        public bool StopUncertain;
        public bool SupportsStop { get; set; } = true;
        public int StopCalls;
        public bool FailNextRead;
        public int? RateLimitRetryAfterSeconds { get; set; }
        public int ReadCalls;
        public int SendCalls;
        public TaskCompletionSource<SendReceipt>? PendingSend;
        public void CompletePending() =>
            _events.Writer.TryWrite(new PublicConversationUpdate(
                Guid.NewGuid().ToString("N"), PublicUpdateKind.Final,
                PublicUpdateOperation.Complete));
        public void InjectMessage(string text) =>
            _messages.Add(new VisibleConversationMessage(
                "injected-update",VisibleMessageRole.User,text));
        private readonly Channel<PublicConversationUpdate> _events = Channel.CreateUnbounded<PublicConversationUpdate>();
        private readonly List<VisibleConversationMessage> _messages = [];
        public Task<bool> BelongsToScopeAsync(string scope, string id, CancellationToken cancellation) =>
            Task.FromResult(scope == "scope-a" && id == "chat-1" && _messages.Count > 0);
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
            Interlocked.Increment(ref SendCalls);
            if(PendingSend is not null)return PendingSend.Task;
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
            if (!DeferCompletion)
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
        public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation)
        {
            Interlocked.Increment(ref ReadCalls);
            if (PendingRead is not null) return PendingRead.Task;
            if (ThrowOnRead) throw new InvalidOperationException("Simulated read unavailable.");
            if (ReturnNullHistory) return Task.FromResult<VisibleConversation?>(null);
            if (FailNextRead)
            {
                FailNextRead = false;
                return Task.FromResult<VisibleConversation?>(null);
            }
            return Task.FromResult<VisibleConversation?>(_messages.Count == 0 ? null :
                new VisibleConversation(id, "Test", _messages.ToArray()));
        }
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<StopReceipt> StopAsync(string _, CancellationToken cancellation)
        {
            StopCalls++;
            return Task.FromResult(new StopReceipt(StopUncertain
                ? StopDisposition.Uncertain : StopDisposition.NotActive));
        }
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
