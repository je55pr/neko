using System.Security.Cryptography;
using System.Text;
using Neko;

namespace Neko.Headless;

internal interface IPrSourceAdapter
{
    Task<PrSourceStatus> ReadAsync(string repository, int number, CancellationToken cancellation);
    Task<string> AuthenticatedPublisherAsync(CancellationToken cancellation);
    // Publish the exact verified worker output at the expected commit. The
    // caller persists a one-use reservation before this external write.
    Task<PrReview> PostReviewAsync(string repository, int number, string expectedHead,
        string body, CancellationToken cancellation);
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

    private bool IsAuthorizedRepository(string repository) =>
        PrWorkflowStore.ValidRepository(repository) &&
        _policy.AllowedRepositories?.Any(allowed =>
            StringComparer.OrdinalIgnoreCase.Equals(allowed, repository)) == true;

    public PrWorkflow? Get(string repository, int number) =>
        IsAuthorizedRepository(repository) ? _store.Get(repository, number) : null;
    public IReadOnlyList<PrWorkflow> List() =>
        _store.List().Where(w => IsAuthorizedRepository(w.Repository)).ToArray();

    private static bool ReviewPostingUnresolved(PrWorkflow workflow) =>
        workflow.Reviewer is {
            ReviewPostAttemptId: not null,
            ReviewPostOutcome: not "posted"
        };

