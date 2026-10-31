using System.Collections.Concurrent;
using Neko;

namespace Neko.Headless;

internal sealed record ManagerSnapshot(
    string Id, string? ConversationId, string LifecycleState,
    string Availability, string? CheckpointPointer,
    IReadOnlyList<string> OutstandingJobReferences,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record ManagerCreateResult(ManagerSnapshot Manager, SendReceipt Receipt);
internal sealed record ManagerSendResult(ManagerSnapshot Manager, SendReceipt Receipt);
internal sealed record ManagerHistoryResult(string? ConversationId, VisibleConversation? History);
internal sealed record ManagerReconcileResult(ManagerSnapshot Manager, ReconciliationResult Reconciliation);

/// <summary>
/// Stable local manager addresses. All remote operations use the existing
/// conversation runtime and its durable at-most-once safety gates.
/// </summary>
internal sealed class ManagerService
{
    private readonly ManagerRegistry _registry;
    private readonly ChatRuntime _runtime;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendGates = new();
    private readonly ConcurrentDictionary<string, string> _inboxBlocks = new();

    // If the inbox reserved a remote dispatch but the process died before the
    // manager journal took over, fail closed even if the conversation is idle.
    // Keep an in-process latch if registry storage itself is unavailable.
    public void BlockForInboxUncertainty(string id, string operationId)
    {
        if (_registry.Get(id) is null) return;
        _inboxBlocks[id] = operationId;
        _registry.Update(id, record =>
            record.LifecycleState is "idle" or "generating" or "sending"
                ? record with { LifecycleState = "uncertain",
                    PendingOperationId = operationId } : record);
    }

    private void ReleaseVerifiedInboxBlock(string id, string conversationId)
    {
        if (!_inboxBlocks.TryGetValue(id, out var operationId)) return;
        var record = _runtime.Get(conversationId);
        if (record?.Status == "idle" &&
            record.LastReconciliation is { Outcome: "restored" } proof &&
            proof.OperationId == operationId)
            _inboxBlocks.TryRemove(id, out _);
    }

    public ManagerService(ManagerRegistry registry, ChatRuntime runtime)
    {
        _registry = registry;
        _runtime = runtime;

        // A process exit during a manager creation or submission cannot
        // establish whether it reached the remote provider. Never replay.
        foreach (var record in _registry.List())
            if (record.LifecycleState is "creating" or "sending")
                _registry.Update(record.Id, current => current with {
                    LifecycleState = "uncertain"
                });
    }

    public IReadOnlyList<ManagerSnapshot> List() =>
        _registry.List().Select(Snapshot).ToArray();

    public ManagerSnapshot? Get(string id)
    {
        var record = _registry.Get(id);
        return record is null ? null : Snapshot(record);
    }

    /// <summary>
    /// GET probes availability but never uses a failed read to erase a
    /// durable mapping or to claim the conversation has ended.
    /// </summary>
    public async Task<ManagerSnapshot?> GetAsync(string id, CancellationToken cancellation)
    {
        var current = Get(id);
        if (current is null || current.ConversationId is null)
            return current;
        var read = await _runtime.ReadAsync(current.ConversationId, cancellation);
        var refreshed = Get(id)!;
        return refreshed with {
            Availability = read.Known && read.ProviderVerified
                ? "available" : "temporarily_unavailable"
        };
    }

    public async Task<ManagerCreateResult> CreateAsync(
        string id, string text, CancellationToken cancellation)
    {
        if (!ManagerRegistry.IsValidId(id))
            throw new ArgumentException("Invalid manager ID.", nameof(id));
        if (!OutboundMessagePreparation.ForNew(text).IsReady)
            throw new ArgumentException("Invalid submission.", nameof(text));
        cancellation.ThrowIfCancellationRequested();

        if (!_registry.TryReserve(id, out var reserved))
            return new ManagerCreateResult(Snapshot(reserved),
                new SendReceipt(SendDisposition.Rejected, "manager_already_exists"));

        SendReceipt receipt;
        try
        {
            receipt = await _runtime.CreateAsync(text, cancellation, id);
        }
        catch (CreationWaitCancelledException) when (cancellation.IsCancellationRequested)
        {
            // The creation gate rejected this waiter before any dispatch or
            // journal latch. Only an explicit subsequent request can retry.
            receipt = new SendReceipt(SendDisposition.Rejected, "request_cancelled")
                { Retryable = true };
        }
        catch (Exception)
        {
            // Unknown failures after entry into the creation gate may have
            // delivered remotely. Do not convert those to safe cancellations.
            receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown");
        }

        if (receipt.Disposition == SendDisposition.Accepted &&
            !HasConfirmedBinding(receipt.ConversationId))
            receipt = new SendReceipt(SendDisposition.Uncertain,
                "conversation_binding_unconfirmed");

        var conversationId = receipt.Disposition == SendDisposition.Accepted
            ? receipt.ConversationId : null;
        var state = receipt.Disposition switch {
            SendDisposition.Rejected => "rejected",
            SendDisposition.Accepted when !string.IsNullOrWhiteSpace(conversationId)
                => _runtime.Get(conversationId)?.Status ?? "uncertain",
            _ => "uncertain"
        };
        var updated = _registry.Update(id, current => current with {
            ConversationId = conversationId,
            CheckpointPointer = conversationId is null
                ? null : ManagerRegistry.CheckpointFor(conversationId),
            LifecycleState = state
        });
        if (receipt.Disposition == SendDisposition.Accepted && conversationId is not null &&
            !_runtime.FinishAcceptedManagerCreation(id, conversationId))
            throw new InvalidOperationException("Creation binding checkpoint unavailable.");
        return new ManagerCreateResult(Snapshot(updated), receipt);
    }

    public async Task<ManagerSendResult?> SendAsync(
        string id, string text, CancellationToken cancellation,
        string? operationId = null)
    {
        // Unknown names must not allocate permanent per-manager gates.
        if (_registry.Get(id) is null) return null;
        var gate = _sendGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var current = Get(id);
            if (current is null) return null;
            if (_inboxBlocks.ContainsKey(id))
                return new ManagerSendResult(current,
                    new SendReceipt(SendDisposition.Rejected, "inbox_uncertainty_blocked"));
            if (current.ConversationId is null || current.LifecycleState != "idle")
                return new ManagerSendResult(current,
                    new SendReceipt(SendDisposition.Rejected, "manager_not_idle"));
            if (!OutboundMessagePreparation.ForExisting(current.ConversationId, text).IsReady)
                return new ManagerSendResult(current,
                    new SendReceipt(SendDisposition.Rejected, "invalid_submission"));

            // Persist the guard before the remote operation, including before
            // potentially slow history preflight. A crash requires review.
            operationId ??= Guid.NewGuid().ToString("N");
            _registry.Update(id, record => record with {
                LifecycleState = "sending", PendingOperationId = operationId
            });

            SendReceipt receipt;
            try { receipt = await _runtime.SendAsync(current.ConversationId, text, cancellation, operationId); }
            catch (OperationCanceledException) when (
                cancellation.IsCancellationRequested &&
                _runtime.Get(current.ConversationId)?.Status == "idle")
            {
                // The runtime has not yet written its dispatch checkpoint.
                // It was cancelled at its gate or during provider preflight.
                receipt = new SendReceipt(SendDisposition.Rejected, "request_cancelled")
                    { Retryable = true };
            }
            catch (Exception)
            {
                receipt = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown");
            }

            var journalState = _runtime.Get(current.ConversationId)?.Status;
            var state = receipt.Disposition == SendDisposition.Uncertain
                ? "uncertain" : journalState ?? "uncertain";
            var updated = _registry.Update(id, record => record with {
                LifecycleState = state
            });
            return new ManagerSendResult(Snapshot(updated), receipt);
        }
        finally { gate.Release(); }
    }

    public async Task<ManagerReconcileResult?> ReconcileAsync(
        string id, CancellationToken cancellation)
    {
        if (_registry.Get(id) is null) return null;
        var gate = _sendGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var current = Get(id)!;
            if (current.ConversationId is not null &&
                _runtime.FinishAcceptedManagerCreation(id, current.ConversationId))
                return new(Get(id)!, new("guard_cleared",
                    "durable_creation_binding", current.ConversationId));
            if (current.LifecycleState != "uncertain")
                return new(current, new("not_applicable", "manager_not_uncertain",
                    current.ConversationId));
            if (current.ConversationId is null)
            {
                var creation = await _runtime.ReconcileCreationAsync(id, cancellation);
                if (creation.Outcome != "restored" || creation.ConversationId is null)
                    return new(Get(id)!, creation);
                var bound = _registry.Update(id, record => record with {
                    ConversationId = creation.ConversationId,
                    CheckpointPointer = ManagerRegistry.CheckpointFor(creation.ConversationId),
                    LifecycleState = "idle"
                });
                _runtime.FinishReconciledManagerCreation(id, creation.ConversationId);
                return new(Snapshot(bound), creation);
            }
            var resolved = _runtime.HasVerifiedManagerTurn(current.ConversationId,
                _registry.Get(id)?.PendingOperationId)
                    ? new ReconciliationResult("restored", "durable_verified_turn",
                        current.ConversationId)
                    : await _runtime.ReconcileAsync(current.ConversationId, cancellation);
            if (resolved.Outcome == "restored" &&
                resolved.ConversationId == current.ConversationId)
            {
                var updated = _registry.Update(id, record =>
                    record.LifecycleState == "uncertain" &&
                    record.ConversationId == resolved.ConversationId
                        ? record with { LifecycleState = "idle" } : record);
                ReleaseVerifiedInboxBlock(id, resolved.ConversationId);
                return new(Snapshot(updated), resolved);
            }
            return new(Get(id)!, resolved);
        }
        finally { gate.Release(); }
    }

    public async Task<ManagerHistoryResult?> ReadHistoryAsync(
        string id, CancellationToken cancellation)
    {
        var current = Get(id);
        if (current is null) return null;
        if (current.ConversationId is null)
            return new ManagerHistoryResult(null, null);
        var read = await _runtime.ReadAsync(current.ConversationId, cancellation);
        return new ManagerHistoryResult(current.ConversationId, read.History);
    }

    private bool HasConfirmedBinding(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) ||
            conversationId.Length > OutboundMessagePreparation.MaxConversationIdLength ||
            conversationId.Any(char.IsControl))
            return false;
        try
        {
            // A provider receipt alone is insufficient. The normal runtime
            // must also have durably recorded the exact remote identity.
            return _runtime.Get(conversationId)?.Id == conversationId;
        }
        catch (Exception) { return false; }
    }

    private ManagerSnapshot Snapshot(ManagerRecord record)
    {
        // Journal checkpoints, not cached remote reads, determine local turn
        // state. Do not clear a registry uncertainty from a stale checkpoint.
        if (record.ConversationId is not null &&
            record.LifecycleState is not ("creating" or "sending" or "uncertain"))
        {
            var journalState = _runtime.Get(record.ConversationId)?.Status ?? "uncertain";
            if (journalState != record.LifecycleState)
                record = _registry.RefreshLifecycle(
                    record.Id, record.LifecycleState, journalState);
        }

        return new ManagerSnapshot(record.Id, record.ConversationId,
            record.LifecycleState,
            record.ConversationId is null ? "not_bound" : "unverified",
            record.CheckpointPointer, record.OutstandingJobReferences,
            record.CreatedAt, record.UpdatedAt);
    }
}
