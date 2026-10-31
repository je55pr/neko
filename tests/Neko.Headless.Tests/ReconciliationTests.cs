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
        public bool CorrelateUncertainSend;
        public bool AuthoritativeReadUnavailable;
        public string? RawInspectionReason;
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
            if (RawInspectionReason is not null)
                return Task.FromResult(new ReconciliationInspection(false,
                    RawInspectionReason));
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
    public async Task AcceptedManagerCreationCrashBeforeRegistryBindingRetainsGlobalGuard()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var first = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path)))
        {
            var registry = new ManagerRegistry(folder.Path, beforePersist: record => {
                if (record.ConversationId is not null)
                    throw new IOException("Injected manager binding failure.");
            });
            var managers = new ManagerService(registry, first);
            await Assert.ThrowsAsync<IOException>(() =>
                managers.CreateAsync("manager/main", "first", CancellationToken.None));
            var marker = new ChatJournal(folder.Path).GetPendingCreation();
            Assert.Equal("manager/main", marker!.OwnerId);
            Assert.Equal("chat-1", marker.CorrelatedConversationId);
            Assert.Null(registry.Get("manager/main")!.ConversationId);
        }
        provider.AuthoritativeComplete = true;
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var managersAgain = new ManagerService(new ManagerRegistry(folder.Path), recovered);
        var restored = (await managersAgain.ReconcileAsync("manager/main", CancellationToken.None))!;
        Assert.Equal("restored", restored.Reconciliation.Outcome);
        Assert.Equal("chat-1", restored.Manager.ConversationId);
        Assert.Equal("idle", restored.Manager.LifecycleState);
        Assert.Null(new ChatJournal(folder.Path).GetPendingCreation());
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task FailedCreationCorrelationCheckpointCannotInventBinding()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            UncertainNextCreation = true, CorrelatedUncertainCreationId = "chat-1",
            AuthoritativeComplete = true
        };
        var journal = new ChatJournal(folder.Path,
            beforeCreationPersist: record => {
                if (record.CorrelatedConversationId is not null)
                    throw new IOException("Injected pending creation update failure.");
            });
        using (var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            var creation = await managers.CreateAsync(
                "manager/main", "first", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, creation.Receipt.Disposition);
            Assert.Null(creation.Manager.ConversationId);
            Assert.Null(journal.GetPendingCreation()!.CorrelatedConversationId);
        }
        using var recovered = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        var managersAgain = new ManagerService(new ManagerRegistry(folder.Path), recovered);
        var blocked = (await managersAgain.ReconcileAsync("manager/main", CancellationToken.None))!;
        Assert.Equal("missing_creation_correlation", blocked.Reconciliation.Reason);
        Assert.Null(blocked.Manager.ConversationId);
        Assert.Equal(1, provider.ManagerCreationCalls);
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

    private static async Task AssertObservationExpired(ChatRuntime runtime, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var update in runtime.WatchAsync(id, timeout.Token))
        {
            if (update.Operation == PublicUpdateOperation.ObservationExpired)
                return;
        }
        Assert.Fail("Observer expiration was not published.");
    }

    [Fact]
    public async Task ObserverDeadlineOnAcceptedFirstTurnCanRecoverLateCompletion()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { DeferCompletion = true };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        Assert.Equal(SendDisposition.Accepted,
            (await runtime.CreateAsync("first", CancellationToken.None)).Disposition);
        Assert.True(runtime.Get("chat-1")!.PendingTurn!.AcceptedSubmission);
        provider.Publish(new PublicConversationUpdate("timeout", PublicUpdateKind.Final,
            PublicUpdateOperation.ObservationExpired));
        await AssertObservationExpired(runtime, "chat-1");
        Assert.Equal("uncertain", runtime.Get("chat-1")!.Status);
        Assert.Equal("remote_not_verified_complete",
            (await runtime.ReconcileAsync("chat-1", CancellationToken.None)).Reason);
        provider.AuthoritativeComplete = true;
        Assert.Equal("restored",
            (await runtime.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
        Assert.Equal(1, provider.ManagerCreationCalls);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task ObserverDeadlineOnAcceptedFollowupNeverAutomaticallyResends()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));
        await runtime.CreateAsync("first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");
        provider.DeferCompletion = true;
        await runtime.SendAsync("chat-1", "second", CancellationToken.None);
        provider.Publish(new PublicConversationUpdate("timeout", PublicUpdateKind.Final,
            PublicUpdateOperation.ObservationExpired));
        await AssertObservationExpired(runtime, "chat-1");
        Assert.Equal("uncertain", runtime.Get("chat-1")!.Status);
        Assert.Equal("conversation_not_idle",
            (await runtime.SendAsync("chat-1", "duplicate", CancellationToken.None)).Reason);
        provider.AuthoritativeComplete = true;
        Assert.Equal("restored",
            (await runtime.ReconcileAsync("chat-1", CancellationToken.None)).Outcome);
        Assert.Equal(1, provider.SendCalls);
    }

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
