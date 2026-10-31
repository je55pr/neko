using Neko;

namespace Neko.Headless;

internal sealed partial class ChatRuntime
{
    private static bool MatchesInspection(
        ReconciliationProbe probe, ReconciliationInspection inspection)
    {
        if (!inspection.VerifiedComplete || inspection.History is null ||
            inspection.History.Id != probe.ConversationId ||
            string.IsNullOrWhiteSpace(inspection.UserMessageId) ||
            string.IsNullOrWhiteSpace(inspection.AssistantMessageId))
            return false;
        var all = inspection.History.Messages;
        var baseline = probe.BaselineMessageIds;
        if (baseline is null || all.Count < baseline.Count + 2 ||
            all.Count > 100000 || baseline.Count > 100000)
            return false;
        for (var i = 0; i < baseline.Count; i++)
            if (string.IsNullOrWhiteSpace(baseline[i]) ||
                baseline[i] != all[i].Id) return false;
        var suffix = all.Skip(baseline.Count).ToArray();
        if (suffix[0].Role != VisibleMessageRole.User ||
            suffix.Count(m => m.Role == VisibleMessageRole.User) != 1 ||
            suffix.Skip(1).Any(m => m.Role != VisibleMessageRole.Assistant) ||
            suffix[0].Id != inspection.UserMessageId ||
            suffix[^1].Id != inspection.AssistantMessageId ||
            PromptHash(suffix[0].Text) != probe.PromptSha256 ||
            (probe.CorrelatedUserMessageId is not null &&
             suffix[0].Id != probe.CorrelatedUserMessageId))
            return false;
        return all.All(m => !string.IsNullOrWhiteSpace(m.Id)) &&
            all.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == all.Count;
    }

    /// <summary>Read-only remote inspection; only an exact verified terminal
    /// turn and a durable checkpoint can release the local send guard.</summary>
    public async Task<ReconciliationResult> ReconcileAsync(
        string id, CancellationToken cancellation)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var record = _journal.Get(id);
            if (record is null) return new("not_found", "unknown_conversation");
            if (record.Status != "uncertain")
                return new("not_applicable", "conversation_not_uncertain", id);
            var turn = record.PendingTurn;
            if (turn is null || string.IsNullOrWhiteSpace(turn.PromptSha256) ||
                turn.BaselineMessageIds is null)
                return Blocked(id, turn?.PromptSha256 ?? "", "missing_correlation_evidence");
            if (!turn.AcceptedSubmission &&
                string.IsNullOrWhiteSpace(turn.CorrelatedUserMessageId))
                return Blocked(id, turn.PromptSha256, "missing_dispatch_correlation");

