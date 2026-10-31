using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Neko;

namespace Neko.Headless;

// Only thrown when the local creation gate rejected a cancelled waiter.
// Unlike provider or journal failures, this proves this call never dispatched.
internal sealed class CreationWaitCancelledException(CancellationToken cancellation)
    : OperationCanceledException("Creation cancelled before dispatch.", cancellation);

/// <summary>
/// Single-process headless conversation owner. Pending operations survive restarts
/// as unresolved until authoritative provider state is reconciled.
/// </summary>
internal sealed class ChatRuntime : IDisposable
{
    private readonly IConversationProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly ConversationCommandCoordinator _commands;
    private readonly ChatJournal _journal;
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, ConversationUpdateFeed> _updates = new();
    private readonly ConcurrentDictionary<string, Task> _pumps = new();
    private readonly CancellationTokenSource _lifetime = new();

    public ChatRuntime(IConversationProvider provider, ChatJournal journal,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commands = new ConversationCommandCoordinator(provider);
        _journal = journal;
        // The previous process may have died after a remote submission. Mark
        // in-flight operations unresolved until the provider can reconcile them.
        foreach (var record in _journal.List())
        {
            if (record.Status is "sending" or "generating" or "stopping")
                _journal.Update(record.Id, current => current with { Status = "uncertain" });
            else if (record.Status == "unavailable")
                // Older builds set this after a failed read without sending.
                _journal.Update(record.Id, current => current with { Status = "idle" });
        }
    }

    public IReadOnlyList<ChatRecord> List() => _journal.List();
    public ChatRecord? Get(string id) => _journal.Get(id);

    /// <summary>
    /// Read local public-event metrics only. A turn token prevents pairing a
    /// previous journal snapshot with a newer feed during concurrent follow-ups.
    /// Missing metrics mean not observed in this process, not zero events.
    /// </summary>
    public ConversationStatus? GetStatus(string id)
    {
        var record = _journal.Get(id);
        if (record is null) return null;
        ConversationProgressSnapshot? progress = null;
        if (record.ProgressTurnId is not null &&
            _updates.TryGetValue(id, out var feed) &&
            feed.TurnId == record.ProgressTurnId)
            progress = feed.Snapshot();

        return new ConversationStatus(
            record.Id, record.Status, record.UpdatedAt,
            progress?.StartedAt, progress?.ElapsedMilliseconds,
            progress?.LastPublicUpdateAt, progress?.PublicEventCount,
            progress?.LastPublicUpdateType, progress?.IsTerminal,
            progress?.TerminalOperation);
    }