    // The review's declared verdict must agree with a structured count of
    // individually labelled findings. Never trust only a first-line claim
    // of NO_BLOCKERS when the original reviewer reported a P1/P2 defect.
    internal static string ParseReviewerVerdict(string body)
    {
        var lines = body.Split('\n');
        if (lines.Length < 3 || !lines[1].Trim().StartsWith(
                "FINDINGS_COUNT: ", StringComparison.Ordinal))
            throw new InvalidOperationException("Structured reviewer findings count missing.");
        var verdict = lines[0].Trim() switch {
            "REVIEW_VERDICT: NO_BLOCKERS" => "no_blockers",
            "REVIEW_VERDICT: BLOCKERS" => "blockers",
            _ => throw new InvalidOperationException("Reviewer verdict unknown.")
        };
        if (!int.TryParse(lines[1].Trim()["FINDINGS_COUNT: ".Length..],
                out var declared) || declared is < 0 or > 50)
            throw new InvalidOperationException("Reviewer findings count invalid.");

        var headings = lines.Skip(2).Count(raw =>
        {
            var line = raw.TrimStart(' ', '\t', '#', '-', '*');
            return line.Length >= 3 && (line[0] is 'P' or 'p') &&
                line[1] is >= '0' and <= '3' &&
                line[2] is ':' or ' ' or '-';
        });
        if (headings != declared || (verdict == "no_blockers") != (declared == 0))
            throw new InvalidOperationException("Reviewer verdict contradicts findings count.");

        if (verdict == "no_blockers" &&
            new[] { "P0:", "P1:", "P2:", "P3:", "BLOCKERS:" }
                .Any(token => lines.Skip(2).Any(line =>
                    line.Contains(token, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Reviewer reported blockers despite clear verdict.");
        return verdict;
    }

    public async Task<PrWorkflow> CreateAsync(string repository, int number,
        string creator, string developer, string manager, CancellationToken cancellation)
    {
        if (!IsAuthorizedRepository(repository) || number < 1)
            throw new ArgumentException("Repository not authorized.");
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
        var record = Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        var status = await _source.ReadAsync(repository, number, cancellation);
        var decision = PrReviewGate.Evaluate(record, _policy, status, _clock.GetUtcNow());
        // Persist source-verifiable checks and reviewer facts before reporting readiness.
        var stale = false;
        var observed = _store.Update(repository, number, current =>
        {
            if (current.HeadSha != record.HeadSha ||
                current.BaseSha != record.BaseSha ||
                current.ReviewLaunchState != record.ReviewLaunchState ||
                current.Reviewer != record.Reviewer ||
                current.MergeAttemptId != record.MergeAttemptId)
            {
                stale = true;
                return current;
            }
            return current.MergeAttemptId is not null ? current : current with {
                LastSourceEvidence = status, LastGateDecision = decision
            };
        });
        // A source read racing revision rearm or reviewer confirmation must
        // not overwrite newer evidence or report stale readiness.
        if (stale)
            return new PrWorkflowReport(observed, new PrGateDecision(false,
                "blocked", ["inspection_obsoleted_by_workflow_change"], _clock.GetUtcNow()));
        if (observed.MergeAttemptId is not null)
            decision = PrReviewGate.Evaluate(
                observed, _policy, status, _clock.GetUtcNow());
        return new PrWorkflowReport(observed, decision);
    }

    /// <summary>Explicit revision rearm; old evidence is retained only in audit history.</summary>
    public async Task<PrWorkflow> RefreshRevisionAsync(string repository, int number,
        CancellationToken cancellation)
    {
        var current = Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        if ((current.MergeAttemptId is not null &&
            current.MergeOutcome != "rejected_before_dispatch") ||
            current.ReviewLaunchState is "dispatching" or "uncertain" ||
            ReviewPostingUnresolved(current))
            throw new InvalidOperationException("Unresolved external action blocks revision rearm.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.Repository != repository || source.Number != number ||
            source.State != "open" || source.Merged ||
            !PrReviewGate.ValidSha(source.HeadSha) || !PrReviewGate.ValidSha(source.BaseSha))
            throw new InvalidOperationException("Source revision unavailable.");
        return _store.Update(repository, number, existing =>
        {
            if ((existing.MergeAttemptId is not null &&
                existing.MergeOutcome != "rejected_before_dispatch") ||
                existing.ReviewLaunchState is "dispatching" or "uncertain" ||
                ReviewPostingUnresolved(existing) ||
                existing.HeadSha != current.HeadSha || existing.BaseSha != current.BaseSha ||
                existing.MergeAttemptId != current.MergeAttemptId)
                throw new InvalidOperationException("Revision change was concurrent.");
            var changed = existing.HeadSha != source.HeadSha ||
                existing.BaseSha != source.BaseSha;
            var rearmRejectedReview = existing.ReviewLaunchState == "rejected";
            if (!changed && existing.MergeAttemptId is null && !rearmRejectedReview)
                return existing;
            var oldAttempt = existing.MergeAttemptId;
            var resetReviewer = changed || rearmRejectedReview;
            return _store.Append(existing with {
                HeadSha = source.HeadSha, BaseSha = source.BaseSha,
                Reviewer = resetReviewer ? null : existing.Reviewer,
                ReviewLaunchState = resetReviewer ? "unstarted" : existing.ReviewLaunchState,
                ReviewLaunchAt = resetReviewer ? null : existing.ReviewLaunchAt,
                MergeAttemptId = null, MergeOutcome = null,
                MergeAuthorizedBy = null, MergeAuthorizedAt = null,
                State = resetReviewer ? "awaiting_independent_review" : "checks_pending",
                LastSourceEvidence = source, LastGateDecision = null
            }, "revision_rearmed",
                "previous_head:" + existing.HeadSha +
                ";previous_base:" + existing.BaseSha +
                ";safe_attempt:" + (oldAttempt ?? "none"));
        });
    }

    // The launch reservation is durable BEFORE the provider creation call.
    // An interrupted, unconfirmed launch is never repeated automatically.
    public async Task<PrWorkflow> LaunchReviewerAsync(string repository, int number,
        string task, string reviewerAccount, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > 12000 ||
            string.IsNullOrWhiteSpace(reviewerAccount) ||
            reviewerAccount.Length > 100 || reviewerAccount.Any(char.IsControl) ||
            StringComparer.OrdinalIgnoreCase.Equals(reviewerAccount,
                _policy.AutomatedReviewer))
            throw new ArgumentException("Independent reviewer must be distinct.");
        var current = Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        if (current.ReviewLaunchState != "unstarted" || current.MergeAttemptId is not null)
            throw new InvalidOperationException("Review creation already reserved.");
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.Repository != repository || source.Number != number ||
            source.HeadSha != current.HeadSha || source.BaseSha != current.BaseSha ||
            source.State != "open" || source.Merged)
            throw new InvalidOperationException("Source head/base changed.");
        var assignment = "Independent review assignment for " +
            repository + "#" + number + " at head " + current.HeadSha +
            ". Do not merge or post a source review yourself. Return a" +
            " substantive review in this strict format: first line exactly" +
            " REVIEW_VERDICT: NO_BLOCKERS or REVIEW_VERDICT: BLOCKERS;" +
            " second line FINDINGS_COUNT: N, where N is zero for NO_BLOCKERS" +
            " and positive for BLOCKERS. Each blocker must have its own" +
            " P0:/P1:/P2:/P3: heading, matching N. Do not claim NO_BLOCKERS" +
            " anywhere if you report a blocking finding. " + task;
        var prepared = OutboundMessagePreparation.ForNew(assignment).Message ??
            throw new ArgumentException("Reviewer assignment is invalid.");
        var promptSha = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(prepared.Text)));
        _store.Update(repository, number, existing =>
        {
            // Bind the remote assignment to the same immutable revision that
            // was read and included in its prompt. A concurrent pr-refresh
            // must never turn an A-head review task into B-head evidence.
            if (existing.HeadSha != current.HeadSha ||
                existing.BaseSha != current.BaseSha ||
                existing.ReviewLaunchState != "unstarted" ||
                existing.MergeAttemptId is not null)
                throw new InvalidOperationException("Review revision changed during launch.");
            return _store.Append(existing with {
                ReviewLaunchState = "dispatching", ReviewLaunchAt = _clock.GetUtcNow(),
                State = "awaiting_independent_review"
            }, "review_launch_reserved", "remote_creation_may_be_uncertain");
        });
        SendReceipt receipt;
        try
        {
            receipt = await _runtime.CreateAsync(assignment, cancellation);
        }
        catch (CreationWaitCancelledException)
        {
            // The runtime explicitly proves that the creation gate cancelled
            // before any provider dispatch. Clear only this reservation.
            return _store.Update(repository, number, w =>
            {
                if (w.ReviewLaunchState != "dispatching" || w.Reviewer is not null ||
                    w.HeadSha != current.HeadSha || w.BaseSha != current.BaseSha)
                    throw new InvalidOperationException("Review reservation changed.");
                return _store.Append(w with {
                    ReviewLaunchState = "unstarted", ReviewLaunchAt = null,
                    State = "awaiting_independent_review"
                }, "review_launch_not_dispatched", "creation_gate_cancelled");
            });
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
            _runtime.Get(receipt.ConversationId)?.Id != receipt.ConversationId ||
            _runtime.Get(receipt.ConversationId)?.DispatchCount != 1)
            return _store.Update(repository, number, w => _store.Append(w with {
                ReviewLaunchState = receipt.Disposition == SendDisposition.Rejected
                    ? "rejected" : "uncertain", State = "blocked"
            }, "review_launch_unconfirmed", receipt.Disposition.ToString()));
        return _store.Update(repository, number, w => _store.Append(w with {
            ReviewLaunchState = "accepted",
            Reviewer = new PrWorkerEvidence(receipt.ConversationId, w.HeadSha,
                w.ReviewLaunchAt!.Value, false, null, null, null, reviewerAccount,
                CreationPromptSha256: promptSha,
                CreationUserMessageId: receipt.MessageId)
        }, "reviewer_bound", "created_for_current_head"));
    }

