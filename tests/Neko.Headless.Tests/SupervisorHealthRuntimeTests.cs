using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Fact]
    public void ReadyHealthExposesUncertaintyWithoutUnlockingAnyCheckpoint()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "generating", DispatchCount: 1,
            PendingTurn: new PendingTurnEvidence("original-hash", [], true)));
        Assert.True(journal.TryStartCreation("new-creation-hash", "manager/main"));

        var registry = new ManagerRegistry(folder.Path);
        Assert.True(registry.TryReserve("manager/main", out _));
        var inboxJournal = new ManagerInboxJournal(folder.Path);
        var uncertain = inboxJournal.Enqueue("manager/main", "original message", null).Message;
        Assert.NotNull(inboxJournal.Transition("manager/main", uncertain.Id,
            InboxStates.Queued, entry => entry with { State = InboxStates.Uncertain }));

        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);
        var managers = new ManagerService(registry, runtime);
        using var inbox = new ManagerInboxService(inboxJournal, managers, runtime);
        var config = new HostConfiguration("unused", "unused", "unused",
            "scope-a", new string('s', 32), folder.Path);
        var health = HostHealthSnapshot.Create(config, journal, runtime, managers, inbox);

        Assert.Equal("ready", health.Status);
        Assert.Equal(1, health.UncertainConversations);
        Assert.True(health.PendingCreation);
        Assert.Equal(1, health.BlockedManagerInboxEntries);
        Assert.Equal("uncertain", journal.Get("chat-1")?.Status);
        Assert.NotNull(journal.GetPendingCreation());
        Assert.Equal(InboxStates.Uncertain,
            Assert.Single(inboxJournal.List("manager/main")).State);
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal(0, provider.ManagerCreationCalls);
    }
}
