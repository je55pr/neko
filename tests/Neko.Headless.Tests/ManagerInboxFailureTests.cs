using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Fact]
    public async Task InterruptedInboxDispatchRemainsBlockedWhenRegistryWriteFails()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var registry = new ManagerRegistry(folder.Path, beforePersist: record =>
        {
            if (record.LifecycleState == "uncertain")
                throw new IOException("Injected registry checkpoint failure.");
        });
        var managers = new ManagerService(registry, runtime);
        var created = await managers.CreateAsync("manager/main", "initial",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, created.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");

        var journal = new ManagerInboxJournal(folder.Path);
        var item = journal.Enqueue("manager/main", "never replay", "key").Message;
        journal.Transition("manager/main", item.Id, InboxStates.Queued,
            message => message with { State = InboxStates.Dispatching });

        // The inbox persisted ambiguity before the manager-registry write fails.
        // Its process-local block must still reject legacy sends and never replay.
        using var inbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), managers, runtime);
        Assert.Equal(InboxStates.Uncertain, inbox.Get("manager/main", item.Id)!.State);
        Assert.Equal("inbox_uncertainty_blocked",
            (await managers.SendAsync("manager/main", "unsafe follow-up",
                CancellationToken.None))!.Receipt.Reason);
        Assert.False(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(0, provider.SendCalls);

        // The same guard is re-established from the inbox journal after restart.
        var restartedManagers = new ManagerService(
            new ManagerRegistry(folder.Path, beforePersist: record =>
            {
                if (record.LifecycleState == "uncertain")
                    throw new IOException("Injected registry checkpoint failure.");
            }), runtime);
        using var restartedInbox = new ManagerInboxService(
            new ManagerInboxJournal(folder.Path), restartedManagers, runtime);
        Assert.Equal("inbox_uncertainty_blocked",
            (await restartedManagers.SendAsync("manager/main", "still unsafe",
                CancellationToken.None))!.Receipt.Reason);
        Assert.Equal(0, provider.SendCalls);
    }
}
