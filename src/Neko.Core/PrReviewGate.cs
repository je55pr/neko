namespace Neko;

/// <summary>Provider-neutral facts. Source-side reviews and checks must be re-read before merge.</summary>
public sealed record PrCheck(string Name, string HeadSha, string Status, string? Conclusion,
    string? ProducerId = null, string? WorkflowId = null,
    bool RequiresWorkflowIdentity = false, long RunId = 0,
    DateTimeOffset? StartedAt = null);
public sealed record PrReview(string Url, string Author, string HeadSha, string Verdict,
    bool Substantive, DateTimeOffset SubmittedAt, string? BodySha256 = null,
    long? SourceReviewId = null, bool AutomatedNoFindings = false);
public sealed record PrSourceStatus(string Repository, int Number, string HeadSha,
    string BaseSha, string State, string Author, bool Merged, bool Mergeable,
    IReadOnlyList<PrCheck> Checks, IReadOnlyList<PrReview> Reviews,
    int UnresolvedThreads);
public sealed record PrReviewPolicy(
    IReadOnlyList<string> RequiredChecks, string AutomatedCheck, string AutomatedReviewer,
    bool RequireNoUnresolvedThreads = true,
    IReadOnlyDictionary<string, string>? TrustedCheckProducers = null,
    string? AutomatedCheckProducer = null,
    IReadOnlyDictionary<string, string>? TrustedCheckWorkflows = null,
    string? AutomatedCheckWorkflow = null,
    string? AutomatedNoFindingsMarker = null,
    IReadOnlyList<string>? AllowedRepositories = null);
