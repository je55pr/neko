using Neko;

namespace Neko.Headless;

internal interface IPrSourceAdapter
{
    Task<PrSourceStatus> ReadAsync(string repository, int number, CancellationToken cancellation);
    // Implementations MUST send expectedHead as an atomic server-side precondition.
    Task<string> MergeAsync(string repository, int number, string expectedHead,
        CancellationToken cancellation);
}

internal sealed record PrWorkflowReport(PrWorkflow Workflow, PrGateDecision Gate);
internal sealed record PrMergeReport(PrWorkflow Workflow, PrGateDecision Gate,
    string Outcome);

/// <summary>Explicit review lifecycle; background worker scheduling remains a separate job.</summary>
internal sealed class PrWorkflowService
{
    private readonly PrWorkflowStore _store;
    private readonly IPrSourceAdapter _source;
    private readonly ChatRuntime _runtime;
    private readonly PrReviewPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _mergeGate = new(1, 1);

    public PrWorkflowService(PrWorkflowStore store, IPrSourceAdapter source,
        ChatRuntime runtime, PrReviewPolicy policy, TimeProvider? clock = null)
    {
        _store = store;
        _source = source;
        _runtime = runtime;
        _policy = policy;
        _clock = clock ?? TimeProvider.System;
    }

    public PrWorkflow? Get(string repository, int number) => _store.Get(repository, number);
    public IReadOnlyList<PrWorkflow> List() => _store.List();

    public async Task<PrWorkflow> CreateAsync(string repository, int number,
        string creator, string developer, string manager, CancellationToken cancellation)
    {
        if (!PrWorkflowStore.ValidRepository(repository) || number < 1)
            throw new ArgumentException("Invalid repository or number.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.Repository != repository || source.Number != number ||
            source.State != "open" || !PrReviewGate.ValidSha(source.HeadSha) ||
            !PrReviewGate.ValidSha(source.BaseSha))
            throw new InvalidOperationException("Source PR identity or state invalid.");
        return _store.Create(repository, number, source.HeadSha, source.BaseSha,
            creator, developer, manager);
    }

    public async Task<PrWorkflowReport> InspectAsync(string repository, int number,
        CancellationToken cancellation)
    {
        var record = _store.Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        var status = await _source.ReadAsync(repository, number, cancellation);
        var decision = PrReviewGate.Evaluate(record, _policy, status, _clock.GetUtcNow());
        // Persist source-verifiable checks and reviewer facts before reporting readiness.
        var observed = _store.Update(repository, number, current => current with {
            LastSourceEvidence = status, LastGateDecision = decision
        });
        return new PrWorkflowReport(observed, decision);
    }

    /// <summary>Explicit revision rearm; old evidence is retained only in audit history.</summary>
    public async Task<PrWorkflow> RefreshRevisionAsync(string repository, int number,
        CancellationToken cancellation)
    {
        var current = _store.Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        if (current.MergeAttemptId is not null ||
            current.ReviewLaunchState is "dispatching" or "uncertain")
            throw new InvalidOperationException("Unresolved external action blocks revision rearm.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.Repository != repository || source.Number != number ||
            source.State != "open" || source.Merged ||
            !PrReviewGate.ValidSha(source.HeadSha) || !PrReviewGate.ValidSha(source.BaseSha))
            throw new InvalidOperationException("Source revision unavailable.");
        return _store.Update(repository, number, existing =>
        {
            if (existing.MergeAttemptId is not null ||
                existing.ReviewLaunchState is "dispatching" or "uncertain" ||
                existing.HeadSha != current.HeadSha || existing.BaseSha != current.BaseSha)
                throw new InvalidOperationException("Revision change was concurrent.");
            if (existing.HeadSha == source.HeadSha && existing.BaseSha == source.BaseSha)
                return existing;
            return _store.Append(existing with {
                HeadSha = source.HeadSha, BaseSha = source.BaseSha,
                Reviewer = null, ReviewLaunchState = "unstarted", ReviewLaunchAt = null,
                State = "awaiting_independent_review", LastSourceEvidence = source,
                LastGateDecision = null
            }, "revision_rearmed",
                "previous_head:" + existing.HeadSha + ";previous_base:" + existing.BaseSha);
        });
    }