            var probe = new ReconciliationProbe(id, turn.PromptSha256,
                turn.BaselineMessageIds, turn.AcceptedSubmission,
                CorrelatedUserMessageId: turn.CorrelatedUserMessageId);
            var inspection = await InspectAsync(probe, cancellation);
            if (!MatchesInspection(probe, inspection))
                return Blocked(id, turn.PromptSha256,
                    inspection.VerifiedComplete ? "history_not_correlated" :
                        SafeInspectionReason(inspection.Reason));
            var now = _timeProvider.GetUtcNow();
            var decision = new ReconciliationDecision(now, "restored",
                "authoritative_turn_complete", turn.PromptSha256,
                inspection.UserMessageId, inspection.AssistantMessageId,
                turn.OperationId, record.ProgressTurnId, record.DispatchCount);
            var saved = _journal.Update(id, current =>
                current.Status == "uncertain" &&
                current.PendingTurn is { } pending &&
                pending.PromptSha256 == turn.PromptSha256 &&
                pending.OperationId == turn.OperationId &&
                pending.BaselineMessageIds.SequenceEqual(turn.BaselineMessageIds)
                    ? current with { Status = "idle", PendingTurn = null,
                        History = inspection.History, LastReconciliation = decision }
                    : current);
            if (saved?.LastReconciliation != decision || saved.Status != "idle")
                return new("blocked", "concurrent_state_change", id);
            // Release the process-local latch only AFTER the durable
            // authoritative checkpoint has committed successfully.
            _commands.ConfirmReconciled(id);
            return new("restored", "authoritative_turn_complete", id,
                inspection.UserMessageId, inspection.AssistantMessageId);
        }
        finally { gate.Release(); }
    }

    /// <summary>A server-issued conversation identity is required to bind an
    /// interrupted creation. Matching user text alone is never sufficient.</summary>
    public async Task<ReconciliationResult> ReconcileCreationAsync(
        string? ownerId, CancellationToken cancellation)
    {
        await _creationGate.WaitAsync(cancellation);
        try
        {
            var pending = _journal.GetPendingCreation();
            if (pending is null) return new("not_applicable", "no_pending_creation");
            if (pending.OwnerId != ownerId)
                return new("blocked", "creation_owner_mismatch");
            if (string.IsNullOrWhiteSpace(pending.PromptSha256) ||
                string.IsNullOrWhiteSpace(pending.CorrelatedConversationId))
            {
                _journal.RecordCreationCheck("missing_creation_correlation");
                return new("blocked", "missing_creation_correlation");
            }
            var id = pending.CorrelatedConversationId;
            var prior = _journal.Get(id);
            if (prior?.Status == "idle" && prior.PendingTurn is null &&
                prior.LastReconciliation is {
                    Outcome: "restored", Reason: "authoritative_turn_complete",
                    OperationId: null } decision &&
                decision.PromptSha256 == pending.PromptSha256 &&
                prior.History is { } previousHistory &&
                previousHistory.Messages.Count(message =>
                    message.Role == VisibleMessageRole.User) == 1 &&
                prior.History.Messages[0].Role == VisibleMessageRole.User &&
                PromptHash(prior.History.Messages[0].Text) == pending.PromptSha256)
            {
                if (ownerId is null)
                {
                    _journal.ClearCreation();
                    _commands.ConfirmCreationReconciled();
                }
                return new("restored", "authoritative_turn_complete", id,
                    decision.UserMessageId, decision.AssistantMessageId);
            }
            // A crash may have persisted the conversation but not yet cleared
            // creation.pending. This is safe to examine only if no follow-up
            // turn has been recorded under the same identity.
            if (prior is not null &&
                (prior.PendingTurn is { } active &&
                    (active.PromptSha256 != pending.PromptSha256 ||
                     active.BaselineMessageIds.Count != 0 ||
                     !active.AcceptedSubmission) ||
                 prior.Status is not ("idle" or "generating" or "uncertain")))
                return new("blocked", "conversation_identity_already_registered", id);

            var probe = new ReconciliationProbe(id, pending.PromptSha256,
                Array.Empty<string>(), CorrelatedCreation: true);
            var inspection = await InspectAsync(probe, cancellation);
            if (!MatchesInspection(probe, inspection))
            {
                var reason=inspection.VerifiedComplete
                    ? "history_not_correlated" : SafeInspectionReason(inspection.Reason);
                _journal.RecordCreationCheck(reason);
                return new("blocked", reason, id,
                    RetryAfterSeconds: RateLimitSeconds);
            }
            var proof = new ReconciliationDecision(_timeProvider.GetUtcNow(),
                "restored", "authoritative_turn_complete", pending.PromptSha256,
                inspection.UserMessageId, inspection.AssistantMessageId);
            if (prior is null)
                _journal.Set(new ChatRecord(id, "idle", inspection.History,
                    LastReconciliation: proof));
            else
            {
                var saved=_journal.Update(id, current =>
                    (current.PendingTurn is null ||
                     current.PendingTurn.PromptSha256 == pending.PromptSha256 &&
                     current.PendingTurn.BaselineMessageIds.Count == 0 &&
                     current.PendingTurn.AcceptedSubmission) &&
                    current.Status is "idle" or "generating" or "uncertain"
                        ? current with { Status="idle", History=inspection.History,
                            PendingTurn=null, LastReconciliation=proof }
                        : current);
                if (saved?.LastReconciliation != proof)
                    return new("blocked", "concurrent_state_change", id);
            }
            // Manager binding is a separate durable write. Keep the global
            // creation guard until that write succeeds, including after crash.
            if (ownerId is null)
            {
                _journal.ClearCreation();
                _commands.ConfirmCreationReconciled();
            }
            return new("restored", "authoritative_turn_complete", id,
                inspection.UserMessageId, inspection.AssistantMessageId);
        }
        finally { _creationGate.Release(); }
    }

    public bool FinishAcceptedManagerCreation(string managerId, string conversationId)
    {
        var pending = _journal.GetPendingCreation();
        var record = _journal.Get(conversationId);
        if (pending?.OwnerId != managerId ||
            pending.CorrelatedConversationId != conversationId ||
            record is null || (record.ProgressTurnId is null &&
                record.LastReconciliation is not { Outcome: "restored" }))
            return false;
        if (!_journal.ClearCorrelatedCreation(managerId, conversationId))
            return false;
        _commands.ConfirmCreationReconciled();
        return true;
    }

    public bool HasVerifiedManagerTurn(string conversationId, string? operationId)
    {
        var record = _journal.Get(conversationId);
        return operationId is not null && record?.Status == "idle" &&
            record.LastReconciliation is { Outcome: "restored" } proof &&
            proof.OperationId == operationId;
    }

    public void FinishReconciledManagerCreation(string managerId, string conversationId)
    {
        var pending = _journal.GetPendingCreation();
        var record = _journal.Get(conversationId);
        if (pending?.OwnerId != managerId ||
            pending.CorrelatedConversationId != conversationId ||
            record?.Status != "idle" ||
            record.LastReconciliation is not { Outcome: "restored" })
            throw new InvalidOperationException("Creation binding is not verified.");
        if (!_journal.ClearCorrelatedCreation(managerId, conversationId))
            throw new InvalidOperationException("Creation guard changed before binding.");
        _commands.ConfirmCreationReconciled();
    }

    private int? RateLimitSeconds =>
        (_provider as IProviderRateLimitStatus)?.RateLimitRetryAfterSeconds;

    private async Task<ReconciliationInspection> InspectAsync(
        ReconciliationProbe probe, CancellationToken cancellation)
    {
        if (_provider is not IAuthoritativeReconciliationProvider provider)
            return new(false, "verification_unsupported");
        try { return await provider.InspectAsync(probe, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw; }
        catch (Exception) { return new(false, "verification_unavailable"); }
    }

    // Third-party adapter errors are untrusted. Never persist or echo a raw
    // remote message, endpoint, credential or provider identifier as a reason.
    private static string SafeInspectionReason(string? reason) => reason switch
    {
        "provider_rate_limited" or "scope_not_verified" or
        "history_unavailable" or "status_unavailable" or
        "remote_not_verified_complete" or "remote_terminal_failure" or
        "verification_unavailable" or "verification_unsupported" or
        "history_not_correlated" or "missing_dispatch_correlation" or
        "turn_mismatch" or "ambiguous_remote_turn" or
        "invalid_conversation_id" => reason,
        _ => "verification_unavailable"
    };

    private ReconciliationResult Blocked(string id, string hash, string reason)
    {
        _journal.Update(id, latest => latest.Status == "uncertain"
            ? latest with { LastReconciliation = new ReconciliationDecision(
                _timeProvider.GetUtcNow(), "blocked", reason, hash) }
            : latest);
        return new("blocked", reason, id, RetryAfterSeconds: RateLimitSeconds);
    }

}
