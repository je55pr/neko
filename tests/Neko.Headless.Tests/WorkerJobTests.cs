using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private static void SeedAcceptedWorker(ChatJournal journal, string id = "worker-2",
        string status = "generating")
    {
        journal.Set(new ChatRecord(id, status,
            ProgressTurnId: "turn-2", DispatchCount: 1,
            PendingTurn: new PendingTurnEvidence("prompt-2", [],
                AcceptedSubmission: true, OperationId: "op-2")));
    }

    private static void CompleteWorker(ChatJournal journal, string id = "worker-2")
    {
        journal.Update(id, old => old with {
            Status = "idle", PendingTurn = null,
            LastReconciliation = new ReconciliationDecision(
                DateTimeOffset.UtcNow, "restored", "provider_terminal_observed",
                "prompt-2", AssistantMessageId: "assistant-2", OperationId: "op-2",
                TurnId: "turn-2", DispatchCount: 1)
        });
    }

    private static (WorkerJobService Jobs, WorkerJobJournal Journal,
        ManagerInboxService Inbox, ManagerInboxJournal InboxJournal,
        ManagerRegistry Registry) SetupJobs(string folder, ChatRuntime runtime,
        ManagerService managers, TimeProvider clock, PrSourceFake? source = null,
        WorkerJobJournal? jobJournal = null, PrReviewPolicy? policyOverride = null)
    {
        var registry = new ManagerRegistry(folder, clock);
        var inboxJournal = new ManagerInboxJournal(folder, clock);
        var inbox = new ManagerInboxService(inboxJournal, managers, runtime, clock);
        var prSource = source ?? new PrSourceFake(GoodSource(clock.GetUtcNow()));
        var policy = policyOverride ?? GatePolicy();
        var workflows = new PrWorkflowService(new PrWorkflowStore(folder, clock),
            prSource, runtime, policy, clock);
        var journal = jobJournal ?? new WorkerJobJournal(folder, clock);
        return (new WorkerJobService(journal, registry, managers, inbox,
            runtime, workflows, policy, clock), journal, inbox, inboxJournal, registry);
    }

    [Fact]
    public void FailedNotificationBacklogCannotStarveOtherWorkerObservations()
    {
        var now = DateTimeOffset.UtcNow;
        var stalled = Enumerable.Range(1, 90).Select(i => new WorkerJob(
            i.ToString("x32"), "manager/failing", "worker-" + i, "turn", 1,
            "operation", null, null, null, null, "attention_required", now, now,
            null, 0, null,
            [new WorkerJobEvent("pending-" + i, "worker_uncertain",
                "notification not yet enqueued", now)])).ToArray();
        var active = new WorkerJob(new string('f', 32), "manager/healthy",
            "worker-healthy", "turn", 1, "operation", null, null, null, null,
            "running", now, now, now.AddSeconds(-1), 0, null, []);
        var all = stalled.Append(active).ToArray();
        var flushed = new HashSet<string>(StringComparer.Ordinal);
        for (var scan = 1; scan <= 100; scan++)
        {
            var selected = WorkerJobService.SelectJobs(all, now, scan);
            Assert.InRange(selected.Count, 1, 64);
            Assert.Contains(selected, job => job.Id == active.Id);
            foreach (var job in selected) flushed.Add(job.Id);
        }
        Assert.Equal(all.Length, flushed.Count);
    }

    [Fact]
    public async Task CompletedAcceptedWorkerWakesIdleManagerOnceAcrossRestart()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chatJournal = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chatJournal);
        var setup = SetupJobs(folder.Path, runtime, managers, clock);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;

        var request = new WorkerJobRegisterRequest("worker-2", "op-2");
        var (created, error) = jobs.Register("manager/main", request);
        Assert.Null(error);
        Assert.NotNull(created);
        var (duplicate, secondError) = jobs.Register("manager/main", request);
        Assert.Null(secondError);
        Assert.True(duplicate!.Duplicate);
        Assert.Equal(created!.Job.Id, duplicate.Job.Id);
        Assert.Equal([created.Job.Id],
            setup.Registry.Get("manager/main")!.OutstandingJobReferences);

        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Empty(setup.InboxJournal.List("manager/main"));
        CompleteWorker(chatJournal);
        clock.AdvanceSeconds(11);
        await Task.WhenAll(
            jobs.ProcessAllAsync(CancellationToken.None),
            jobs.ProcessAllAsync(CancellationToken.None));
        var finished = jobs.Get("manager/main", created.Job.Id)!;
        Assert.Equal("completed", finished.State);
        Assert.Equal("assistant-2", finished.Terminal!.AssistantMessageId);
        Assert.Single(finished.Events);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Empty(setup.Registry.Get("manager/main")!.OutstandingJobReferences);
        Assert.Equal(0, provider.SendCalls);

        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));
        Assert.Equal(1, provider.SendCalls);
        await WaitForTerminal(runtime, "chat-1");
        Assert.True(await inbox.ProcessAsync("manager/main", CancellationToken.None));

        using var restarted = SetupJobs(folder.Path, runtime, managers, clock).Jobs;
        clock.AdvanceSeconds(120);
        await restarted.ProcessAllAsync(CancellationToken.None);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Single(restarted.Get("manager/main", created.Job.Id)!.Events);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task UnverifiedAndReplacedWorkerTurnsCannotCreateSuccessEvidence()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var journal = new ChatJournal(folder.Path);
        SeedAcceptedWorker(journal);
        var setup = SetupJobs(folder.Path, runtime, managers, clock);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;

        Assert.Equal("worker_operation_mismatch",
            jobs.Register("manager/main",
                new WorkerJobRegisterRequest("worker-2", "wrong")).Error);
        Assert.Equal("invalid_job_binding",
            jobs.Register("manager/main",
                new WorkerJobRegisterRequest("chat-1")).Error);
        var registered = jobs.Register("manager/main",
            new WorkerJobRegisterRequest("worker-2", "op-2")).Result!.Job;
        // The original turn was never proven terminal, then a different
        // operation took over this worker conversation.
        journal.Update("worker-2", old => old with {
            DispatchCount = 2, ProgressTurnId = "another-turn"
        });
        await jobs.ProcessAllAsync(CancellationToken.None);
        var observed = jobs.Get("manager/main", registered.Id)!;
        Assert.Equal("attention_required", observed.State);
        Assert.Null(observed.Terminal);
        Assert.Single(observed.Events);
        Assert.Equal("worker_binding_lost", observed.Events[0].Kind);
        Assert.Equal(0, provider.SendCalls);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Contains(registered.Id,
            setup.Registry.Get("manager/main")!.OutstandingJobReferences);
    }

    [Fact]
    public async Task StaleRestoredDecisionCannotCertifyDifferentRejectedTurn()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        using var runtime = InboxRuntime(folder.Path, new FakeProvider());
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        CompleteWorker(chat);
        // A rejected later send can leave its new turn token and dispatch
        // count alongside the previous completion decision. That old proof
        // must never certify the replacement turn as accepted or finished.
        chat.Update("worker-2", old => old with {
            ProgressTurnId = "rejected-turn", DispatchCount = 2
        });
        var setup = SetupJobs(folder.Path, runtime, managers, clock);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        Assert.Equal("worker_acceptance_unverified",
            jobs.Register("manager/main",
                new WorkerJobRegisterRequest("worker-2")).Error);
        Assert.Empty(setup.Journal.List("manager/main"));
        Assert.Empty(setup.InboxJournal.List("manager/main"));
    }

    [Fact]
    public async Task InterruptedInboxCheckpointReplaysOnlyItsStableEnqueueKey()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        var failing = false;
        var jobJournal = new WorkerJobJournal(folder.Path, clock, record => {
            if (failing && record.Events.Any(e => e.InboxMessageId is not null))
                throw new IOException("Simulated event acknowledgement disk failure.");
        });
        var setup = SetupJobs(folder.Path, runtime, managers, clock,
            jobJournal: jobJournal);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        var job = jobs.Register("manager/main",
            new WorkerJobRegisterRequest("worker-2")).Result!.Job;
        CompleteWorker(chat);
        failing = true;
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Null(jobs.Get("manager/main", job.Id)!.Events[0].InboxMessageId);

        failing = false;
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.NotNull(jobs.Get("manager/main", job.Id)!.Events[0].InboxMessageId);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task SourceFailuresBackoffThenCheckAndReviewFindingsAreDeduplicated()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        new PrWorkflowStore(folder.Path, clock).Create("sample/project", 7,
            PrHead, PrBase, "creator", "worker-2", "chat-1");
        var status = GoodSource(clock.GetUtcNow());
        status = status with {
            Checks = [new PrCheck("build", PrHead, "completed", "failure",
                "app:100", RunId: 42),
                new PrCheck("review-job", PrHead, "completed", "success", "app:200")],
            Reviews = [.. status.Reviews,
                new PrReview("https://example.org/reviews/99", "independent",
                    PrHead, "changes_requested", true, clock.GetUtcNow())]
        };
        var source = new PrSourceFake(status) {
            OnRead = calls => calls == 1
                ? throw new IOException("HTTP 429")
                : status
        };
        var setup = SetupJobs(folder.Path, runtime, managers, clock, source);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        var (registration, error) = jobs.Register("manager/main",
            new WorkerJobRegisterRequest("worker-2", "op-2",
                "sample/project", 7));
        Assert.Null(error);
        var id = registration!.Job.Id;

        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, jobs.Get("manager/main", id)!.ExternalFailures);
        clock.AdvanceSeconds(30);
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Equal(1, source.Reads);
        clock.AdvanceSeconds(100);
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Equal(2, source.Reads);
        var events = jobs.Get("manager/main", id)!.Events;
        Assert.True(events.Count == 2, string.Join(", ", events.Select(e => e.Kind)));
        Assert.Contains(events, e => e.Kind == "check_failed");
        Assert.Contains(events, e => e.Kind == "review_findings");
        Assert.Equal(2, setup.InboxJournal.List("manager/main").Count);
        clock.AdvanceSeconds(180);
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Equal(2, setup.InboxJournal.List("manager/main").Count);
        Assert.Equal(0, source.PostCalls);
        Assert.Equal(0, source.MergeCalls);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task MarkerFreeAutomatedFindingStillNotifiesIdleManagerOnce()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        new PrWorkflowStore(folder.Path, clock).Create("sample/project", 7,
            PrHead, PrBase, "creator", "worker-2", "chat-1");
        var initial = GoodSource(clock.GetUtcNow());
        var botFinding = new PrReview("https://example.org/reviews/bot-finding",
            "auto-account", PrHead, "commented", true, clock.GetUtcNow(),
            SourceReviewId: 501, AutomatedNoFindings: false);
        var current = initial with { Reviews =
            [.. initial.Reviews.Where(r => r.Author != "auto-account"), botFinding] };
        var source = new PrSourceFake(current);
        var policy = GatePolicy() with {
            AutomatedNoFindingsMarker = null,
            AllowVerifiedEmptyAutomatedReview = true
        };
        var setup = SetupJobs(folder.Path, runtime, managers, clock,
            source: source, policyOverride: policy);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        var (registered, error) = jobs.Register("manager/main",
            new WorkerJobRegisterRequest("worker-2", "op-2", "sample/project", 7));
        Assert.Null(error);
        Assert.NotNull(registered);
        await jobs.ProcessAllAsync(CancellationToken.None);
        var job = jobs.Get("manager/main", registered.Job.Id)!;
        Assert.Single(job.Events, e => e.Kind == "review_findings" &&
            e.ReviewUrl == botFinding.Url);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        clock.AdvanceSeconds(180);
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Single(jobs.Get("manager/main", job.Id)!.Events);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal(0, source.PostCalls);
        Assert.Equal(0, source.MergeCalls);
    }

    [Fact]
    public async Task StandaloneBotInlineFindingWakesManagerWithoutFakeApproval()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        new PrWorkflowStore(folder.Path, clock).Create("sample/project", 7,
            PrHead, PrBase, "creator", "worker-2", "chat-1");
        const string findingUrl = "https://github.com/example/pull/7#comment-44";
        var baseline = GoodSource(clock.GetUtcNow());
        var bot = new PrReview("https://github.com/example/pull/7#review",
            "auto-account", PrHead, "commented", false, clock.GetUtcNow(),
            SourceReviewId: 123, AutomatedNoFindings: false);
        var source = new PrSourceFake(baseline with {
            Reviews = [.. baseline.Reviews.Where(r => r.Author != "auto-account"), bot],
            InlineFindings = [new PrInlineFinding(findingUrl, "auto-account", PrHead, 44)]
        });
        var policy = GatePolicy() with {
            AutomatedNoFindingsMarker = null, AllowVerifiedEmptyAutomatedReview = true
        };
        var setup = SetupJobs(folder.Path, runtime, managers, clock,
            source: source, policyOverride: policy);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        var (registered, error) = jobs.Register("manager/main",
            new WorkerJobRegisterRequest("worker-2", "op-2", "sample/project", 7));
        Assert.Null(error);
        Assert.NotNull(registered);
        await jobs.ProcessAllAsync(CancellationToken.None);
        var events = jobs.Get("manager/main", registered.Job.Id)!.Events;
        Assert.Single(events, e =>
            e.Kind == "review_findings" && e.ReviewUrl == findingUrl);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        clock.AdvanceSeconds(180);
        await jobs.ProcessAllAsync(CancellationToken.None);
        Assert.Single(jobs.Get("manager/main", registered.Job.Id)!.Events);
        Assert.Single(setup.InboxJournal.List("manager/main"));
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal(0, source.PostCalls);
        Assert.Equal(0, source.MergeCalls);
    }

    [Fact]
    public async Task ConcurrentHumanEnqueueAndCompletionKeepDistinctOrderedMessages()
    {
        using var folder = new TemporaryDirectory();
        var clock = new InboxClock();
        var provider = new FakeProvider();
        using var runtime = InboxRuntime(folder.Path, provider);
        var managers = await CreateIdleManager(runtime, folder.Path);
        var chat = new ChatJournal(folder.Path);
        SeedAcceptedWorker(chat);
        CompleteWorker(chat);
        var setup = SetupJobs(folder.Path, runtime, managers, clock);
        using var jobs = setup.Jobs;
        using var inbox = setup.Inbox;
        jobs.Register("manager/main", new WorkerJobRegisterRequest("worker-2"));
        await Task.WhenAll(
            Task.Run(() => inbox.Enqueue("manager/main", "Human message", "human.one")),
            jobs.ProcessAllAsync(CancellationToken.None));
        var messages = setup.InboxJournal.List("manager/main");
        Assert.Equal(2, messages.Count);
        Assert.Equal([1L, 2L], messages.Select(m => m.Sequence));
        Assert.Equal(2, messages.Select(m => m.IdempotencyKey).Distinct().Count());
        Assert.Equal(0, provider.SendCalls);
    }
}
