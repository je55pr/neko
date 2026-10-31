using Neko;

namespace Neko.Core.Tests;

public sealed class PrReviewGateTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Changed = "cccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 4, 0, 0, TimeSpan.Zero);
    private static readonly PrReviewPolicy Policy = new(
        ["build"], "review-bot-run", "review-bot",
        TrustedCheckProducers: new Dictionary<string, string> { ["build"] = "app:100" },
        AutomatedCheckProducer: "app:200");

    private static PrWorkerEvidence Worker() => new("fresh-review-worker", Head,
        Now.AddMinutes(-30), true, Now.AddMinutes(-10), "https://example.org/review/12",
        "approved", "review-account", CompletionMessageId: "worker-result",
        ReviewPostAttemptId: "post-attempt", ReviewPostOutcome: "posted",
        PostedBodySha256: "verified-body-digest", ReviewerVerdict: "no_blockers");

    private static PrWorkflow Workflow() => new("sample/project", 12, Head, Base,
        "author-worker", "developer-worker", "manager-worker", Now.AddHours(-1),
        Reviewer: Worker(), ReviewLaunchState: "accepted", ReviewLaunchAt: Now.AddMinutes(-30));

    private static PrSourceStatus Source() => new("sample/project", 12, Head, Base,
        "open", "author-account", false, true,
        [
            new("build", Head, "completed", "success", "app:100"),
            new("review-bot-run", Head, "completed", "success", "app:200")
        ],
        [
            new("https://example.org/review/12", "review-account", Head, "approved",
                true, Now.AddMinutes(-9), "verified-body-digest"),
            new("https://example.org/review/13", "review-bot", Head, "commented",
                true, Now.AddMinutes(-8))
        ], 0);

    private static PrGateDecision Evaluate(PrWorkflow? workflow = null,
        PrSourceStatus? source = null, PrReviewPolicy? policy = null) =>
        PrReviewGate.Evaluate(workflow ?? Workflow(), policy ?? Policy,
            source ?? Source(), Now);

    [Fact]
    public void CompleteIndependentAndAutomatedReviewsPermitMerge()
    {
        var result = Evaluate();
        Assert.True(result.Ready);
        Assert.Equal("merge_ready", result.State);
        Assert.Empty(result.Blockers);
    }

    [Theory]
    [InlineData("author-worker")]
    [InlineData("developer-worker")]
    [InlineData("manager-worker")]
    public void SameChatAsAuthorDeveloperOrManagerIsNeverIndependent(string identity)
    {
        var workflow = Workflow() with { Reviewer = Worker() with { ConversationId = identity } };
        Assert.Contains("reviewer_not_independent", Evaluate(workflow).Blockers);
    }

    [Fact]
    public void SharedSourceAccountStillRequiresIndependentChatAndPostedWorkerReceipt()
    {
        // The local source credential may be shared. Independence is proven
        // by the newly created worker conversation and host-authored receipt.
        var workflow = Workflow() with {
            Reviewer = Worker() with { ReviewerAccount = "author-account" }
        };
        var source = Source() with {
            Reviews = [Source().Reviews[0] with { Author = "author-account" },
                Source().Reviews[1]]
        };
        Assert.True(Evaluate(workflow, source).Ready);
        Assert.Contains("review_post_receipt_missing",
            Evaluate(workflow with {
                Reviewer = workflow.Reviewer! with { ReviewPostAttemptId = null }
            }, source).Blockers);
    }

    [Fact]
    public void UnconfirmedReviewCreationCannotBePromotedToReady()
    {
        var workflow = Workflow() with { ReviewLaunchState = "uncertain" };
        Assert.Contains("review_worker_creation_unconfirmed", Evaluate(workflow).Blockers);
    }

    [Fact]
    public void AcceptedButIncompleteWorkerBlocksDespitePostedReview()
    {
        var workflow = Workflow() with {
            Reviewer = Worker() with { Completed = false, CompletedAt = null }
        };
        Assert.Contains("review_worker_not_completed", Evaluate(workflow).Blockers);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    public void SourceRevisionInvalidatesProof(string changed)
    {
        var source = changed == "head" ? Source() with { HeadSha = Changed } :
            Source() with { BaseSha = Changed };
        Assert.False(Evaluate(source: source).Ready);
        Assert.Contains(changed == "head" ? "head_sha_changed" : "base_sha_changed",
            Evaluate(source: source).Blockers);
    }

    [Theory]
    [InlineData("queued", null)]
    [InlineData("in_progress", null)]
    [InlineData("completed", "skipped")]
    [InlineData("completed", "cancelled")]
    [InlineData("completed", "failure")]
    public void AutomatedReviewerRunMustConcludeSuccess(string status, string? conclusion)
    {
        var source = Source() with {
            Checks = [new("build", Head, "completed", "success", "app:100"),
                new("review-bot-run", Head, status, conclusion, "app:200")]
        };
        Assert.Contains("automated_review_run_not_successful", Evaluate(source: source).Blockers);
    }

    [Fact]
    public void AutomatedRunWithoutSubstantivePostedReviewCannotPass()
    {
        var source = Source() with {
            Reviews = [Source().Reviews[0],
                Source().Reviews[1] with { Substantive = false }]
        };
        Assert.Contains("substantive_automated_review_missing", Evaluate(source: source).Blockers);
    }

    [Fact]
    public void SourceReviewMustMatchWorkerAccountUrlAndCurrentHead()
    {
        var source = Source() with {
            Reviews = [Source().Reviews[0] with { HeadSha = Changed }, Source().Reviews[1]]
        };
        Assert.Contains("independent_review_not_verified_at_source", Evaluate(source: source).Blockers);
    }

    [Theory]
    [InlineData("queued", null)]
    [InlineData("completed", "failure")]
    [InlineData("completed", "neutral")]
    public void NonPassingRequiredCheckBlocks(string status, string? conclusion)
    {
        var source = Source() with {
            Checks = [new("build", Head, status, conclusion, "app:100"), Source().Checks[1]]
        };
        Assert.Contains("required_check_not_successful:build", Evaluate(source: source).Blockers);
    }

    [Fact]
    public void APassingCheckFromAnUntrustedProducerCannotSatisfyPolicy()
    {
        var spoofedRequired = Source() with {
            Checks = [Source().Checks[0] with { ProducerId = "user:999" },
                Source().Checks[1]]
        };
        Assert.Contains("required_check_not_successful:build",
            Evaluate(source: spoofedRequired).Blockers);

        var spoofedAutomated = Source() with {
            Checks = [Source().Checks[0],
                Source().Checks[1] with { ProducerId = "app:999" }]
        };
        Assert.Contains("automated_review_run_not_successful",
            Evaluate(source: spoofedAutomated).Blockers);
    }

    [Fact]
    public void MissingCheckProducerPolicyCannotSilentlyPass()
    {
        var policy = Policy with {
            AutomatedCheckProducer = null,
            TrustedCheckProducers = null
        };
        var result = Evaluate(policy: policy);
        Assert.Contains("automated_review_producer_missing", result.Blockers);
        Assert.Contains("required_check_producer_missing:build", result.Blockers);
    }

    [Fact]
    public void UnresolvedThreadsBlockEvenIfEveryOtherCheckPasses()
    {
        Assert.Contains("unresolved_review_threads:2",
            Evaluate(source: Source() with { UnresolvedThreads = 2 }).Blockers);
    }

    [Fact]
    public void RequestedChangesBlockUntilNewVerifiedReview()
    {
        var source = Source() with {
            Reviews = [..Source().Reviews,
                new PrReview("https://example.org/review/14", "another", Head,
                    "changes_requested", true, Now.AddMinutes(-1))]
        };
        Assert.Contains("changes_requested", Evaluate(source: source).Blockers);
    }

    [Fact]
    public void AutomatedReviewerCannotBeReusedAsIndependentReviewer()
    {
        var workflow = Workflow() with {
            Reviewer = Worker() with {
                ReviewerAccount = "REVIEW-BOT",
                ReviewUrl = "https://example.org/review/13"
            }
        };
        Assert.Contains("reviewer_not_independent", Evaluate(workflow).Blockers);
        Assert.Contains("substantive_automated_review_missing", Evaluate(workflow).Blockers);
    }

    [Fact]
    public void OlderHeadChangeRequestRemainsBindingUntilLatestDecisiveApproval()
    {
        var requested = new PrReview("https://example.org/review/14", "reviewer-x",
            Changed, "changes_requested", true, Now.AddMinutes(-2));
        var commented = new PrReview("https://example.org/review/15", "reviewer-x",
            Head, "commented", true, Now.AddMinutes(-1));
        var source = Source() with { Reviews = [..Source().Reviews, requested, commented] };
        Assert.Contains("changes_requested", Evaluate(source: source).Blockers);

        var approved = new PrReview("https://example.org/review/16", "REVIEWER-X",
            Head, "approved", true, Now.AddMinutes(1));
        var resolved = source with { Reviews = [..source.Reviews, approved] };
        Assert.DoesNotContain("changes_requested", Evaluate(source: resolved).Blockers);
    }

    [Fact]
    public void NoRequiredChecksAndNoAutomatedPolicyNeverImplicitlyWaive()
    {
        var policy = new PrReviewPolicy([], "", "");
        var decision = Evaluate(policy: policy);
        Assert.Contains("required_checks_policy_missing", decision.Blockers);
        Assert.Contains("automated_review_policy_missing", decision.Blockers);
    }

    [Fact]
    public void AttemptedMergeNeverReauthorizedByNewEvidence()
    {
        Assert.Contains("merge_already_attempted", Evaluate(
            workflow: Workflow() with { MergeAttemptId = "attempt" }).Blockers);
    }
}