    private static string PromptHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public async Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation,
        string? ownerId = null)
    {
        try { await _creationGate.WaitAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new CreationWaitCancelledException(cancellation);
        }
        try
        {
            var prepared = OutboundMessagePreparation.ForNew(text);
            if (prepared.Message is null)
                return new SendReceipt(SendDisposition.Rejected, "invalid_submission");
            if (!_journal.TryStartCreation(PromptHash(prepared.Message.Text), ownerId))
                return new SendReceipt(SendDisposition.Rejected, "previous_creation_unresolved");

            var feed = new ConversationUpdateFeed(_timeProvider);
            SendReceipt receipt;
            try { receipt = await _commands.CreateAsync(prepared.Message, cancellation); }
            catch (Exception) { receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"); }

            // Persist server-correlated identity even if history verification
            // is still pending. Never infer an ID from a matching prompt.
            var usableId = !string.IsNullOrWhiteSpace(receipt.ConversationId) &&
                receipt.ConversationId.Length <= OutboundMessagePreparation.MaxConversationIdLength &&
                !receipt.ConversationId.Any(char.IsControl);
            if (receipt.Disposition != SendDisposition.Rejected && usableId)
                _journal.SetCreationCorrelation(receipt.ConversationId!);
            if (receipt.Disposition == SendDisposition.Accepted && !usableId)
                return new SendReceipt(SendDisposition.Uncertain, "conversation_id_unknown");

            if (receipt.Disposition == SendDisposition.Rejected)
                _journal.ClearCreation();
            else if (receipt.Disposition == SendDisposition.Accepted)
            {
                _journal.Set(new ChatRecord(receipt.ConversationId!, "generating",
                    ProgressTurnId: feed.TurnId,
                    PendingTurn: new PendingTurnEvidence(
                        PromptHash(prepared.Message.Text), Array.Empty<string>(),
                        AcceptedSubmission: true,
                        CorrelatedUserMessageId: receipt.MessageId)));
                // Manager creation must bind its registry identity durably
                // before the global creation guard can be released.
                if (ownerId is null) _journal.ClearCreation();
                StartPump(receipt.ConversationId!, feed);
            }
            // An unconfirmed creation retains the on-disk lock; no replay.
            return receipt;
        }
        finally { _creationGate.Release(); }
    }

    public async Task<SendReceipt> SendAsync(
        string id, string text, CancellationToken cancellation,
        string? operationId = null)
    {
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var prepared = OutboundMessagePreparation.ForExisting(id, text);
            if (prepared.Message is null)
                return new SendReceipt(SendDisposition.Rejected, "invalid_submission");
            var current = _journal.Get(id);
            if (current is null)
                return new SendReceipt(SendDisposition.Rejected, "unknown_conversation");
            if (current.Status != "idle")
                return new SendReceipt(SendDisposition.Rejected, "conversation_not_idle");
            // A cached transcript alone does not establish that the adapter can
            // resume the remote conversation after a process restart.
            VisibleConversation? upstream;
            try { upstream = await _provider.ReadAsync(id, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { upstream = null; }
            if (upstream is null)
            {
                // A read can fail transiently (e.g. rate limiting). No send
                // occurred, so keep the chat idle for a later guarded attempt.
                return new SendReceipt(SendDisposition.Rejected, "provider_history_unavailable")
                {
                    // History preflight failed before any submission. A later
                    // explicit attempt is safe, but never submit without a fresh read.
                    Retryable = true,
                    RetryAfterSeconds = (_provider as IProviderRateLimitStatus)?
                        .RateLimitRetryAfterSeconds
                };
            }
            // Bind the new stream before making "sending" observable, so
            // watchers cannot replay a terminal event from the previous turn.
            var feed=new ConversationUpdateFeed(_timeProvider);
            _updates.TryGetValue(id,out var previousFeed);
            _updates[id]=feed;
            try
            {
                _journal.Update(id, latest => latest with {
                    Status="sending",History=upstream,
                    ProgressTurnId=feed.TurnId,
                    PendingTurn=new PendingTurnEvidence(PromptHash(prepared.Message.Text),
                        upstream.Messages.Select(message=>message.Id).ToArray(),
                        OperationId: operationId)
                });
            }
            catch
            {
                if(previousFeed is not null)_updates[id]=previousFeed;
                else _updates.TryRemove(id,out _);
                feed.Publish(new PublicConversationUpdate(id,
                    PublicUpdateKind.Final,PublicUpdateOperation.Fail));
                throw;
            }

            SendReceipt receipt;
            try { receipt = await _commands.SendAsync(prepared.Message, cancellation); }
            catch (Exception) { receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown"); }

            switch (receipt.Disposition)
            {
                case SendDisposition.Rejected:
                    _journal.Update(id, latest => latest with {
                        Status = "idle", PendingTurn = null
                    });
                    feed.Publish(new PublicConversationUpdate(id,PublicUpdateKind.Final,
                        PublicUpdateOperation.Fail));
                    break;
                case SendDisposition.Accepted:
                    _journal.Update(id, latest => latest with {
                        Status = "generating",
                        PendingTurn = latest.PendingTurn is { } turn
                            ? turn with { AcceptedSubmission = true,
                                CorrelatedUserMessageId = receipt.MessageId }
                            : null
                    });
                    StartPump(id,feed);
                    break;
                default:
                    _journal.Update(id, latest => latest with {
                        Status = "uncertain",
                        PendingTurn = latest.PendingTurn is { } turn
                            ? turn with { CorrelatedUserMessageId = receipt.MessageId }
                            : null
                    });
                    feed.Publish(new PublicConversationUpdate(id,PublicUpdateKind.Final,
                        PublicUpdateOperation.Fail));
                    break;
            }
            return receipt;
        }
        finally { gate.Release(); }
    }

    public async Task<StopReceipt> StopAsync(string id, CancellationToken cancellation)
    {
        // Unsupported Stop is a pure no-op, including for the on-disk journal.
        if (!_provider.SupportsStop)
            return new StopReceipt(StopDisposition.Unsupported, "stop_not_supported");
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var current = _journal.Get(id);
            if (current is null)
                return new StopReceipt(StopDisposition.NotActive, "unknown_conversation");
            if (current.Status != "generating")
                return new StopReceipt(StopDisposition.NotActive, "conversation_not_generating");

            var transition=_journal.Update(id,latest =>
                latest.Status=="generating"?
                    latest with {Status="stopping"}:latest);
            if(transition?.Status!="stopping")
                return new StopReceipt(StopDisposition.NotActive,
                    "conversation_already_finished");
            StopReceipt receipt;
            try { receipt = await _commands.StopAsync(id, cancellation); }
            catch (Exception) { receipt = new StopReceipt(StopDisposition.Uncertain, "stop_unknown"); }

            if (receipt.Disposition is StopDisposition.NotActive or StopDisposition.Unsupported)
                _journal.Update(id, latest => latest.Status == "stopping"
                    ? latest with { Status = "generating" } : latest);
            else if (receipt.Disposition == StopDisposition.Uncertain)
                _journal.Update(id, latest => latest.Status=="stopping"
                    ? latest with { Status = "stop_uncertain" }: latest);
            // A requested stop remains pending until an authoritative terminal event.
            return receipt;
        }
        finally { gate.Release(); }
    }

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
                    inspection.VerifiedComplete ? "history_not_correlated" : inspection.Reason);
            var now = _timeProvider.GetUtcNow();
            var decision = new ReconciliationDecision(now, "restored",
                inspection.Reason, turn.PromptSha256,
                inspection.UserMessageId, inspection.AssistantMessageId,
                turn.OperationId);
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
                prior.LastReconciliation is { Outcome: "restored", OperationId: null } decision &&
                decision.PromptSha256 == pending.PromptSha256 &&
                prior.History is { } previousHistory &&
                previousHistory.Messages.Count(message =>
                    message.Role == VisibleMessageRole.User) == 1 &&
                prior.History.Messages[0].Role == VisibleMessageRole.User &&
                PromptHash(prior.History.Messages[0].Text) == pending.PromptSha256)
            {
                if (ownerId is null) _journal.ClearCreation();
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
                    ? "history_not_correlated" : inspection.Reason;
                _journal.RecordCreationCheck(reason);
                return new("blocked", reason, id,
                    RetryAfterSeconds: RateLimitSeconds);
            }
            var proof = new ReconciliationDecision(_timeProvider.GetUtcNow(),
                "restored", inspection.Reason, pending.PromptSha256,
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
            if (ownerId is null) _journal.ClearCreation();
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
        _journal.ClearCreation();
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
        _journal.ClearCreation();
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

    private ReconciliationResult Blocked(string id, string hash, string reason)
    {
        _journal.Update(id, latest => latest.Status == "uncertain"
            ? latest with { LastReconciliation = new ReconciliationDecision(
                _timeProvider.GetUtcNow(), "blocked", reason, hash) }
            : latest);
        return new("blocked", reason, id, RetryAfterSeconds: RateLimitSeconds);
    }

    internal async Task<HistoryReadResult> ReadAsync(string id, CancellationToken cancellation)
    {
        // Local registration, not provider availability, determines whether an ID is known.
        if (_journal.Get(id) is null) return new HistoryReadResult(false, null);
        try
        {
            var current = await _provider.ReadAsync(id, cancellation);
            if (current is not null)
            {
                _journal.Update(id, latest => latest with { History = current });
                return new HistoryReadResult(true, current, ProviderVerified: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { /* Preserve the most recent local checkpoint. */ }
        // Re-read after awaiting the provider: the stream pump may have saved
        // a transcript while this request was waiting for an unavailable read.
        return new HistoryReadResult(true, _journal.Get(id)?.History);
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        if (_journal.Get(id) is null) yield break;
        if (!_updates.TryGetValue(id, out var feed))
        {
            var record = _journal.Get(id);
            yield return new PublicConversationUpdate(
                id, PublicUpdateKind.Final,
                record?.Status == "idle"
                    ? PublicUpdateOperation.Complete : PublicUpdateOperation.Fail);
            yield break;
        }
        await foreach (var update in feed.WatchAsync(cancellation))
            yield return update;
    }

    private void StartPump(string id, ConversationUpdateFeed feed)
    {
        _updates[id] = feed;
        _pumps[id] = Task.Run(async () =>
        {
            var terminalPublished = false;
            try
            {
                await foreach (var update in _provider.WatchAsync(id, _lifetime.Token))
                {
                    if (update.Kind != PublicUpdateKind.Final ||
                        update.Operation is not (PublicUpdateOperation.Complete or PublicUpdateOperation.Fail
                            or PublicUpdateOperation.ObservationExpired))
                    {
                        feed.Publish(update);
                        continue;
                    }
                    VisibleConversation? snapshot = null;
                    try { snapshot = await _provider.ReadAsync(id, _lifetime.Token); }
                    catch (Exception) { /* Keep existing snapshot. */ }
                    // Do not acknowledge a remote completion until its local
                    // checkpoint is durable. A failed write must close the feed
                    // with Fail instead of leaving SSE watchers waiting forever.
                    if (_journal.Update(id, latest =>
                        latest.Status == "idle" &&
                        latest.LastReconciliation is { Outcome: "restored" } &&
                        latest.PendingTurn is null
                            ? latest : latest with {
                                Status = update.Operation == PublicUpdateOperation.Complete
                                    ? "idle" : "uncertain",
                                History = snapshot ?? latest.History,
                                PendingTurn = update.Operation == PublicUpdateOperation.Complete
                                    ? null : latest.PendingTurn,
                                LastReconciliation = update.Operation == PublicUpdateOperation.Complete &&
                                    latest.PendingTurn is { } completed
                                    ? new ReconciliationDecision(_timeProvider.GetUtcNow(),
                                        "restored", "provider_terminal_observed",
                                        completed.PromptSha256,
                                        AssistantMessageId: update.MessageId,
                                        OperationId: completed.OperationId)
                                    : latest.LastReconciliation
                            }) is null)
                        throw new IOException("The terminal checkpoint is unavailable.");
                    feed.Publish(update);
                    terminalPublished = true;
                    break;
                }
            }
            catch (Exception) { /* A failed checkpoint or stream is locally uncertain. */ }
            finally
            {
                if (!terminalPublished)
                {
                    try
                    {
                        // Best effort only: if storage remains unavailable, the
                        // journal may be stale, but live consumers must finish.
                        _journal.Update(id, current =>
                            current.Status == "idle" &&
                            current.LastReconciliation is { Outcome: "restored" } &&
                            current.PendingTurn is null ? current :
                                current with { Status = "uncertain" });
                    }
                    catch (Exception) { /* Never suppress the terminal feed event. */ }
                    feed.Publish(new PublicConversationUpdate(
                        Guid.NewGuid().ToString("D"), PublicUpdateKind.Final, PublicUpdateOperation.Fail));
                }
            }
        });
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try
        {
            // Do not release provider resources while a stream consumer is still
            // accessing them. A noncooperative adapter has a bounded grace period.
            Task.WhenAll(_pumps.Values).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) { /* Cancellation is expected on shutdown. */ }
        _lifetime.Dispose();
        if (_provider is IDisposable disposable) disposable.Dispose();
    }
}
