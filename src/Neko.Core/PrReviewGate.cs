namespace Neko;

/// <summary>Provider-neutral facts. Source-side reviews and checks must be re-read before merge.</summary>
public sealed record PrCheck(string Name, string HeadSha, string Status, string? Conclusion);
public sealed record PrReview(string Url, string Author, string HeadSha, string Verdict,
    bool Substantive, DateTimeOffset SubmittedAt);
public sealed record PrSourceStatus(string Repository, int Number, string HeadSha,
    string BaseSha, string State, string Author, bool Merged, bool Mergeable,
    IReadOnlyList<PrCheck> Checks, IReadOnlyList<PrReview> Reviews,
    int UnresolvedThreads);
public sealed record PrReviewPolicy(
    IReadOnlyList<string> RequiredChecks, string AutomatedCheck, string AutomatedReviewer,
    bool RequireNoUnresolvedThreads = true);
public sealed record PrWorkerEvidence(string ConversationId, string CreatedForHead,
    DateTimeOffset CreatedAt, bool Completed, DateTimeOffset? CompletedAt,
    string? ReviewUrl, string? Verdict, string? ReviewerAccount,
    string? CompletionMessageId = null);
public sealed record PrAuditEvent(DateTimeOffset At, string Action, string Detail,
    string? AttemptId = null);
public sealed record PrWorkflow(string Repository, int Number, string HeadSha, string BaseSha,
    string CreatorConversationId, string DeveloperConversationId, string ManagerConversationId,
    DateTimeOffset CreatedAt, string State = "implementing",
    PrWorkerEvidence? Reviewer = null, string? MergeAttemptId = null,
    string? MergeOutcome = null, string? MergedSha = null,
    string ReviewLaunchState = "unstarted", DateTimeOffset? ReviewLaunchAt = null,
    DateTimeOffset? MergeAuthorizedAt = null, string? MergeAuthorizedBy = null,
    PrSourceStatus? LastSourceEvidence = null, PrGateDecision? LastGateDecision = null,
    IReadOnlyList<PrAuditEvent>? Audit = null);
public sealed record PrGateDecision(bool Ready, string State,
    IReadOnlyList<string> Blockers, DateTimeOffset CheckedAt);

public static class PrReviewGate
{
    public static bool ValidSha(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static PrGateDecision Evaluate(PrWorkflow workflow, PrReviewPolicy policy,
        PrSourceStatus source, DateTimeOffset now)
    {
        var blockers = new List<string>();
        if (source.Repository != workflow.Repository || source.Number != workflow.Number)
            blockers.Add("source_identity_mismatch");
        if (source.State != "open" || source.Merged) blockers.Add("pr_not_open");
        if (!source.Mergeable) blockers.Add("pr_not_mergeable");
        if (source.HeadSha != workflow.HeadSha) blockers.Add("head_sha_changed");
        if (source.BaseSha != workflow.BaseSha) blockers.Add("base_sha_changed");
        if (workflow.MergeAttemptId is not null) blockers.Add("merge_already_attempted");
        if (workflow.ManagerConversationId == workflow.CreatorConversationId ||
            workflow.ManagerConversationId == workflow.DeveloperConversationId)
            blockers.Add("manager_is_developer");
        var worker = workflow.Reviewer;
        if (worker is null)
            blockers.Add("independent_reviewer_missing");
        else
        {
            if (workflow.ReviewLaunchState != "accepted")
                blockers.Add("review_worker_creation_unconfirmed");
            if (worker.ConversationId == workflow.CreatorConversationId ||
                worker.ConversationId == workflow.DeveloperConversationId ||
                worker.ConversationId == workflow.ManagerConversationId ||
                worker.ReviewerAccount == source.Author)
                blockers.Add("reviewer_not_independent");
            if (worker.CreatedForHead != workflow.HeadSha ||
                worker.CreatedAt < workflow.CreatedAt)
                blockers.Add("reviewer_not_created_for_head");
            if (!worker.Completed || worker.CompletedAt is null ||
                worker.CompletedAt < worker.CreatedAt)
                blockers.Add("review_worker_not_completed");
            if (string.IsNullOrWhiteSpace(worker.ReviewUrl) ||
                string.IsNullOrWhiteSpace(worker.ReviewerAccount) ||
                worker.Verdict is not ("approved" or "commented"))
                blockers.Add("review_post_evidence_missing");
            else if (!source.Reviews.Any(r => r.Url == worker.ReviewUrl &&
                r.Author == worker.ReviewerAccount && r.HeadSha == workflow.HeadSha &&
                r.Substantive && r.SubmittedAt >= worker.CreatedAt &&
                r.Verdict == worker.Verdict))
                blockers.Add("independent_review_not_verified_at_source");
        }

        if (string.IsNullOrWhiteSpace(policy.AutomatedCheck) ||
            string.IsNullOrWhiteSpace(policy.AutomatedReviewer))
            blockers.Add("automated_review_policy_missing");
        else
        {
            if (!source.Checks.Any(c => c.Name == policy.AutomatedCheck &&
                c.HeadSha == workflow.HeadSha && c.Status == "completed" &&
                c.Conclusion == "success"))
                blockers.Add("automated_review_run_not_successful");
            if (!source.Reviews.Any(r => r.Author == policy.AutomatedReviewer &&
                r.HeadSha == workflow.HeadSha && r.Substantive &&
                r.Verdict is "approved" or "commented"))
                blockers.Add("substantive_automated_review_missing");
        }
        if (policy.RequiredChecks is null || policy.RequiredChecks.Count == 0)
            blockers.Add("required_checks_policy_missing");
        else
            foreach (var name in policy.RequiredChecks.Distinct(StringComparer.Ordinal))
                if (!source.Checks.Any(c => c.Name == name && c.HeadSha == workflow.HeadSha &&
                    c.Status == "completed" && c.Conclusion == "success"))
                    blockers.Add("required_check_not_successful:" + name);
        if (policy.RequireNoUnresolvedThreads && source.UnresolvedThreads != 0)
            blockers.Add("unresolved_review_threads:" + source.UnresolvedThreads);
        if (source.Reviews.Any(r => r.HeadSha == workflow.HeadSha &&
            r.Verdict == "changes_requested"))
            blockers.Add("changes_requested");
        var state = blockers.Count == 0 ? "merge_ready" :
            blockers.Contains("head_sha_changed") || blockers.Contains("base_sha_changed")
                ? "blocked" :
            blockers.Contains("changes_requested") ? "changes_requested" :
            blockers.Any(b => b.StartsWith("required_check_")) ? "checks_pending" :
            blockers.Any(b => b.StartsWith("automated_") || b.StartsWith("substantive_"))
                ? "awaiting_automated_review" : "awaiting_independent_review";
        return new PrGateDecision(blockers.Count == 0, state, blockers, now);
    }
}
