using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Fact]
    public async Task DirectSendCannotOverwriteCompletedTurnBeforeInboxConsumesProof()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        using var inbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), managers, runtime);
        var first = inbox.Enqueue("manager/main", "one").Result!.Message;
        var second = inbox.Enqueue("manager/main", "two").Result!.Message;

        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
        var premature = await managers.SendAsync("manager/main", "legacy before proof",
            CancellationToken.None);
        Assert.Equal("manager_inbox_pending", premature!.Receipt.Reason);
        Assert.Equal(1, provider.SendCalls);

        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", first.Id)!.State);
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", second.Id)!.State);
        Assert.Equal(2, provider.SendCalls);

        var legacy = await managers.SendAsync("manager/main", "legacy afterward",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, legacy!.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(3, provider.SendCalls);
    }

    [Fact]
    public async Task DirectSendCannotOverwriteReconciledTurnBeforeInboxConsumesProof()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        using var inbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), managers, runtime);
        var first = inbox.Enqueue("manager/main", "one").Result!.Message;
        var second = inbox.Enqueue("manager/main", "two").Result!.Message;

        provider.DeferCompletion = true;
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        provider.Publish(new PublicConversationUpdate("timeout", PublicUpdateKind.Final,
            PublicUpdateOperation.ObservationExpired));
        await AssertObservationExpired(runtime, "chat-1");
        Assert.Equal("uncertain", runtime.Get("chat-1")!.Status);
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("uncertain", inbox.Get("manager/main", first.Id)!.State);

        provider.AuthoritativeComplete = true;
        var restored = (await managers.ReconcileAsync("manager/main",
            CancellationToken.None))!;
        Assert.Equal("restored", restored.Reconciliation.Outcome);
        Assert.Equal("idle", restored.Manager.LifecycleState);
        var premature = await managers.SendAsync("manager/main", "legacy before proof",
            CancellationToken.None);
        Assert.Equal("manager_inbox_pending", premature!.Receipt.Reason);
        Assert.Equal(1, provider.SendCalls);

        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", first.Id)!.State);
        provider.DeferCompletion = false;
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal("completed", inbox.Get("manager/main", second.Id)!.State);
        Assert.Equal(2, provider.SendCalls);
    }
}
