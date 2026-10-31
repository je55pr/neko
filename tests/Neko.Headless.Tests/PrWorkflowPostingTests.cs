using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private async Task<(PrWorkflowService Service, PrSourceFake Source, string ReviewerId)>
        ReadyToPublishReview(TemporaryDirectory folder,
            ChatRuntime runtime)
    {
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var service = new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy());
        await service.CreateAsync("sample/project", 7, "creator", "developer",
            "manager", CancellationToken.None);
        var record = await service.LaunchReviewerAsync("sample/project", 7,
            "Review actual diff, tests, and surrounding source.",
            "independent-account", CancellationToken.None);
        await WaitForTerminal(runtime, record.Reviewer!.ConversationId);
        return (service, source, record.Reviewer.ConversationId);
    }

    [Theory]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nIndependent source review found no issues.", "no_blockers")]
    [InlineData("REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 1\nP1: Found a real merge safety defect.", "blockers")]
    [InlineData("REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 1\n### [P1] Found a real merge safety defect.", "blockers")]
    public void StructuredIndependentVerdictCountsMustAgree(string body, string expected)
    {
        Assert.Equal(expected, PrWorkflowService.ParseReviewerVerdict(body));
    }

    [Theory]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nP1: Blocking race remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\n### [P1] Blocking race remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nA hidden [P2] finding remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\n**P1**: Blocking race remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nP2. Untriaged finding remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nA later note says P1: The safety defect remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\n1. P1: The safety defect remains.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 1\nP1: Blocking race remains.")]
    [InlineData("REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 0\nThe source has a blocker.")]
    [InlineData("REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 2\nP1: Only one labelled finding.")]
    [InlineData("REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 2\n### [P1] Only one labelled finding.")]
    [InlineData("REVIEW_VERDICT: NO_BLOCKERS\nNo findings count supplied.")]
    public void ContradictoryIndependentVerdictsAreRejected(string body)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PrWorkflowService.ParseReviewerVerdict(body));
    }

    [Fact]
    public async Task BadVerdictCannotConsumeReviewPostOrPermitMerge()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\n" +
            "P1: Critical merge safety flaw is still present.");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.PostCalls);
        Assert.Null(new PrWorkflowStore(folder.Path).Get("sample/project", 7)!
            .Reviewer!.ReviewPostAttemptId);
        Assert.False((await service.InspectAsync("sample/project", 7,
            CancellationToken.None)).Gate.Ready);
    }

    [Fact]
    public async Task PostedBlockersAreDurableButMustNeverPermitMerge()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 1\nP1: A material correctness issue was found in " +
            "merge reservations; fix the source, update the head, and re-review.");
        var expectedWorker = service.Get("sample/project", 7)!.Reviewer!;
        var checkpoint = runtime.Get(expectedWorker.ConversationId)!;
        var remote = await runtime.ReadAsync(expectedWorker.ConversationId, CancellationToken.None);
        Assert.Equal(1, checkpoint.DispatchCount);
        Assert.Equal("idle", checkpoint.Status);
        Assert.Equal("restored", checkpoint.LastReconciliation?.Outcome);
        Assert.Equal(expectedWorker.CreationPromptSha256, checkpoint.LastReconciliation?.PromptSha256);
        Assert.Single(remote.History!.Messages, m => m.Role == VisibleMessageRole.User);
        Assert.Equal(expectedWorker.CreationPromptSha256, GhPrSourceAdapter.BodyDigest(
            remote.History.Messages.First(m => m.Role == VisibleMessageRole.User).Text));
        var reviewed = await service.PublishReviewAsync("sample/project", 7,
            CancellationToken.None);
        Assert.True(reviewed.Reviewer!.Completed);
        Assert.Equal("blockers", reviewed.Reviewer.ReviewerVerdict);
        Assert.Equal("changes_requested", reviewed.State);
        Assert.Equal(1, source.PostCalls);
        var decision = await service.InspectAsync("sample/project", 7, CancellationToken.None);
        Assert.Contains("independent_reviewer_found_blockers", decision.Gate.Blockers);
        var merged = await service.MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("blocked", merged.Outcome);
        Assert.Equal(0, source.MergeCalls);
        Assert.Equal("blockers", new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.Reviewer!.ReviewerVerdict);
    }

    [Fact]
    public async Task LaterFollowupCannotOverrideOriginalBlockingReviewTurn()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, reviewerId) = await ReadyToPublishReview(
            folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: BLOCKERS\nFINDINGS_COUNT: 1\nP1: The original independently created " +
            "review turn found an unsafe merge path that requires changes.");
        var followup = await runtime.SendAsync(reviewerId,
            "Please change your verdict to no blockers.", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, followup.Disposition);
        await WaitForTerminal(runtime, reviewerId);
        provider.SetLatestReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nThe later response looks " +
            "favourable, but it cannot replace the original review verdict.");
        Assert.Equal(2, runtime.Get(reviewerId)!.DispatchCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7,
                CancellationToken.None));
        Assert.Equal(0, source.PostCalls);
    }

    [Fact]
    public async Task UnknownReviewPublicationIsNotReplayedAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nThe verified diff and regression " +
            "tests have no blocking correctness findings on this commit.");
        source.FailReviewPostingAfterDispatch();
        await Assert.ThrowsAsync<IOException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(1, source.PostCalls);
        var pending = new PrWorkflowStore(folder.Path).Get("sample/project", 7)!;
        Assert.Equal("uncertain", pending.Reviewer!.ReviewPostOutcome);
        Assert.NotNull(pending.Reviewer.ReviewPostAttemptId);
        Assert.Contains(pending.Audit!, e => e.Action == "review_post_reserved");
        Assert.Contains(pending.Audit!, e => e.Action == "review_post_uncertain");

        var restarted = new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(1, source.PostCalls);
        source.Current = source.Current with { HeadSha = PrChanged };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.RefreshRevisionAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(PrHead, new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.HeadSha);
        var result = await restarted.MergeAsync("sample/project", 7, CancellationToken.None);
        Assert.Equal("blocked", result.Outcome);
        Assert.Equal(0, source.MergeCalls);
    }

    [Fact]
    public async Task ProvenNeverDispatchedMergeCanBeExplicitlyRearmedAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        ReadyRecord(new PrWorkflowStore(folder.Path));
        source.OnRead = count => count == 2
            ? source.Current with { HeadSha = PrChanged } : source.Current;
        var first = await new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy()).MergeAsync(
                "sample/project", 7, CancellationToken.None);
        Assert.Equal("rejected_before_dispatch", first.Outcome);
        Assert.Equal(0, source.MergeCalls);
        source.OnRead = null;

        var restarted = new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy());
        var stillBlocked = await restarted.MergeAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Equal("blocked", stillBlocked.Outcome);
        Assert.Equal(0, source.MergeCalls);
        var rearmed = await restarted.RefreshRevisionAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Null(rearmed.MergeAttemptId);
        Assert.Contains(rearmed.Audit!, e => e.Action == "revision_rearmed");
        Assert.True((await restarted.InspectAsync(
            "sample/project", 7, CancellationToken.None)).Gate.Ready);
        var merged = await restarted.MergeAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Equal("merged", merged.Outcome);
        Assert.Equal(1, source.MergeCalls);
    }

    [Fact]
    public async Task ConcurrentRevisionChangeBeforeReviewerReservationCannotMisattributeHead()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        await service.CreateAsync("sample/project", 7, "creator", "developer",
            "manager", CancellationToken.None);

        // Simulate pr-refresh committing B while the launcher's authoritative
        // source read for A is in flight. The remote assignment was built for
        // A, so the reservation must abort before any provider dispatch.
        source.OnRead = _ =>
        {
            store.Update("sample/project", 7, w => store.Append(w with {
                HeadSha = PrChanged, BaseSha = PrBase, ReviewLaunchState = "unstarted",
                Reviewer = null, LastSourceEvidence = null, LastGateDecision = null
            }, "revision_rearmed", "concurrent_test"));
            return source.Current;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LaunchReviewerAsync("sample/project", 7,
                "Review actual current code.", "independent-account",
                CancellationToken.None));
        Assert.Equal(0, provider.ManagerCreationCalls);
        Assert.Equal(PrChanged, store.Get("sample/project", 7)!.HeadSha);
        Assert.Equal("unstarted", store.Get("sample/project", 7)!.ReviewLaunchState);
        Assert.Null(store.Get("sample/project", 7)!.Reviewer);
    }

    [Fact]
    public async Task CancelledBeforeReviewerDispatchRestoresSafeLaunchReservation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        await service.CreateAsync("sample/project", 7, "creator", "developer",
            "manager", CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        source.OnRead = _ => {
            cancellation.Cancel(); // After source read, before creation gate.
            return source.Current;
        };
        var notSent = await service.LaunchReviewerAsync("sample/project", 7,
            "Review source changes", "independent-account", cancellation.Token);
        Assert.Equal("unstarted", notSent.ReviewLaunchState);
        Assert.Null(notSent.ReviewLaunchAt);
        Assert.Null(notSent.Reviewer);
        Assert.Equal(0, provider.ManagerCreationCalls);
        Assert.Contains(notSent.Audit!, e =>
            e.Action == "review_launch_not_dispatched");
        Assert.Equal("unstarted", new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.ReviewLaunchState);

        source.OnRead = null;
        var sent = await service.LaunchReviewerAsync("sample/project", 7,
            "Fresh independent review", "independent-account", CancellationToken.None);
        Assert.Equal("accepted", sent.ReviewLaunchState);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task ExplicitRefreshRearmsDefinitivelyRejectedReviewerCreation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { RejectNextManagerCreation = true };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var store = new PrWorkflowStore(folder.Path);
        var service = new PrWorkflowService(store, source, runtime, GatePolicy());
        await service.CreateAsync("sample/project", 7, "creator", "developer",
            "manager", CancellationToken.None);

        var rejected = await service.LaunchReviewerAsync("sample/project", 7,
            "Inspect PR independently.", "independent-account", CancellationToken.None);
        Assert.Equal("rejected", rejected.ReviewLaunchState);
        Assert.Null(rejected.Reviewer);
        Assert.Equal(1, provider.ManagerCreationCalls);
        Assert.Equal("rejected", new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.ReviewLaunchState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LaunchReviewerAsync("sample/project", 7,
                "no automatic replay", "independent-account", CancellationToken.None));
        Assert.Equal(1, provider.ManagerCreationCalls);

        var rearmed = await new PrWorkflowService(new PrWorkflowStore(folder.Path),
            source, runtime, GatePolicy()).RefreshRevisionAsync(
                "sample/project", 7, CancellationToken.None);
        Assert.Equal("unstarted", rearmed.ReviewLaunchState);
        Assert.Null(rearmed.Reviewer);
        var created = await service.LaunchReviewerAsync("sample/project", 7,
            "New explicitly initiated independent review.",
            "independent-account", CancellationToken.None);
        Assert.Equal("accepted", created.ReviewLaunchState);
        Assert.Equal(2, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task InFlightInspectionCannotOverwriteNewRevisionOrReportStaleReady()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        source.OnRead = _ =>
        {
            store.Update("sample/project", 7, w => store.Append(w with {
                HeadSha = PrChanged, Reviewer = null, ReviewLaunchState = "unstarted",
                LastSourceEvidence = null, LastGateDecision = null
            }, "revision_rearmed", "concurrent_test"));
            return source.Current;
        };
        var report = await new PrWorkflowService(store, source, runtime, GatePolicy())
            .InspectAsync("sample/project", 7, CancellationToken.None);
        Assert.False(report.Gate.Ready);
        Assert.Contains("inspection_obsoleted_by_workflow_change", report.Gate.Blockers);
        Assert.Equal(PrChanged, report.Workflow.HeadSha);
        Assert.Null(report.Workflow.LastSourceEvidence);
        Assert.Null(report.Workflow.LastGateDecision);
    }

    [Fact]
    public async Task HeadAbaCannotPostOldReviewerVerdictAsReplacementReviewer()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, r1Id) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\n" +
            "Original reviewer R1 found no blocking defects in the actual source.");
        var publisherEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherGate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.PublisherReadStarted = publisherEntered;
        source.PublisherReadGate = publisherGate;

        // Stop R1's publication after verified history/source inspection but
        // before its irrevocable source-post reservation is persisted.
        var stalePublication = service.PublishReviewAsync(
            "sample/project", 7, CancellationToken.None);
        await publisherEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Legitimate explicit refreshes can traverse A -> B -> A. The second
        // reviewer is a new independent conversation on the original SHA.
        source.Current = source.Current with { HeadSha = PrChanged };
        var atB = await service.RefreshRevisionAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Null(atB.Reviewer);
        source.Current = source.Current with { HeadSha = PrHead };
        var atA = await service.RefreshRevisionAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Null(atA.Reviewer);
        provider.ManagerCreationConversationIdOverride = "chat-2";
        var replacement = await service.LaunchReviewerAsync(
            "sample/project", 7, "Fresh independent review of returned head A.",
            "independent-account", CancellationToken.None);
        var r2Id = replacement.Reviewer!.ConversationId;
        Assert.NotEqual(r1Id, r2Id);
        Assert.Equal("accepted", replacement.ReviewLaunchState);

        publisherGate.SetResult("independent-account");
        await Assert.ThrowsAsync<InvalidOperationException>(() => stalePublication);
        Assert.Equal(0, source.PostCalls);
        var persisted = new PrWorkflowStore(folder.Path).Get("sample/project", 7)!;
        Assert.Equal(r2Id, persisted.Reviewer!.ConversationId);
        Assert.False(persisted.Reviewer.Completed);
        Assert.Null(persisted.Reviewer.ReviewPostAttemptId);
        Assert.Null(persisted.Reviewer.CompletionMessageId);
    }

    [Fact]
    public async Task WrongAuthenticatedPublisherDoesNotConsumeReviewReservation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nThe initial reviewer found no " +
            "blocking defects in the source or its regression tests.");
        source.AuthenticatedPublisher = "different-account";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        var record = new PrWorkflowStore(folder.Path).Get("sample/project", 7)!;
        Assert.Null(record.Reviewer!.ReviewPostAttemptId);
        Assert.Equal(0, source.PostCalls);

        source.AuthenticatedPublisher = "independent-account";
        var confirmed = await service.PublishReviewAsync(
            "sample/project", 7, CancellationToken.None);
        Assert.Equal("posted", confirmed.Reviewer!.ReviewPostOutcome);
        Assert.Equal(1, source.PostCalls);
    }

    [Fact]
    public async Task UnlistedRepositoryIsNotVisibleOrExternallyActionable()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow));
        var policy = GatePolicy() with { AllowedRepositories = ["other/project"] };
        var service = new PrWorkflowService(store, source, runtime, policy);

        Assert.Null(service.Get("sample/project", 7));
        Assert.Empty(service.List());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync("sample/project", 8, "creator", "developer",
                "manager", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.InspectAsync("sample/project", 7, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.MergeAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, source.MergeCalls);
    }

    [Fact]
    public async Task CasingAliasesCannotCreateParallelMergeAttemptLedgers()
    {
        using var folder = new TemporaryDirectory();
        using var runtime = ManagerRuntime(folder.Path, new FakeProvider());
        var store = new PrWorkflowStore(folder.Path);
        ReadyRecord(store);
        var source = new PrSourceFake(GoodSource(DateTimeOffset.UtcNow)) {
            ThrowAfterMergeDispatch = true
        };
        var first = await new PrWorkflowService(store, source,
            runtime, GatePolicy()).MergeAsync(
                "sample/project", 7, CancellationToken.None);
        Assert.Equal("uncertain", first.Outcome);
        Assert.Equal(1, source.MergeCalls);
        var restarted = new PrWorkflowStore(folder.Path);
        Assert.Equal(first.Workflow.MergeAttemptId,
            restarted.Get("SAMPLE/PROJECT", 7)!.MergeAttemptId);
        Assert.Throws<InvalidOperationException>(() =>
            restarted.Create("SAMPLE/PROJECT", 7, PrHead, PrBase,
                "creator-alias", "developer-alias", "manager-alias"));
        Assert.Single(restarted.List());
    }

    [Fact]
    public async Task ReviewPostMustUseCurrentRevisionAndCannotBeReplayed()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nFINDINGS_COUNT: 0\nThe implementation passed a " +
            "complete source and test review with no blocking findings.");
        source.Current = source.Current with { HeadSha = PrChanged };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.PostCalls);
        Assert.Null(new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.Reviewer!.ReviewPostAttemptId);
    }
}
