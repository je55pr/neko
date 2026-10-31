using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private sealed partial class FakeProvider
    {
        public void AddReviewerResult(string text) => _messages.Add(
            new VisibleConversationMessage(Guid.NewGuid().ToString("N"),
                VisibleMessageRole.Assistant, text));

        public void SetInitialReviewerResult(string text)
        {
            var index = _messages.FindIndex(m => m.Role == VisibleMessageRole.Assistant);
            if (index < 0) throw new InvalidOperationException("Initial reply missing.");
            _messages[index] = _messages[index] with { Text = text };
        }

        public void SetLatestReviewerResult(string text)
        {
            var index = _messages.FindLastIndex(m => m.Role == VisibleMessageRole.Assistant);
            if (index < 0) throw new InvalidOperationException("Reply missing.");
            _messages[index] = _messages[index] with { Text = text };
        }
    }

    private const string PrHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PrBase = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PrChanged = "cccccccccccccccccccccccccccccccccccccccc";
    private const string PrMerged = "dddddddddddddddddddddddddddddddddddddddd";
    private const string PrReviewUrl = "https://example.org/reviews/7";

    private static PrReviewPolicy GatePolicy() => new(
        ["build"], "review-job", "auto-account",
        TrustedCheckProducers: new Dictionary<string, string> { ["build"] = "app:100" },
        AutomatedCheckProducer: "app:200");

    private static PrSourceStatus GoodSource(DateTimeOffset now) =>
        new("sample/project", 7, PrHead, PrBase, "open", "creator-account", false, true,
        [
            new("build", PrHead, "completed", "success", "app:100"),
            new("review-job", PrHead, "completed", "success", "app:200")
        ],
        [
            new(PrReviewUrl, "independent-account", PrHead, "approved",
                true, now.AddMinutes(1), "test-review-body"),
            new("https://example.org/reviews/8", "auto-account", PrHead, "commented",
                true, now.AddMinutes(1))
        ], 0);

    private sealed class PrSourceFake(PrSourceStatus initial) : IPrSourceAdapter
    {
        public PrSourceStatus Current = initial;
        public Func<int, PrSourceStatus>? OnRead;
        public int Reads;
        public int MergeCalls;
        public int PostCalls;
        public string AuthenticatedPublisher = "independent-account";
        public bool ThrowAfterMergeDispatch;
        public bool ThrowAfterReviewDispatch;
        public void FailReviewPostingAfterDispatch() => ThrowAfterReviewDispatch = true;
        public Task<string> AuthenticatedPublisherAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(AuthenticatedPublisher);
        }

        public Task<PrReview> PostReviewAsync(string repository, int number,
            string expectedHead, string body, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (repository != Current.Repository || number != Current.Number)
                throw new ArgumentException("Unexpected test PR identity.");
            PostCalls++;
            if (expectedHead != Current.HeadSha)
                throw new InvalidOperationException("PR moved during review posting.");
            if (ThrowAfterReviewDispatch)
                throw new IOException("Review dispatch accepted but reply lost.");
            var posted = new PrReview(PrReviewUrl, "independent-account",
                expectedHead, "commented", true, DateTimeOffset.UtcNow,
                GhPrSourceAdapter.BodyDigest(body));
            Current = Current with { Reviews = [.. Current.Reviews, posted] };
            return Task.FromResult(posted);
        }

        public Task<PrSourceStatus> ReadAsync(string repository, int number,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (repository != Current.Repository || number != Current.Number)
                throw new ArgumentException("Unexpected test PR identity.");
            Reads++;
            return Task.FromResult(OnRead?.Invoke(Reads) ?? Current);
        }

        public Task<string> MergeAsync(string repository, int number, string expectedHead,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (repository != Current.Repository || number != Current.Number)
                throw new ArgumentException("Unexpected test PR identity.");
            MergeCalls++;
            if (expectedHead != Current.HeadSha)
                throw new InvalidOperationException("Source head changed during merge.");
            if (ThrowAfterMergeDispatch)
                throw new IOException("Ambiguous remote response.");
            Current = Current with { State = "closed", Merged = true };
            return Task.FromResult(PrMerged);
        }
    }

    private static PrWorkflow ReadyRecord(PrWorkflowStore store)
    {
        var created = store.Create("sample/project", 7, PrHead, PrBase,
            "creator-chat", "developer-chat", "manager-chat");
        return store.Update("sample/project", 7, workflow => store.Append(workflow with {
            State = "awaiting_automated_review", ReviewLaunchState = "accepted",
            ReviewLaunchAt = created.CreatedAt.AddMilliseconds(10),
            Reviewer = new PrWorkerEvidence("fresh-review-chat", PrHead,
                created.CreatedAt.AddMilliseconds(10), true,
                created.CreatedAt.AddMilliseconds(20), PrReviewUrl, "approved",
                "independent-account", CompletionMessageId: "worker-reply",
                ReviewPostAttemptId: "review-attempt", ReviewPostOutcome: "posted",
                PostedBodySha256: "test-review-body", ReviewerVerdict: "no_blockers",
                CreationPromptSha256: new string('A', 64))
        }, "review_verified", "test_source_proof"));
    }

    [Fact]
    public async Task SuccessfulMergeIsGuardedAuditedAndSurvivesRestart()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var now = DateTimeOffset.UtcNow;
        var source = new PrSourceFake(GoodSource(now));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        Assert.True((await service.InspectAsync("sample/project", 7, CancellationToken.None)).Gate.Ready);
        var result = await service.MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("merged", result.Outcome);
        Assert.Equal(1, source.MergeCalls);
        var persisted = new PrWorkflowStore(folder.Path).Get("sample/project", 7)!;
        Assert.Equal(PrMerged, persisted.MergedSha);
        Assert.Equal("merged", persisted.MergeOutcome);
        Assert.Equal("authenticated_host_operator", persisted.MergeAuthorizedBy);
        Assert.NotNull(persisted.LastSourceEvidence);
        Assert.Equal(PrHead, persisted.LastSourceEvidence.HeadSha);
        Assert.True(persisted.LastGateDecision!.Ready);
        Assert.NotNull(persisted.MergeAuthorizedAt);
        Assert.Contains(persisted.Audit!, e => e.Action == "merge_reserved");
        Assert.Contains(persisted.Audit!, e => e.Action == "merged");
        var restarted = new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy());
        Assert.Equal("blocked",
            (await restarted.MergeAsync("sample/project", 7, CancellationToken.None)).Outcome);
        Assert.Equal(1, source.MergeCalls);
    }

    [Fact]
    public async Task NewHeadBetweenPreflightAndDispatchStopsBeforeExternalWrite()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        source.OnRead = call => call >= 2
            ? source.Current with { HeadSha = PrChanged } : source.Current;
        var result = await new PrWorkflowService(store, source, runtime, GatePolicy())
            .MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("rejected_before_dispatch", result.Outcome);
        Assert.Equal(0, source.MergeCalls);
        Assert.Equal("rejected_before_dispatch", new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.MergeOutcome);
    }

    [Fact]
    public async Task AtomicExpectedHeadPreconditionStopsConcurrentHeadChange()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        source.OnRead = call => {
            if (call == 2) source.Current = source.Current with { HeadSha = PrChanged };
            return call == 2 ? GoodSource(DateTimeOffset.UtcNow) : source.Current;
        };
        var result = await new PrWorkflowService(store, source, runtime, GatePolicy())
            .MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("uncertain", result.Outcome);
        Assert.Equal(1, source.MergeCalls);
        Assert.NotNull(new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.MergeAttemptId);
    }

    [Fact]
    public async Task UnknownExternalMergeResultIsNeverReplayedAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow))
            { ThrowAfterMergeDispatch = true };
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var first = await new PrWorkflowService(store, source, runtime, GatePolicy())
            .MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("uncertain", first.Outcome);
        Assert.Contains(first.Workflow.Audit!, e => e.Action == "merge_uncertain");
        var again = await new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy())
            .MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("blocked", again.Outcome);
        Assert.Equal(1, source.MergeCalls);
    }

    [Fact]
    public async Task DurableReservationFailureNeverInvokesExternalMerge()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var setup = new PrWorkflowStore(folder.Path);
        ReadyRecord(setup);
        var store = new PrWorkflowStore(folder.Path,
            beforePersist: w => {
                if (w.MergeAttemptId is not null) throw new IOException("disk full");
            });
        await Assert.ThrowsAsync<IOException>(() => new PrWorkflowService(
            store, source, runtime, GatePolicy()).MergeAsync(
                "sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.MergeCalls);
        Assert.Null(new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.MergeAttemptId);
    }

    [Fact]
    public async Task ConcurrentMergeRequestsHaveOneExternalWrite()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.MergeAsync("sample/project", 7, CancellationToken.None)));
        Assert.Single(attempts, a => a.Outcome == "merged");
        Assert.Equal(1, source.MergeCalls);
    }

    [Fact]
    public async Task WorkerCannotClaimAcceptedCreationIsCompletedOrInventSourceReview()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { DeferCompletion = true };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        await service.CreateAsync("sample/project", 7, "creator", "developer",
            "manager", CancellationToken.None);
        var created = await service.LaunchReviewerAsync("sample/project", 7,
            "Inspect source and post review.", "independent-account", CancellationToken.None);
        Assert.Equal("accepted", created.ReviewLaunchState);
        Assert.Equal("generating", runtime.Get(created.Reviewer!.ConversationId)!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        provider.CompletePending();
        await WaitForTerminal(runtime, created.Reviewer.ConversationId);

        // An echoed URL cannot become source-side review provenance.
        provider.SetInitialReviewerResult("Review posted: " + PrReviewUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.PostCalls);

        provider.SetInitialReviewerResult("REVIEW_VERDICT: NO_BLOCKERS\n\n" +
            "Independent source, tests, and failure-boundary review found no blockers.");
        var finished = await service.PublishReviewAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.True(finished.Reviewer!.Completed);
        Assert.False(string.IsNullOrWhiteSpace(finished.Reviewer.CompletionMessageId));
        Assert.Equal("posted", finished.Reviewer.ReviewPostOutcome);
        Assert.Equal("no_blockers", finished.Reviewer.ReviewerVerdict);
        Assert.Equal(1, source.PostCalls);
        Assert.Contains(finished.Audit!, e => e.Action == "review_post_verified");
        Assert.True((await service.InspectAsync("sample/project", 7, CancellationToken.None))
            .Gate.Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LaunchReviewerAsync("sample/project", 7,
                "repeat", "independent-account", CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitRevisionRefreshInvalidatesOldWorkerAndPreservesAudit()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        source.Current = source.Current with { HeadSha = PrChanged };
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        var stale = await service.InspectAsync("sample/project", 7, CancellationToken.None);
        Assert.Contains("head_sha_changed", stale.Gate.Blockers);
        var fresh = await service.RefreshRevisionAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Equal(PrChanged, fresh.HeadSha);
        Assert.Null(fresh.Reviewer);
        Assert.Equal("unstarted", fresh.ReviewLaunchState);
        Assert.Contains(fresh.Audit!, e => e.Action == "revision_rearmed" &&
            e.Detail.Contains(PrHead));
        var restarted = new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy());
        var inspected = await restarted.InspectAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.False(inspected.Gate.Ready);
        Assert.Contains("independent_reviewer_missing", inspected.Gate.Blockers);
    }

    [Fact]
    public async Task UncertainReviewerCreationCannotBeResetByHeadRefresh()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        store.Update("sample/project", 7, record => record with {
            ReviewLaunchState = "uncertain", Reviewer = null
        });
        source.Current = source.Current with { HeadSha = PrChanged };
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RefreshRevisionAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(PrHead, new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.HeadSha);
    }

}
