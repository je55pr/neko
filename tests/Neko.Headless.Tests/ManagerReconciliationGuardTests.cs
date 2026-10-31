using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Fact]
    public async Task PersistedManagerBindingCanClearStrandedCreationGuardAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var original = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), original);
            var created = await managers.CreateAsync("manager/main", "first",
                CancellationToken.None);
            Assert.Equal(SendDisposition.Accepted, created.Receipt.Disposition);
            await WaitForTerminal(original, "chat-1");
        }

        // Reconstruct the post-registry-commit/pre-guard-cleanup crash boundary:
        // the persisted mapping exists and the original correlated creation
        // marker still blocks all new creations.
        var journal = new ChatJournal(folder.Path);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("first")));
        Assert.True(journal.TryStartCreation(digest, "manager/main"));
        journal.SetCreationCorrelation("chat-1");

        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var managersAgain = new ManagerService(new ManagerRegistry(folder.Path), recovered);
        Assert.Equal("chat-1", managersAgain.Get("manager/main")!.ConversationId);
        Assert.NotNull(journal.GetPendingCreation());

        var result = (await managersAgain.ReconcileAsync(
            "manager/main", CancellationToken.None))!;
        Assert.Equal("guard_cleared", result.Reconciliation.Outcome);
        Assert.Equal("durable_creation_binding", result.Reconciliation.Reason);
        Assert.Equal("chat-1", result.Manager.ConversationId);
        Assert.Equal("idle", result.Manager.LifecycleState);
        Assert.Null(journal.GetPendingCreation());
        Assert.Equal(1, provider.ManagerCreationCalls);
        Assert.Equal(0, provider.SendCalls);
    }

}