    // The launch reservation is durable BEFORE the provider creation call.
    // An interrupted, unconfirmed launch is never repeated automatically.
    public async Task<PrWorkflow> LaunchReviewerAsync(string repository, int number,
        string task, string reviewerAccount, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 12000 ||
            string.IsNullOrWhiteSpace(reviewerAccount) ||
            reviewerAccount.Length > 100 || reviewerAccount.Any(char.IsControl))
            throw new ArgumentException("Invalid reviewer request.");
        var current = _store.Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        if (current.ReviewLaunchState != "unstarted" || current.MergeAttemptId is not null)
            throw new InvalidOperationException("Review creation already reserved.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.HeadSha != current.HeadSha || source.BaseSha != current.BaseSha ||
            source.State != "open" || source.Author == reviewerAccount)
            throw new InvalidOperationException("Source head/base changed.");
        current = _store.Update(repository, number, existing =>
        {
            if (existing.ReviewLaunchState != "unstarted" || existing.MergeAttemptId is not null)
                throw new InvalidOperationException("Review creation already reserved.");
            return _store.Append(existing with {
                ReviewLaunchState = "dispatching", ReviewLaunchAt = _clock.GetUtcNow(),
                State = "awaiting_independent_review"
            }, "review_launch_reserved", "remote_creation_may_be_uncertain");
        });
        SendReceipt receipt;
        try
        {
            receipt = await _runtime.CreateAsync(
                "Independent review assignment for " + repository + "#" + number +
                " at head " + current.HeadSha + ". Do not merge. " + task,
                cancellation);
        }
        catch (Exception)
        {
            // A provider/journal exception after reservation does not prove non-delivery.
            return _store.Update(repository, number, w => _store.Append(w with {
                ReviewLaunchState = "uncertain", State = "blocked"
            }, "review_launch_uncertain", "creation_outcome_unknown"));
        }
        if (receipt.Disposition != SendDisposition.Accepted ||
            string.IsNullOrWhiteSpace(receipt.ConversationId) ||
            _runtime.Get(receipt.ConversationId)?.Id != receipt.ConversationId)
            return _store.Update(repository, number, w => _store.Append(w with {
                ReviewLaunchState = receipt.Disposition == SendDisposition.Rejected
                    ? "rejected" : "uncertain", State = "blocked"
            }, "review_launch_unconfirmed", receipt.Disposition.ToString()));
        return _store.Update(repository, number, w => _store.Append(w with {
            ReviewLaunchState = "accepted",
            Reviewer = new PrWorkerEvidence(receipt.ConversationId, w.HeadSha,
                w.ReviewLaunchAt!.Value, false, null, null, null, reviewerAccount)
        }, "reviewer_bound", "created_for_current_head"));
    }

    /// <summary>Only a verified local terminal turn can become worker completion evidence.</summary>
    public async Task<PrWorkflow> ConfirmReviewAsync(string repository, int number,
        string reviewUrl, string verdict, CancellationToken cancellation)
    {
        var record = _store.Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        var worker = record.Reviewer ??
            throw new InvalidOperationException("No bound reviewer.");
        if (record.ReviewLaunchState != "accepted" ||
            record.MergeAttemptId is not null || worker.CreatedForHead != record.HeadSha)
            throw new InvalidOperationException("Reviewer cannot be confirmed.");
        if (verdict is not ("approved" or "commented") ||
            !Uri.TryCreate(reviewUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("Invalid source review evidence.");
        var history = await _runtime.ReadAsync(worker.ConversationId, cancellation);
        if (!history.ProviderVerified || history.History is null ||
            _runtime.Get(worker.ConversationId)?.Status != "idle" ||
            !history.History.Messages.Any(m => m.Role == VisibleMessageRole.Assistant &&
                m.Text.Contains(reviewUrl, StringComparison.Ordinal)))
            throw new InvalidOperationException("Reviewer has not durably completed.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        var match = source.Reviews.Any(r => r.Url == reviewUrl &&
            r.Author == worker.ReviewerAccount && r.HeadSha == record.HeadSha &&
            r.Verdict == verdict && r.Substantive && r.SubmittedAt >= worker.CreatedAt);
        if (source.HeadSha != record.HeadSha || source.BaseSha != record.BaseSha || !match)
            throw new InvalidOperationException("Source-side independent review unverified.");
        var posted = history.History.Messages.Last(m =>
            m.Role == VisibleMessageRole.Assistant &&
            m.Text.Contains(reviewUrl, StringComparison.Ordinal));
        return _store.Update(repository, number, w => _store.Append(w with {
            Reviewer = worker with { Completed = true, CompletedAt = _clock.GetUtcNow(),
                ReviewUrl = reviewUrl, Verdict = verdict,
                CompletionMessageId = posted.Id }, LastSourceEvidence = source
        }, "review_verified", "local_terminal_and_source_review"));
    }

    public async Task<PrMergeReport> MergeAsync(string repository, int number,
        CancellationToken cancellation)
    {
        await _mergeGate.WaitAsync(cancellation);
        try
        {
            var checkedReport = await InspectAsync(repository, number, cancellation);
            if (!checkedReport.Gate.Ready)
                return new PrMergeReport(checkedReport.Workflow, checkedReport.Gate, "blocked");
            var attemptId = Guid.NewGuid().ToString("N");
            // Persist irreversible attempt marker before any external merge write.
            var reserved = _store.Update(repository, number, w =>
            {
                if (w.MergeAttemptId is not null ||
                    w.HeadSha != checkedReport.Workflow.HeadSha ||
                    w.BaseSha != checkedReport.Workflow.BaseSha)
                    throw new InvalidOperationException("Merge reservation changed.");
                return _store.Append(w with {
                    MergeAttemptId = attemptId, MergeOutcome = "uncertain", State = "blocked",
                    MergeAuthorizedAt = _clock.GetUtcNow(),
                    MergeAuthorizedBy = w.ManagerConversationId
                }, "merge_reserved", "expected_head:" + w.HeadSha +
                    ";manager:" + w.ManagerConversationId, attemptId);
            });
            try
            {
                // Post-reservation source recheck detects races before the remote write.
                var latest = await _source.ReadAsync(repository, number, cancellation);
                var recheck = PrReviewGate.Evaluate(
                    reserved with { MergeAttemptId = null }, _policy,
                    latest, _clock.GetUtcNow());
                if (!recheck.Ready)
                {
                    var stopped = _store.Update(repository, number, w =>
                        _store.Append(w with { MergeOutcome = "rejected" },
                            "merge_preflight_rejected", string.Join(",", recheck.Blockers), attemptId));
                    return new PrMergeReport(stopped, recheck, "rejected");
                }
                // Fresh preflight proof must also be durable before the merge write.
                _store.Update(repository, number, w => _store.Append(w with {
                    LastSourceEvidence = latest, LastGateDecision = recheck
                }, "merge_preflight_verified", "exact_head_and_policy_pass", attemptId));
                var mergedSha = await _source.MergeAsync(repository, number,
                    reserved.HeadSha, cancellation);
                if (!PrReviewGate.ValidSha(mergedSha))
                    throw new InvalidOperationException("Merge response lacks commit.");
                var merged = _store.Update(repository, number, w =>
                    _store.Append(w with { MergeOutcome = "merged", MergedSha = mergedSha,
                        State = "merged" }, "merged", "source_confirmed", attemptId));
                return new PrMergeReport(merged, checkedReport.Gate, "merged");
            }
            catch (Exception)
            {
                // Even errors before dispatch stay blocked; never infer safe replay.
                var uncertain = _store.Update(repository, number, w =>
                    _store.Append(w with { MergeOutcome = "uncertain", State = "blocked" },
                        "merge_uncertain", "manual_reconciliation_required", attemptId));
                return new PrMergeReport(uncertain, checkedReport.Gate, "uncertain");
            }
        }
        finally { _mergeGate.Release(); }
    }
}