    /// <summary>
    /// The host posts the verified worker output itself. A URL echoed by a
    /// worker is never evidence that the worker authored a source review.
    /// </summary>
    public async Task<PrWorkflow> PublishReviewAsync(string repository, int number,
        CancellationToken cancellation)
    {
        var record = Get(repository, number)
            ?? throw new KeyNotFoundException("Workflow unavailable.");
        var worker = record.Reviewer ??
            throw new InvalidOperationException("No bound reviewer.");
        if (record.ReviewLaunchState != "accepted" ||
            record.MergeAttemptId is not null || worker.CreatedForHead != record.HeadSha ||
            worker.ReviewPostAttemptId is not null)
            throw new InvalidOperationException("Reviewer posting is blocked.");

        var history = await _runtime.ReadAsync(worker.ConversationId, cancellation);
        var local = _runtime.Get(worker.ConversationId);
        var userTurns = history.History?.Messages
            .Where(message => message.Role == VisibleMessageRole.User).ToArray();
        var response = history.History?.Messages.LastOrDefault(
            message => message.Role == VisibleMessageRole.Assistant);
        if (!history.ProviderVerified || response is null || userTurns is not { Length: 1 } ||
            local?.Status != "idle" || local.DispatchCount != 1 ||
            local.LastReconciliation is not { Outcome: "restored" } terminal ||
            terminal.PromptSha256 != worker.CreationPromptSha256 ||
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(userTurns[0].Text))) != worker.CreationPromptSha256 ||
            (worker.CreationUserMessageId is not null &&
                userTurns[0].Id != worker.CreationUserMessageId) ||
            (terminal.AssistantMessageId is not null &&
                response.Id != terminal.AssistantMessageId) ||
            string.IsNullOrWhiteSpace(response.Id))
            throw new InvalidOperationException(
                "Original reviewer turn is not authoritatively completed.");
        var body = response.Text.Trim();
        if (body.Length is < 50 or > 12000)
            throw new InvalidOperationException("Substantive reviewer verdict missing.");
        var verdict = ParseReviewerVerdict(body);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var source = await _source.ReadAsync(repository, number, cancellation);
        if (source.Repository != repository || source.Number != number ||
            source.State != "open" || source.HeadSha != record.HeadSha ||
            source.BaseSha != record.BaseSha)
            throw new InvalidOperationException("PR revision changed before review posting.");
        // Discover the authenticated publication identity before reserving a
        // one-use remote write. A typo in reviewer configuration cannot consume
        // the review-post attempt or create an unresolvable external review.
        var principal = await _source.AuthenticatedPublisherAsync(cancellation);
        if (!StringComparer.OrdinalIgnoreCase.Equals(principal, worker.ReviewerAccount) ||
            StringComparer.OrdinalIgnoreCase.Equals(principal, _policy.AutomatedReviewer))
            throw new InvalidOperationException("Review publishing account mismatched.");
        var attemptId = Guid.NewGuid().ToString("N");

        // This one-use durable reservation comes before any external write.
        _store.Update(repository, number, current =>
        {
            if (current.Reviewer?.ReviewPostAttemptId is not null ||
                current.HeadSha != record.HeadSha || current.BaseSha != record.BaseSha)
                throw new InvalidOperationException("Concurrent review posting.");
            return _store.Append(current with {
                Reviewer = current.Reviewer! with {
                    ReviewPostAttemptId = attemptId,
                    ReviewPostOutcome = "uncertain",
                    CompletionMessageId = response.Id,
                    PostedBodySha256 = digest,
                    ReviewerVerdict = verdict
                }, State = "blocked"
            }, "review_post_reserved", "exact_worker_message:" + response.Id, attemptId);
        });

        try
        {
            var receipt = await _source.PostReviewAsync(
                repository, number, record.HeadSha, body, cancellation);
            if (receipt.HeadSha != record.HeadSha ||
                !StringComparer.Ordinal.Equals(receipt.BodySha256, digest) ||
                !StringComparer.OrdinalIgnoreCase.Equals(receipt.Author, worker.ReviewerAccount) ||
                StringComparer.OrdinalIgnoreCase.Equals(receipt.Author, _policy.AutomatedReviewer) ||
                receipt.Verdict is not ("commented" or "approved") ||
                string.IsNullOrWhiteSpace(receipt.Url))
                throw new InvalidOperationException("Published review receipt mismatched.");

            // Re-read authoritative source. Merely receiving a publish receipt
            // is not sufficient to mark independently-reviewed work complete.
            var verified = await _source.ReadAsync(repository, number, cancellation);
            if (verified.HeadSha != record.HeadSha || verified.BaseSha != record.BaseSha ||
                !verified.Reviews.Any(r => r.Url == receipt.Url &&
                    r.Author == receipt.Author && r.HeadSha == record.HeadSha &&
                    r.BodySha256 == digest && r.Substantive &&
                    r.Verdict == receipt.Verdict))
                throw new InvalidOperationException("Published review could not be verified.");
            return _store.Update(repository, number, current =>
                _store.Append(current with {
                    Reviewer = current.Reviewer! with {
                        Completed = true, CompletedAt = _clock.GetUtcNow(),
                        ReviewUrl = receipt.Url, Verdict = receipt.Verdict,
                        ReviewerAccount = receipt.Author,
                        ReviewPostOutcome = "posted"
                    }, LastSourceEvidence = verified,
                    State = verdict == "no_blockers" ? "checks_pending" : "changes_requested"
                }, "review_post_verified", "source_receipt_verified", attemptId));
        }
        catch
        {
            // The remote review may have been accepted. Never repeat this
            // write automatically or simply discard its one-use reservation.
            _store.Update(repository, number, current =>
                _store.Append(current with {
                    Reviewer = current.Reviewer! with { ReviewPostOutcome = "uncertain" },
                    State = "blocked"
                }, "review_post_uncertain", "manual_source_reconciliation_required", attemptId));
            throw;
        }
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
                    MergeAuthorizedBy = "authenticated_host_operator"
                }, "merge_reserved", "expected_head:" + w.HeadSha +
                    ";authenticated_host_operator", attemptId);
            });
            var externalMergeInvoked = false;
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
                        _store.Append(w with { MergeOutcome = "rejected_before_dispatch" },
                            "merge_preflight_rejected", string.Join(",", recheck.Blockers), attemptId));
                    return new PrMergeReport(stopped, recheck, "rejected_before_dispatch");
                }
                // Fresh preflight proof must also be durable before the merge write.
                _store.Update(repository, number, w => _store.Append(w with {
                    LastSourceEvidence = latest, LastGateDecision = recheck
                }, "merge_preflight_verified", "exact_head_and_policy_pass", attemptId));
                externalMergeInvoked = true;
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
                // Reads and durable preflight writes cannot dispatch the merge.
                // Once the external call is entered, the outcome stays uncertain.
                var outcome = externalMergeInvoked
                    ? "uncertain" : "rejected_before_dispatch";
                var result = _store.Update(repository, number, w =>
                    _store.Append(w with { MergeOutcome = outcome, State = "blocked" },
                        externalMergeInvoked ? "merge_uncertain" : "merge_preflight_failed",
                        externalMergeInvoked
                            ? "manual_reconciliation_required" : "no_external_merge_invoked",
                        attemptId));
                return new PrMergeReport(result, checkedReport.Gate, outcome);
            }
        }
        finally { _mergeGate.Release(); }
    }
}
