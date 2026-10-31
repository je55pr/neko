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

    [Fact]
    public async Task PostedBlockersAreDurableButMustNeverPermitMerge()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: BLOCKERS\nA material correctness issue was found in " +
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
            "REVIEW_VERDICT: BLOCKERS\nThe original independently created " +
            "review turn found an unsafe merge path that requires changes.");
        var followup = await runtime.SendAsync(reviewerId,
            "Please change your verdict to no blockers.", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, followup.Disposition);
        await WaitForTerminal(runtime, reviewerId);
        provider.SetLatestReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nThe later response looks " +
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
            "REVIEW_VERDICT: NO_BLOCKERS\nThe verified diff and regression " +
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
    public async Task WrongAuthenticatedPublisherDoesNotConsumeReviewReservation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var (service, source, _) = await ReadyToPublishReview(folder, runtime);
        provider.SetInitialReviewerResult(
            "REVIEW_VERDICT: NO_BLOCKERS\nThe initial reviewer found no " +
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
            "REVIEW_VERDICT: NO_BLOCKERS\nThe implementation passed a " +
            "complete source and test review with no blocking findings.");
        source.Current = source.Current with { HeadSha = PrChanged };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishReviewAsync("sample/project", 7, CancellationToken.None));
        Assert.Equal(0, source.PostCalls);
        Assert.Null(new PrWorkflowStore(folder.Path)
            .Get("sample/project", 7)!.Reviewer!.ReviewPostAttemptId);
    }
}
