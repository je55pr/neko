using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Fact]
    public async Task DurableReconciliationAlsoReleasesProcessLocalDeliveryLatch()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        await runtime.CreateAsync("first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");
        provider.DeferCompletion = true;
        provider.CorrelateUncertainSend = true;
        provider.UncertainNextSend = true;
        Assert.Equal(SendDisposition.Uncertain,
            (await runtime.SendAsync("chat-1", "correlated", CancellationToken.None))
                .Disposition);
        provider.AuthoritativeComplete = true;
        Assert.Equal("restored",
            (await runtime.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
        provider.UncertainNextSend = false;
        provider.DeferCompletion = false;
        var explicitFollowup = await runtime.SendAsync(
            "chat-1", "operator follow-up", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, explicitFollowup.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(2, provider.SendCalls);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
    }

    [Fact]
    public async Task SameProcessCreationReconciliationReleasesCreationCoordinatorLatch()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            UncertainNextCreation = true, CorrelatedUncertainCreationId = "chat-1",
            AuthoritativeComplete = true
        };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        Assert.Equal(SendDisposition.Uncertain,
            (await managers.CreateAsync("manager/main", "first", CancellationToken.None))
                .Receipt.Disposition);
        Assert.Equal("restored",
            (await managers.ReconcileAsync("manager/main", CancellationToken.None))!
                .Reconciliation.Outcome);
        provider.UncertainNextCreation = false;
        provider.RejectNextManagerCreation = true;
        var nextExplicitCreation = await runtime.CreateAsync(
            "new operator request", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, nextExplicitCreation.Disposition);
        Assert.Equal("provider_busy", nextExplicitCreation.Reason);
        Assert.Equal(2, provider.ManagerCreationCalls);
        Assert.Null(new ChatJournal(folder.Path).GetPendingCreation());
    }

    [Fact]
    public async Task LeftoverAcceptedManagerMarkerReportsGuardClearedNotRemoteCompletion()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        Assert.True(journal.TryStartCreation(
            "ABCDEF123456", "manager/main"));
        journal.SetCreationCorrelation("chat-1");
        journal.Set(new ChatRecord("chat-1", "generating",
            ProgressTurnId: "accepted-first-turn"));
        var registry = new ManagerRegistry(folder.Path);
        Assert.True(registry.TryReserve("manager/main", out _));
        registry.Update("manager/main", current => current with {
            ConversationId = "chat-1",
            CheckpointPointer = ManagerRegistry.CheckpointFor("chat-1"),
            LifecycleState = "generating"
        });
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);
        var managers = new ManagerService(registry, runtime);
        var result = (await managers.ReconcileAsync(
            "manager/main", CancellationToken.None))!;
        Assert.Equal("guard_cleared", result.Reconciliation.Outcome);
        Assert.Equal("durable_creation_binding", result.Reconciliation.Reason);
        Assert.Equal("chat-1", result.Manager.ConversationId);
        Assert.Equal("uncertain", result.Manager.LifecycleState);
        Assert.Null(journal.GetPendingCreation());
        Assert.Equal(0, provider.ManagerCreationCalls);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task UntrustedProviderReasonIsNeverPersistedOrReturned()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);
        provider.RawInspectionReason = "remote key secret/private-endpoint token";
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var result = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("verification_unavailable", result.Reason);
        Assert.Equal("verification_unavailable",
            recovered.Get("chat-1")!.LastReconciliation!.Reason);
        Assert.DoesNotContain("private-endpoint",
            File.ReadAllText(Path.Combine(folder.Path,
                ManagerRegistry.CheckpointFor("chat-1"))));
    }

    [Fact]
    public void AtomicCreationClearCannotDeleteAnotherOwnersGuard()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        Assert.True(journal.TryStartCreation("digest", "manager/main"));
        journal.SetCreationCorrelation("chat-1");
        Assert.False(journal.ClearCorrelatedCreation("manager/other", "chat-1"));
        Assert.False(journal.ClearCorrelatedCreation("manager/main", "chat-2"));
        Assert.Equal("chat-1", journal.GetPendingCreation()!.CorrelatedConversationId);
        Assert.True(journal.ClearCorrelatedCreation("manager/main", "chat-1"));
        Assert.Null(journal.GetPendingCreation());
    }

}
