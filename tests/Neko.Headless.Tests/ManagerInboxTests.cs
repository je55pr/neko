using System.Net;
using System.Text.Json;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private static ChatRuntime InboxRuntime(string folder, FakeProvider provider) =>
        new(new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder));

    private static async Task<ManagerService> CreateIdleManager(
        ChatRuntime runtime, string folder)
    {
        var managers = new ManagerService(new ManagerRegistry(folder), runtime);
        Assert.Equal(SendDisposition.Accepted,
            (await managers.CreateAsync("manager/main", "initial", CancellationToken.None))
                .Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("idle", managers.Get("manager/main")?.LifecycleState);
        return managers;
    }

    [Fact]
    public async Task ConcurrentIdempotencyReservationsHaveOneStableIdAcrossRestart()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ManagerInboxJournal(folder.Path);
        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ =>
            Task.Run(() => journal.Enqueue("manager/main", "hello", "key-1"))));
        Assert.Single(results.Select(r => r.Message.Id).Distinct());
        Assert.Single(journal.List("manager/main"));
        Assert.Equal(InboxEnqueueStatus.Duplicate,
            new ManagerInboxJournal(folder.Path).Enqueue("manager/main", "hello", "key-1").Status);
        Assert.Equal(InboxEnqueueStatus.Conflict,
            journal.Enqueue("manager/main", "different", "key-1").Status);
        Assert.Single(journal.List("manager/main"));
        var another = journal.Enqueue("manager/second", "another", "key-1");
        Assert.Equal(InboxEnqueueStatus.Enqueued, another.Status);
        Assert.Equal(1, another.Message.Sequence);
    }

    [Fact]
    public async Task BusyManagerKeepsOrderedBacklogUntilEachTurnCompletes()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { DeferCompletion = true };
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        await managers.CreateAsync("manager/main", "initial", CancellationToken.None);
        using var inbox = new ManagerInboxService(new ManagerInboxJournal(folder.Path),
            managers, runtime);
        var first = inbox.Enqueue("manager/main", "one", "a").Result!.Message;
        var second = inbox.Enqueue("manager/main", "two", "b").Result!.Message;
        Assert.Equal("queued", first.State);
        Assert.Equal(2, second.Sequence);
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);

        provider.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("queued", inbox.Get("manager/main", second.Id)!.State);

        provider.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", first.Id)!.State);
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(2, provider.SendCalls);
        provider.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", second.Id)!.State);
        Assert.Equal(2, provider.SendCalls);
    }

    [Fact]
    public async Task OfflineHistoryRemainsQueuedWithCooldownAndNoProviderSend()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var clock = new InboxClock();
        using var inbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path, clock), managers, runtime, clock);
        var message = inbox.Enqueue("manager/main", "later").Result!.Message;
        provider.ReturnNullHistory = true;
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal("queued", inbox.Get("manager/main", message.Id)!.State);
        Assert.NotNull(inbox.Get("manager/main", message.Id)!.NotBefore);
        provider.ReturnNullHistory = false;
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        clock.AdvanceSeconds(16);
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
        await WaitForTerminal(runtime, "chat-1");
    }

    [Fact]
    public async Task ExplicitRetryAfterDefinitiveRejectionHonorsCooldown()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var clock = new InboxClock();
        using var inbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path, clock), managers, runtime, clock);
        var message = inbox.Enqueue("manager/main", "rate limited").Result!.Message;
        provider.PendingSend = new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.PendingSend.SetResult(new SendReceipt(SendDisposition.Rejected, "rate_limited")
            { Retryable = true, RetryAfterSeconds = 120 });
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("rejected", inbox.Get("manager/main", message.Id)!.State);
        Assert.Equal(1, provider.SendCalls);
        provider.PendingSend = null;
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("queued", inbox.Retry("manager/main", message.Id)!.State);
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
        clock.AdvanceSeconds(121);
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(2, provider.SendCalls);
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", message.Id)!.State);
    }

    [Fact]
    public async Task UncertainReceiptNeverAutoReplaysOrAllowsExplicitRetry()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { UncertainNextSend = true };
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        using var inbox = new ManagerInboxService(new ManagerInboxJournal(folder.Path),
            managers, runtime);
        var message = inbox.Enqueue("manager/main", "uncertain").Result!.Message;
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("uncertain", inbox.Get("manager/main", message.Id)!.State);
        Assert.Null(inbox.Retry("manager/main", message.Id));
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task InterruptedDispatchBlocksManagerEvenWhenRuntimeIsIdle()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var journal = new ManagerInboxJournal(folder.Path);
        var message = journal.Enqueue("manager/main", "never replay", null).Message;
        journal.Transition("manager/main", message.Id, InboxStates.Queued,
            m => m with { State = InboxStates.Dispatching });
        using var recoveredInbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), managers, runtime);
        Assert.Equal("uncertain", recoveredInbox.Get("manager/main", message.Id)!.State);
        Assert.Equal("uncertain", managers.Get("manager/main")!.LifecycleState);
        Assert.Equal("inbox_uncertainty_blocked",
            (await managers.SendAsync("manager/main", "legacy must block",
                CancellationToken.None))!.Receipt.Reason);
        Assert.False(await recoveredInbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal("uncertain",
            new ManagerInboxJournal(folder.Path).Get("manager/main", message.Id)!.State);
    }

    [Fact]
    public async Task PersistenceFailureBeforeDispatchCannotReachProvider()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var journal = new ManagerInboxJournal(folder.Path,
            beforePersist: message => {
                if (message.State == InboxStates.Dispatching)
                    throw new IOException("Simulated disk fault");
            });
        using var inbox = new ManagerInboxService(journal, managers, runtime);
        var message = inbox.Enqueue("manager/main", "saved first").Result!.Message;
        await Assert.ThrowsAsync<IOException>(() =>
            inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("queued", journal.Get("manager/main", message.Id)!.State);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task PersistenceFailureAfterRemoteAcceptanceNeverReplaysOnRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var journal = new ManagerInboxJournal(folder.Path,
            beforePersist: message => {
                if (message.State == InboxStates.Accepted)
                    throw new IOException("Simulated acknowledgement loss");
            });
        var messageId = "";
        using (var inbox = new ManagerInboxService(journal, managers, runtime))
        {
            messageId = inbox.Enqueue("manager/main", "accepted once").Result!.Message.Id;
            await Assert.ThrowsAsync<IOException>(() =>
                inbox.ProcessAsync("manager/main", CancellationToken.None));
            Assert.Equal(InboxStates.Uncertain, journal.Get("manager/main", messageId)!.State);
            Assert.Equal(1, provider.SendCalls);
            await WaitForTerminal(runtime, "chat-1");
        }
        using var recovered = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), managers, runtime);
        Assert.Equal("uncertain", recovered.Get("manager/main", messageId)!.State);
        Assert.False(await recovered.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task ConcurrentWorkersAndCancellationRespectSingleDispatch()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        using var inbox = new ManagerInboxService(new ManagerInboxJournal(folder.Path),
            managers, runtime);
        var canceled = inbox.Enqueue("manager/main", "cancel").Result!.Message;
        Assert.Equal("cancelled", inbox.Cancel("manager/main", canceled.Id)!.State);
        Assert.Null(inbox.Cancel("manager/main", canceled.Id));

        var one = inbox.Enqueue("manager/main", "first").Result!.Message;
        var two = inbox.Enqueue("manager/main", "second").Result!.Message;
        provider.PendingSend = new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = inbox.ProcessAsync("manager/main", CancellationToken.None);
        for (var i = 0; i < 100 && provider.SendCalls == 0; i++)
            await Task.Delay(5);
        Assert.Equal(1, provider.SendCalls);
        var simultaneous = inbox.ProcessAsync("manager/main", CancellationToken.None);
        Assert.False(simultaneous.IsCompleted);
        Assert.Null(inbox.Cancel("manager/main", one.Id));
        provider.PendingSend.SetResult(new SendReceipt(SendDisposition.Rejected, "rejected"));
        await processing;
        await simultaneous;
        Assert.Equal(1, provider.SendCalls);
        Assert.Equal("queued", inbox.Get("manager/main", two.Id)!.State);
    }

    [Fact]
    public async Task InboxEndpointDistinguishesLocalAckDuplicateConflictAndCancellation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        using var inbox = new ManagerInboxService(new ManagerInboxJournal(folder.Path),
            managers, runtime);
        var (status, body) = await ExecuteManagerResult(ManagerEndpoints.EnqueueInbox(
            inbox, "main", new InboxEnqueueRequest("confidential message", "request:1")));
        Assert.Equal(202, status);
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal("queued",
            parsed.RootElement.GetProperty("message").GetProperty("state").GetString());
        Assert.DoesNotContain("confidential message", body);
        var id = parsed.RootElement.GetProperty("message").GetProperty("id").GetString()!;
        Assert.Equal(200, (await ExecuteManagerResult(ManagerEndpoints.EnqueueInbox(
            inbox, "main", new InboxEnqueueRequest("confidential message", "request:1")))).Status);
        Assert.Equal(409, (await ExecuteManagerResult(ManagerEndpoints.EnqueueInbox(
            inbox, "main", new InboxEnqueueRequest("other", "request:1")))).Status);
        Assert.Equal(200, (await ExecuteManagerResult(ManagerEndpoints.CancelInbox(
            inbox, "main", id))).Status);
        Assert.Equal(409, (await ExecuteManagerResult(ManagerEndpoints.CancelInbox(
            inbox, "main", id))).Status);
        Assert.Equal(0, provider.SendCalls);
    }

    private sealed class InboxClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceSeconds(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