public sealed record PrWorkerEvidence(string ConversationId, string CreatedForHead,
    DateTimeOffset CreatedAt, bool Completed, DateTimeOffset? CompletedAt,
    string? ReviewUrl, string? Verdict, string? ReviewerAccount,
    string? CompletionMessageId = null,
    string? ReviewPostAttemptId = null, string? ReviewPostOutcome = null,
    string? PostedBodySha256 = null, string? ReviewerVerdict = null,
    string? CreationPromptSha256 = null, string? CreationUserMessageId = null);
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

    // Recognize common Markdown headings such as "### [P1] Bug",
    // "**P1:** Bug", and "P1: Bug". A zero-findings verdict may never
    // override a source review containing one of these headings.
    public static bool SeverityHeading(string raw)
    {
        var line = raw.TrimStart(' ', '\t', '#', '-', '*', '_', '>');
        if (line.Length >= 4 && line[0] == '[' &&
            line[1] is 'P' or 'p' && line[2] is >= '0' and <= '3' &&
            line[3] == ']')
            return line.Length == 4 || IsSeverityBoundary(line[4]);
        return line.Length >= 2 &&
            line[0] is 'P' or 'p' && line[1] is >= '0' and <= '3' &&
            (line.Length == 2 || IsSeverityBoundary(line[2]));
    }

    private static bool IsSeverityBoundary(char value) =>
        !char.IsLetterOrDigit(value) && value != '_';

    public static bool ContainsSeverityFinding(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (SeverityHeading(line) ||
                line.Contains("BLOCKERS:", StringComparison.OrdinalIgnoreCase))
                return true;
            for (var i = 0; i + 1 < line.Length; i++)
            {
                // In-line severity labels must be checked too. A review may
                // claim zero findings at the top while describing P1: later.
                if (line[i] is 'P' or 'p' &&
                    line[i + 1] is >= '0' and <= '3' &&
                    (i == 0 || IsSeverityBoundary(line[i - 1])) &&
                    (i + 2 == line.Length || IsSeverityBoundary(line[i + 2])))
                    return true;
            }
        }
        return false;
    }

    // Select one exact trusted producer/workflow set for both latest-run
    // evaluation and same-head rerun ambiguity detection.
    private static IEnumerable<PrCheck> TrustedChecks(IEnumerable<PrCheck> checks,
        string name, string head, string? producer, string? workflow)
    {
        if (string.IsNullOrWhiteSpace(producer)) return Enumerable.Empty<PrCheck>();
        return checks.Where(c => c.Name == name && c.HeadSha == head &&
            c.ProducerId == producer &&
            (string.IsNullOrWhiteSpace(workflow)
                ? !c.RequiresWorkflowIdentity
                : c.WorkflowId == workflow));
    }

    private static PrCheck? TrustedLatestCheck(IEnumerable<PrCheck> checks,
        string name, string head, string? producer, string? workflow) =>
        TrustedChecks(checks, name, head, producer, workflow)
            .OrderByDescending(c => c.RunId).FirstOrDefault();

    public static PrGateDecision Evaluate(PrWorkflow workflow, PrReviewPolicy policy,
        PrSourceStatus source, DateTimeOffset now)
    {
        var blockers = new List<string>();
        if (policy.AllowedRepositories?.Any(allowed =>
                StringComparer.OrdinalIgnoreCase.Equals(allowed, workflow.Repository)) != true)
            blockers.Add("repository_not_authorized");
        if (!StringComparer.OrdinalIgnoreCase.Equals(source.Repository, workflow.Repository) ||
            source.Number != workflow.Number)
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
                StringComparer.OrdinalIgnoreCase.Equals(worker.ReviewerAccount,
                    policy.AutomatedReviewer))
                blockers.Add("reviewer_not_independent");
            if (worker.CreatedForHead != workflow.HeadSha ||
                worker.CreatedAt < workflow.CreatedAt)
                blockers.Add("reviewer_not_created_for_head");
            if (worker.CreationPromptSha256 is not { Length: 64 } ||
                worker.CreationPromptSha256.Any(c =>
                    c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                blockers.Add("review_creation_turn_unbound");
            if (!worker.Completed || worker.CompletedAt is null ||
                worker.CompletedAt < worker.CreatedAt || worker.CompletionMessageId is null)
                blockers.Add("review_worker_not_completed");
            if (worker.ReviewerVerdict != "no_blockers")
                blockers.Add("independent_reviewer_found_blockers");
            if (worker.ReviewPostOutcome != "posted" ||
                string.IsNullOrWhiteSpace(worker.ReviewPostAttemptId) ||
                string.IsNullOrWhiteSpace(worker.PostedBodySha256))
                blockers.Add("review_post_receipt_missing");
            if (string.IsNullOrWhiteSpace(worker.ReviewUrl) ||
                string.IsNullOrWhiteSpace(worker.ReviewerAccount) ||
                worker.Verdict is not ("approved" or "commented"))
                blockers.Add("review_post_evidence_missing");
            else if (!source.Reviews.Any(r => r.Url == worker.ReviewUrl &&
                StringComparer.OrdinalIgnoreCase.Equals(r.Author, worker.ReviewerAccount) &&
                r.HeadSha == workflow.HeadSha &&
                r.Substantive && r.SubmittedAt >= worker.CreatedAt &&
                r.BodySha256 == worker.PostedBodySha256 &&
                r.Verdict == worker.Verdict))
                blockers.Add("independent_review_not_verified_at_source");
        }

        if (string.IsNullOrWhiteSpace(policy.AutomatedCheck) ||
            string.IsNullOrWhiteSpace(policy.AutomatedReviewer))
            blockers.Add("automated_review_policy_missing");
        else
        {
            if (string.IsNullOrWhiteSpace(policy.AutomatedCheckProducer))
                blockers.Add("automated_review_producer_missing");
            var automated = TrustedLatestCheck(source.Checks,
                policy.AutomatedCheck, workflow.HeadSha,
                policy.AutomatedCheckProducer, policy.AutomatedCheckWorkflow);
            if (automated is not { Status: "completed", Conclusion: "success" })
                blockers.Add("automated_review_run_not_successful");
            // Without a trustworthy run ID also present on the submitted
            // review, multiple same-head runs are ambiguous, including
            // overlapping runs whose review timestamps can interleave.
            // Fail closed rather than pairing one run's check to another's
            // review simply because their timestamps overlap.
            if (TrustedChecks(source.Checks, policy.AutomatedCheck,
                    workflow.HeadSha, policy.AutomatedCheckProducer,
                    policy.AutomatedCheckWorkflow).Count() != 1)
                blockers.Add("automated_review_run_ambiguous");
            var botReviews = source.Reviews.Where(r =>
                StringComparer.OrdinalIgnoreCase.Equals(r.Author, policy.AutomatedReviewer) &&
                r.HeadSha == workflow.HeadSha && r.Substantive &&
                r.Url != worker?.ReviewUrl &&
                r.Verdict is "approved" or "commented")
                .OrderByDescending(r => r.SubmittedAt)
                .ThenByDescending(r => r.SourceReviewId ?? long.MinValue).ToArray();
            if (botReviews.Length == 0)
                blockers.Add("substantive_automated_review_missing");
            if (string.IsNullOrWhiteSpace(policy.AutomatedNoFindingsMarker))
                blockers.Add("automated_finding_policy_missing");
            // A successful check says a reviewer ran, NOT that it found no
            // defects. Only a configured, source-derived positive conclusion
            // on the latest substantive bot review counts as clear.
            if (botReviews.Length > 0 &&
                (!botReviews[0].AutomatedNoFindings ||
                 botReviews.Length > 1 &&
                 botReviews[0].SubmittedAt == botReviews[1].SubmittedAt &&
                 (botReviews[0].SourceReviewId is null ||
                  botReviews[1].SourceReviewId is null)))
                blockers.Add("automated_findings_untriaged");
            // Same-commit re-runs are distinct reviews. An older positive
            // result cannot satisfy a later successful check run. The source
            // must prove both the check start and a review submitted since
            // that start; missing timestamps always block.
            if (automated?.StartedAt is not { } runStarted ||
                botReviews.Length == 0 ||
                botReviews[0].SubmittedAt < runStarted)
                blockers.Add("automated_review_run_not_bound_to_review");
        }
        if (policy.RequiredChecks is null || policy.RequiredChecks.Count == 0)
            blockers.Add("required_checks_policy_missing");
        else
            foreach (var name in policy.RequiredChecks.Distinct(StringComparer.Ordinal))
            {
                if (policy.TrustedCheckProducers is null ||
                    !policy.TrustedCheckProducers.TryGetValue(name, out var producer) ||
                    string.IsNullOrWhiteSpace(producer))
                    blockers.Add("required_check_producer_missing:" + name);
                else
                {
                    string? expectedWorkflow = null;
                    if (policy.TrustedCheckWorkflows is not null)
                        policy.TrustedCheckWorkflows.TryGetValue(name, out expectedWorkflow);
                    var latest = TrustedLatestCheck(source.Checks,
                        name, workflow.HeadSha, producer, expectedWorkflow);
                    if (latest is not { Status: "completed", Conclusion: "success" })
                        blockers.Add("required_check_not_successful:" + name);
                }
            }
        if (policy.RequireNoUnresolvedThreads && source.UnresolvedThreads != 0)
            blockers.Add("unresolved_review_threads:" + source.UnresolvedThreads);
        // An earlier head's active change request remains binding until that
        // reviewer issues a newer decisive approval or the review is dismissed.
        // A mere COMMENTED review does not supersede a change request.
        if (source.Reviews
            .Where(r => r.Verdict is "approved" or "changes_requested")
            .GroupBy(r => r.Author, StringComparer.OrdinalIgnoreCase)
            .Any(g =>
            {
                var timestamp = g.Max(r => r.SubmittedAt);
                var latestAtTime = g.Where(r => r.SubmittedAt == timestamp).ToArray();
                // GitHub submission timestamps have second precision. A later
                // review ID breaks ties; missing/ambiguous IDs must fail closed
                // whenever a change request might still be outstanding.
                var ambiguous = latestAtTime.Length > 1 &&
                    latestAtTime.Any(r => r.SourceReviewId is null) &&
                    latestAtTime.Any(r => r.Verdict == "changes_requested");
                var newestId = latestAtTime.Max(r => r.SourceReviewId ?? 0);
                return ambiguous || latestAtTime.Any(r =>
                    (r.SourceReviewId ?? 0) == newestId &&
                    r.Verdict == "changes_requested");
            }))
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
