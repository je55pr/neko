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
        AutomatedCheckProducer: "app:200",
        AutomatedNoFindingsMarker: "No actionable findings",
        AllowedRepositories: ["sample/project"]);

    private static PrWorkerEvidence Worker() => new("fresh-review-worker", Head,
        Now.AddMinutes(-30), true, Now.AddMinutes(-10), "https://example.org/review/12",
        "approved", "review-account", CompletionMessageId: "worker-result",
        ReviewPostAttemptId: "post-attempt", ReviewPostOutcome: "posted",
        PostedBodySha256: "verified-body-digest", ReviewerVerdict: "no_blockers",
        CreationPromptSha256: new string('A', 64));

    private static PrWorkflow Workflow() => new("sample/project", 12, Head, Base,
        "author-worker", "developer-worker", "manager-worker", Now.AddHours(-1),
        Reviewer: Worker(), ReviewLaunchState: "accepted", ReviewLaunchAt: Now.AddMinutes(-30));

    private static PrSourceStatus Source() => new("sample/project", 12, Head, Base,
        "open", "author-account", false, true,
        [
            new("build", Head, "completed", "success", "app:100"),
            new("review-bot-run", Head, "completed", "success", "app:200",
                StartedAt: Now.AddMinutes(-15))
        ],
        [
            new("https://example.org/review/12", "review-account", Head, "approved",
                true, Now.AddMinutes(-9), "verified-body-digest"),
            new("https://example.org/review/13", "review-bot", Head, "commented",
                true, Now.AddMinutes(-8), SourceReviewId: 51,
                AutomatedNoFindings: true)
        ], 0);

    private static PrGateDecision Evaluate(PrWorkflow? workflow = null,
        PrSourceStatus? source = null, PrReviewPolicy? policy = null) =>
        PrReviewGate.Evaluate(workflow ?? Workflow(), policy ?? Policy,
            source ?? Source(), Now);

    [Fact]
    public void RepositoryNotInTrustedHostPolicyCannotPassTheGate()
    {
        var restricted = Policy with { AllowedRepositories = ["different/repository"] };
        var result = Evaluate(policy: restricted);
        Assert.False(result.Ready);
        Assert.Contains("repository_not_authorized", result.Blockers);
        Assert.Contains("repository_not_authorized",
            Evaluate(policy: Policy with { AllowedRepositories = null }).Blockers);
    }

    [Fact]
    public void CompleteIndependentAndAutomatedReviewsPermitMerge()
    {
        var result = Evaluate();
        Assert.True(result.Ready);
        Assert.Equal("merge_ready", result.State);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void EmptyAutomatedReviewOptInStillRequiresTrustworthyReviewAndCheck()
    {
        var policy = Policy with {
            AutomatedNoFindingsMarker = null,
            AllowVerifiedEmptyAutomatedReview = true
        };
        Assert.True(Evaluate(policy: policy).Ready);
        Assert.Contains("automated_finding_policy_missing",
            Evaluate(policy: Policy with { AutomatedNoFindingsMarker = null }).Blockers);
        var incomplete = Source() with {
            Reviews = [Source().Reviews[0],
                Source().Reviews[1] with { Substantive = false }]
        };
        Assert.Contains("substantive_automated_review_missing",
            Evaluate(source: incomplete, policy: policy).Blockers);
        var untrusted = Source() with {
            Checks = [Source().Checks[0],
                Source().Checks[1] with { ProducerId = "app:untrusted" }]
        };
        Assert.Contains("automated_review_run_not_successful",
            Evaluate(source: untrusted, policy: policy).Blockers);
        // Older review findings cannot be ignored merely because the current
        // bot submitted an empty review on a newer head.
        var unresolved = Source() with { UnresolvedThreads = 1 };
        Assert.Contains("unresolved_review_threads:1", Evaluate(
            source: unresolved,
            policy: policy with { RequireNoUnresolvedThreads = false }).Blockers);
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
                new("review-bot-run", Head, status, conclusion, "app:200",
                StartedAt: Now.AddMinutes(-15))]
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
    public void SharedActionsAppCannotSpoofTrustedWorkflowOrMaskLatestFailure()
    {
        var policy = Policy with {
            TrustedCheckProducers = new Dictionary<string, string> {
                ["build"] = "app:15368"
            },
            TrustedCheckWorkflows = new Dictionary<string, string> {
                ["build"] = "workflow:400"
            }
        };
        var trustedFailure = new PrCheck("build", Head, "completed", "failure",
            "app:15368", "workflow:400", true, 80);
        var wrongWorkflowSuccess = new PrCheck("build", Head, "completed", "success",
            "app:15368", "workflow:999", true, 100);
        var olderTrustedSuccess = new PrCheck("build", Head, "completed", "success",
            "app:15368", "workflow:400", true, 70);
        var source = Source() with {
            Checks = [wrongWorkflowSuccess, trustedFailure, olderTrustedSuccess,
                Source().Checks[1]]
        };
        Assert.Contains("required_check_not_successful:build",
            Evaluate(source: source, policy: policy).Blockers);

        var latestTrustedSuccess = new PrCheck("build", Head, "completed",
            "success", "app:15368", "workflow:400", true, 110);
        var updated = source with {
            Checks = [..source.Checks, latestTrustedSuccess]
        };
        Assert.True(Evaluate(source: updated, policy: policy).Ready);

        var noWorkflowConfiguration = policy with {
            TrustedCheckWorkflows = null
        };
        Assert.Contains("required_check_not_successful:build",
            Evaluate(source: updated, policy: noWorkflowConfiguration).Blockers);
    }

    [Fact]
    public void AutomatedRunMustMatchConfiguredWorkflowNotJustActionsApp()
    {
        var policy = Policy with {
            AutomatedCheckProducer = "app:15368",
            AutomatedCheckWorkflow = "workflow:777"
        };
        var correct = new PrCheck("review-bot-run", Head, "completed", "success",
            "app:15368", "workflow:777", true, 200,
            StartedAt: Now.AddMinutes(-15));
        var differentWorkflow = new PrCheck("review-bot-run", Head, "completed",
            "success", "app:15368", "workflow:999", true, 300,
            StartedAt: Now.AddMinutes(-15));
        var source = Source() with {
            Checks = [Source().Checks[0], correct, differentWorkflow]
        };
        Assert.True(Evaluate(policy: policy, source: source).Ready);
        Assert.Contains("automated_review_run_not_successful",
            Evaluate(policy: policy with { AutomatedCheckWorkflow = null },
                source: source).Blockers);
        Assert.Contains("automated_review_run_not_successful",
            Evaluate(policy: policy with { AutomatedCheckWorkflow = "workflow:999" },
                source: source with {
                    Checks = [Source().Checks[0], correct]
                }).Blockers);
    }

    [Fact]
    public void SuccessfulAutomationCheckNeverWaivesActionableSourceReviewFindings()
    {
        var oldClear = Source().Reviews[1] with {
            SourceReviewId = 51, AutomatedNoFindings = true,
            SubmittedAt = Now.AddMinutes(-8)
        };
        var latestActionable = oldClear with {
            Url = "https://example.org/review/14", SourceReviewId = 52,
            SubmittedAt = Now.AddMinutes(-7), AutomatedNoFindings = false
        };
        var source = Source() with {
            Reviews = [Source().Reviews[0], oldClear, latestActionable],
            UnresolvedThreads = 0
        };
        Assert.Contains("automated_findings_untriaged",
            Evaluate(source: source).Blockers);
        Assert.False(Evaluate(source: source).Ready);

        var newerClear = latestActionable with {
            Url = "https://example.org/review/15", SourceReviewId = 53,
            SubmittedAt = Now.AddMinutes(-6), AutomatedNoFindings = true
        };
        Assert.True(Evaluate(source: source with {
            Reviews = [..source.Reviews, newerClear]
        }).Ready);
        Assert.Contains("automated_finding_policy_missing",
            Evaluate(policy: Policy with { AutomatedNoFindingsMarker = null }).Blockers);
    }

    [Fact]
    public void SameHeadRerunNeedsNewAutomatedReviewAfterCheckStart()
    {
        var source = Source();
        var original = Evaluate(source: source);
        Assert.True(original.Ready);

        var newRun = source.Checks[1] with {
            RunId = 9002,
            StartedAt = Now.AddMinutes(-5)
        };
        var beforeNewReview = source with {
            Checks = [source.Checks[0], newRun]
        };
        Assert.Contains("automated_review_run_not_bound_to_review",
            Evaluate(source: beforeNewReview).Blockers);
        Assert.False(Evaluate(source: beforeNewReview).Ready);

        var matchingNewReview = source.Reviews[1] with {
            Url = "https://example.org/review/next",
            SourceReviewId = 999,
            SubmittedAt = Now.AddMinutes(-4)
        };
        var complete = beforeNewReview with {
            Reviews = [..source.Reviews, matchingNewReview]
        };
        Assert.True(Evaluate(source: complete).Ready);

        var missingStart = complete with {
            Checks = [source.Checks[0], newRun with { StartedAt = null }]
        };
        Assert.Contains("automated_review_run_not_bound_to_review",
            Evaluate(source: missingStart).Blockers);
    }

    [Fact]
    public void OverlappingBotRunsCannotShareAStaleClearReview()
    {
        var original = Source();
        var runA = original.Checks[1] with {
            RunId = 701, StartedAt = Now.AddMinutes(-15)
        };
        var runB = original.Checks[1] with {
            RunId = 702, StartedAt = Now.AddMinutes(-9)
        };
        // A's review is submitted after B began, so timestamp-only
        // correlation would accept it as B's review.
        var overlapping = original with {
            Checks = [original.Checks[0], runA, runB],
            Reviews = [original.Reviews[0], original.Reviews[1] with {
                SubmittedAt = Now.AddMinutes(-8)
            }]
        };
        Assert.Contains("automated_review_run_ambiguous",
            Evaluate(source: overlapping).Blockers);
        Assert.False(Evaluate(source: overlapping).Ready);

        // An untrusted same-name check is ignored when counting the
        // authenticated publisher's actual runs.
        var unrelated = original with {
            Checks = [original.Checks[0], runA,
                runB with { ProducerId = "app:untrusted" }]
        };
        Assert.True(Evaluate(source: unrelated).Ready);
    }

    [Fact]
    public void SameSecondReviewChangesUseSourceIdsOrFailClosed()
    {
        var stamp = Now.AddMinutes(-2);
        var approval = new PrReview("https://example.org/review/a",
            "reviewer-x", Head, "approved", true, stamp, SourceReviewId: 101);
        var request = new PrReview("https://example.org/review/b",
            "REVIEWER-X", Head, "changes_requested", true, stamp, SourceReviewId: 102);
        var source = Source() with {
            Reviews = [..Source().Reviews, approval, request]
        };
        Assert.Contains("changes_requested", Evaluate(source: source).Blockers);

        var laterApproval = approval with {
            Url = "https://example.org/review/c", SourceReviewId = 103
        };
        Assert.DoesNotContain("changes_requested", Evaluate(source: source with {
            Reviews = [..source.Reviews, laterApproval]
        }).Blockers);

        var unknownIds = Source() with {
            Reviews = [..Source().Reviews,
                approval with { SourceReviewId = null },
                request with { SourceReviewId = null }]
        };
        Assert.Contains("changes_requested", Evaluate(source: unknownIds).Blockers);
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
