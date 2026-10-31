using System.Net;
using System.Net.Http;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private sealed partial class FakeProvider
    {
        public bool AuthoritativeComplete;
        public bool AuthoritativeReadUnavailable;
        public bool AuthoritativeThrow;
        public string? CorrelatedUncertainCreationId;
        public int Inspections;

        public void RemoteExchange(string text) => AddExchange(text);

        public Task<ReconciliationInspection> InspectAsync(
            ReconciliationProbe probe, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Inspections);
            cancellation.ThrowIfCancellationRequested();
            if (AuthoritativeThrow) throw new IOException("private provider error");
            if (RateLimitRetryAfterSeconds is not null)
                return Task.FromResult(new ReconciliationInspection(false, "provider_rate_limited"));
            if (AuthoritativeReadUnavailable)
                return Task.FromResult(new ReconciliationInspection(false, "history_unavailable"));
            if (!AuthoritativeComplete)
                return Task.FromResult(new ReconciliationInspection(false, "remote_not_verified_complete"));
            var messages = _messages.ToArray();
            var suffix = messages.Skip(probe.BaselineMessageIds.Count).ToArray();
            var user = suffix.FirstOrDefault(message => message.Role == VisibleMessageRole.User);
            var assistant = suffix.LastOrDefault(message => message.Role == VisibleMessageRole.Assistant);
            return Task.FromResult(new ReconciliationInspection(true,
                "authoritative_turn_complete",
                new VisibleConversation(probe.ConversationId, "Remote", messages),
                user?.Id, assistant?.Id));
        }
    }

    private static async Task PrepareAcceptedTimeout(
        ChatRuntime runtime, FakeProvider provider)
    {
        var creation = await runtime.CreateAsync("first", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, creation.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        provider.DeferCompletion = true;
        Assert.Equal(SendDisposition.Accepted,
            (await runtime.SendAsync("chat-1", "second", CancellationToken.None))
                .Disposition);
        Assert.Equal("generating", runtime.Get("chat-1")!.Status);
    }

    [Fact]
    public async Task AcceptedLateTerminalRequiresExplicitReconciliationAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);

        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        Assert.Equal("conversation_not_idle",
            (await recovered.SendAsync("chat-1", "do not replay", CancellationToken.None)).Reason);
        var pending = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("blocked", pending.Outcome);
        Assert.Equal("remote_not_verified_complete", pending.Reason);
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);

        provider.AuthoritativeComplete = true;
        var restored = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("restored", restored.Outcome);
        Assert.NotNull(restored.AssistantMessageId);
        Assert.Equal("idle", recovered.Get("chat-1")!.Status);
        Assert.Null(recovered.Get("chat-1")!.PendingTurn);
        Assert.Equal(1, provider.SendCalls);
        Assert.Equal("restored", recovered.Get("chat-1")!.LastReconciliation!.Outcome);
    }

    [Fact]
    public async Task ThrottledAndUnavailableHistoryNeverReleasesUncertainGuard()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);

        provider.AuthoritativeComplete = true;
        provider.RateLimitRetryAfterSeconds = 75;
        using (var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
        {
            var blocked = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
            Assert.Equal("blocked", blocked.Outcome);
            Assert.Equal("provider_rate_limited", blocked.Reason);
            Assert.Equal(75, blocked.RetryAfterSeconds);
            Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        }
        using var restarted = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        Assert.Equal("provider_rate_limited",
            restarted.Get("chat-1")!.LastReconciliation!.Reason);
        provider.RateLimitRetryAfterSeconds = null;
        provider.AuthoritativeReadUnavailable = true;
        Assert.Equal("history_unavailable",
            (await restarted.ReconcileAsync("chat-1", CancellationToken.None)).Reason);
        provider.AuthoritativeReadUnavailable = false;
        Assert.Equal("restored",
            (await restarted.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task ExtraRemoteUserAndUnmatchedHistoryStayBlocked()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);
        provider.AuthoritativeComplete = true;
        provider.InjectMessage("unrelated external follow-up");
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var blocked = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("history_not_correlated", blocked.Reason);
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task UncorrelatedUncertainSendRemainsBlockedEvenIfTextMatchesLater()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        await runtime.CreateAsync("first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");
        provider.UncertainNextSend = true;
        var sent = await runtime.SendAsync("chat-1", "ambiguous", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, sent.Disposition);
        provider.RemoteExchange("ambiguous");
        provider.AuthoritativeComplete = true;
        var blocked = await runtime.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("missing_dispatch_correlation", blocked.Reason);
        Assert.Equal(0, provider.Inspections);
        Assert.Equal("uncertain", runtime.Get("chat-1")!.Status);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task CorrelatedCreationReconcilesManagerBindingAcrossRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            UncertainNextCreation = true, CorrelatedUncertainCreationId = "chat-1"
        };
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), first);
            var created = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, created.Receipt.Disposition);
            Assert.Null(created.Manager.ConversationId);
        }
        using (var restarted = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), restarted);
            Assert.Equal("blocked",
                (await managers.ReconcileAsync("manager/main", CancellationToken.None))!
                    .Reconciliation.Outcome);
            provider.AuthoritativeComplete = true;
            var result = (await managers.ReconcileAsync("manager/main", CancellationToken.None))!;
            Assert.Equal("restored", result.Reconciliation.Outcome);
            Assert.Equal("chat-1", result.Manager.ConversationId);
            Assert.Equal("idle", result.Manager.LifecycleState);
            Assert.Equal("idle", restarted.Get("chat-1")!.Status);
            Assert.Equal(1, provider.ManagerCreationCalls);
            Assert.Equal(0, provider.SendCalls);
        }
        using var final = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path));
        var bound = new ManagerService(new ManagerRegistry(folder.Path), final);
        Assert.Equal("chat-1", bound.Get("manager/main")!.ConversationId);
        Assert.Equal("not_applicable",
            (await final.ReconcileCreationAsync(null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task CreationWithoutCorrelatedIdentityNeverGuessesSession()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { UncertainNextCreation = true, AuthoritativeComplete = true };
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), first);
            Assert.Equal(SendDisposition.Uncertain,
                (await managers.CreateAsync("manager/main", "first", CancellationToken.None))
                    .Receipt.Disposition);
        }
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path));
        var managersAgain = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        var unresolved = (await managersAgain.ReconcileAsync("manager/main", CancellationToken.None))!;
        Assert.Equal("missing_creation_correlation", unresolved.Reconciliation.Reason);
        Assert.Null(unresolved.Manager.ConversationId);
        Assert.Equal(0, provider.Inspections);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task FailedReconciliationCheckpointNeverUnlocksOrLosesProof()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);
        provider.AuthoritativeComplete = true;
        var fail = true;
        var journal = new ChatJournal(folder.Path, record => {
            if (fail && record.Status == "idle" &&
                record.LastReconciliation is { Outcome: "restored" })
                throw new IOException("Injected checkpoint failure.");
        });
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);
        await Assert.ThrowsAsync<IOException>(() =>
            recovered.ReconcileAsync("chat-1", CancellationToken.None));
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        Assert.NotNull(recovered.Get("chat-1")!.PendingTurn);
        fail = false;
        Assert.Equal("restored",
            (await recovered.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task ConfirmedRejectedSendIsIdleAndNotReconcilable()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path));
        await runtime.CreateAsync("first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");
        provider.PendingSend = new TaskCompletionSource<SendReceipt>();
        provider.PendingSend.SetResult(new SendReceipt(SendDisposition.Rejected,
            "provider_rate_limited") { Retryable=true, RetryAfterSeconds=60 });
        var rejected = await runtime.SendAsync("chat-1", "blocked", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
        Assert.Null(runtime.Get("chat-1")!.PendingTurn);
        Assert.Equal("not_applicable",
            (await runtime.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ManagerCanFinishRegistryBindingAfterJournalRecoveryWriteSucceeded()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), first);
            Assert.Equal(SendDisposition.Accepted,
                (await managers.CreateAsync("manager/main", "first", CancellationToken.None))
                    .Receipt.Disposition);
            await WaitForTerminal(first, "chat-1");
            provider.DeferCompletion = true;
            Assert.Equal(SendDisposition.Accepted,
                (await managers.SendAsync("manager/main", "second", CancellationToken.None))!
                    .Receipt.Disposition);
        }
        provider.AuthoritativeComplete = true;
        var fail = true;
        var registry = new ManagerRegistry(folder.Path, beforePersist: record => {
            if (fail && record.LifecycleState == "idle" &&
                record.PendingOperationId is not null)
                throw new IOException("Injected registry failure.");
        });
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var managersAgain = new ManagerService(registry, recovered);
        await Assert.ThrowsAsync<IOException>(() =>
            managersAgain.ReconcileAsync("manager/main", CancellationToken.None));
        Assert.Equal("idle", recovered.Get("chat-1")!.Status);
        Assert.Equal("uncertain", registry.Get("manager/main")!.LifecycleState);

        fail = false;
        var result = (await managersAgain.ReconcileAsync(
            "manager/main", CancellationToken.None))!;
        Assert.Equal("restored", result.Reconciliation.Outcome);
        Assert.Equal("durable_verified_turn", result.Reconciliation.Reason);
        Assert.Equal("idle", result.Manager.LifecycleState);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task CreationMarkerCanReconcileAfterPreexistingJournalCheckpoint()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            UncertainNextCreation = true, CorrelatedUncertainCreationId = "chat-1",
            AuthoritativeComplete = true
        };
        var journal = new ChatJournal(folder.Path);
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal))
        {
            Assert.Equal(SendDisposition.Uncertain,
                (await first.CreateAsync("first", CancellationToken.None)).Disposition);
            journal.Set(new ChatRecord("chat-1", "generating",
                ProgressTurnId: "original-start"));
        }
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        Assert.Equal("restored",
            (await recovered.ReconcileCreationAsync(null, CancellationToken.None)).Outcome);
        Assert.Null(new ChatJournal(folder.Path).GetPendingCreation());
        Assert.Equal("idle", recovered.Get("chat-1")!.Status);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task ProviderInspectionExceptionIsRedactedAndFailClosed()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(folder.Path)))
            await PrepareAcceptedTimeout(first, provider);
        provider.AuthoritativeThrow = true;
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var result = await recovered.ReconcileAsync("chat-1", CancellationToken.None);
        Assert.Equal("verification_unavailable", result.Reason);
        Assert.DoesNotContain("private provider error", result.ToString());
        Assert.Equal("uncertain", recovered.Get("chat-1")!.Status);
        provider.AuthoritativeThrow = false;
        provider.AuthoritativeComplete = true;
        Assert.Equal("restored",
            (await recovered.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
    }

    [Fact]
    public void ObserverExpirationIsTerminalButNotAConfirmedFailure()
    {
        var feed = new ConversationUpdateFeed();
        feed.Publish(new PublicConversationUpdate("turn", PublicUpdateKind.Final,
            PublicUpdateOperation.ObservationExpired));
        Assert.True(feed.Snapshot().IsTerminal);
        Assert.Equal("ObservationExpired", feed.Snapshot().TerminalOperation);
    }
}
