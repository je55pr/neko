using System.Collections.Concurrent;
using Neko;

namespace Neko.Headless;

/// <summary>
/// Durable manager submission queue. A locally queued message is not a remote
/// receipt. The worker only dispatches never-attempted queued entries; remote
/// uncertainty is exclusively resolved through ManagerService reconciliation.
/// </summary>
internal sealed class ManagerInboxService : IDisposable
{
    private readonly ManagerInboxJournal _journal;
    private readonly ManagerService _managers;
    private readonly ChatRuntime _runtime;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    public ManagerInboxService(ManagerInboxJournal journal,
        ManagerService managers, ChatRuntime runtime, TimeProvider? clock = null)
    {
        _journal = journal;
        _managers = managers;
        _runtime = runtime;
        _clock = clock ?? TimeProvider.System;
        _journal.MarkInterruptedDispatches();
        foreach (var manager in _managers.List())
        {
            var unresolved = _journal.List(manager.Id).FirstOrDefault(message =>
                message.State is InboxStates.Uncertain or InboxStates.Dispatching);
            if (unresolved is not null)
                _managers.BlockForInboxUncertainty(manager.Id, unresolved.Id);
        }
    }

    public static bool ValidKey(string? key) =>
        key is null || key.Length is >= 1 and <= 128 &&
        key.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_' or '.' or ':');

    public (InboxEnqueueResult? Result, string? Error) Enqueue(
        string managerId, string text, string? key = null)
    {
        if (!ManagerRegistry.IsValidId(managerId) || _managers.Get(managerId) is null)
            return (null, "manager_not_found");
        if (!OutboundMessagePreparation.ForNew(text).IsReady || !ValidKey(key))
            return (null, "invalid_submission");
        var (message, status) = _journal.Enqueue(managerId, text, key);
        if (status == InboxEnqueueStatus.Conflict)
            return (null, "idempotency_conflict");
        return (new(InboxMessageView.From(message),
            status == InboxEnqueueStatus.Duplicate), null);
    }

    public IReadOnlyList<InboxMessageView>? List(string managerId) =>
        _managers.Get(managerId) is null ? null :
            _journal.List(managerId).Select(InboxMessageView.From).ToArray();

    public InboxMessageView? Get(string managerId, string messageId) =>
        _managers.Get(managerId) is null ? null :
            _journal.Get(managerId, messageId) is { } message
                ? InboxMessageView.From(message) : null;

    public InboxMessageView? Cancel(string managerId, string messageId) =>
        _journal.Transition(managerId, messageId, InboxStates.Queued,
            message => message with { State = InboxStates.Cancelled,
                Reason = "cancelled_before_dispatch", CompletedAt = _clock.GetUtcNow() })
            is { } cancelled ? InboxMessageView.From(cancelled) : null;

    // Explicitly rearm only a proven retryable rejection. Requeue never
    // follows an accepted, uncertain or interrupted-dispatch record.
    public InboxMessageView? Retry(string managerId, string messageId) =>
        _journal.Transition(managerId, messageId, InboxStates.Rejected,
            message => message.Retryable == true
                ? message with { State = InboxStates.Queued, Reason = "explicit_retry",
                    Retryable = null, DispatchedAt = null, AcceptedAt = null,
                    RemoteMessageId = null, ConversationId = null }
                : throw new InvalidOperationException("Message is not retryable."))
            is { } retried ? InboxMessageView.From(retried) : null;

    public async Task<bool> ProcessAsync(string managerId, CancellationToken cancellation)
    {
        var gate = _gates.GetOrAdd(managerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            var next = _journal.List(managerId).FirstOrDefault(m =>
                m.State is not (InboxStates.Completed or InboxStates.Cancelled));
            if (next is null) return false;

            if (next.State is InboxStates.Accepted or InboxStates.Generating)
            {
                var manager = _managers.Get(managerId);
                if (manager is null || manager.ConversationId is null ||
                    manager.ConversationId != next.ConversationId)
                    return false;
                var checkpoint = _runtime.Get(manager.ConversationId);
                if (checkpoint?.Status == "idle" &&
                    checkpoint.PendingTurn is null &&
                    checkpoint.LastReconciliation is { Outcome: "restored" } decision &&
                    decision.OperationId == next.Id)
                {
                    _journal.Transition(managerId, next.Id, next.State, message =>
                        message with { State = InboxStates.Completed,
                            CompletedAt = _clock.GetUtcNow(),
                            Reason = decision.Reason });
                    return true;
                }
                if (checkpoint?.Status == "uncertain" || manager.LifecycleState == "uncertain")
                {
                    _journal.Transition(managerId, next.Id, next.State, message =>
                        message with { State = InboxStates.Uncertain,
                            Reason = "remote_completion_unverified" });
                    return false;
                }
                if (next.State == InboxStates.Accepted && checkpoint?.Status == "generating")
                    _journal.Transition(managerId, next.Id, InboxStates.Accepted,
                        message => message with { State = InboxStates.Generating });
                return false;
            }

            if (next.State != InboxStates.Queued ||
                next.NotBefore is { } notBefore && notBefore > _clock.GetUtcNow())
                return false;

            var current = _managers.Get(managerId);
            if (current?.ConversationId is null || current.LifecycleState != "idle")
                return false;

            // Dispatching must be durable before the first remote preflight.
            // If any step after this fails, never silently requeue it.
            var reserved = _journal.Transition(managerId, next.Id, InboxStates.Queued,
                message => message with { State = InboxStates.Dispatching,
                    DispatchedAt = _clock.GetUtcNow(), NotBefore = null,
                    Reason = "dispatch_reserved" });
            if (reserved is null) return false;

            ManagerSendResult? outcome;
            try
            {
                // The durable inbox ID is also the #19 turn correlation ID.
                outcome = await _managers.SendAsync(managerId, next.Text,
                    CancellationToken.None, next.Id);
            }
            catch (Exception)
            {
                TryMarkUncertain(managerId, next.Id, "dispatch_outcome_unknown");
                return false;
            }
            if (outcome is null)
            {
                TryMarkUncertain(managerId, next.Id, "manager_unavailable");
                return false;
            }
            var receipt = outcome.Receipt;
            if (receipt.Disposition == SendDisposition.Uncertain)
            {
                TryMarkUncertain(managerId, next.Id, receipt.Reason);
                return false;
            }
            if (receipt.Disposition == SendDisposition.Accepted)
            {
                try
                {
                    _journal.Transition(managerId, next.Id, InboxStates.Dispatching,
                        message => message with {
                            State = InboxStates.Accepted,
                            AcceptedAt = _clock.GetUtcNow(),
                            ConversationId = current.ConversationId,
                            RemoteMessageId = receipt.MessageId,
                            Reason = receipt.Reason
                        });
                }
                catch
                {
                    TryMarkUncertain(managerId, next.Id, "acceptance_checkpoint_failed");
                    throw;
                }
                return true;
            }
            // An unavailable history preflight and a temporarily busy manager
            // prove no remote submission was made. Keep the original queue
            // entry, with a conservative cool-off before probing again.
            if (receipt.Reason is "provider_history_unavailable" or "manager_not_idle" or
                "conversation_not_idle")
            {
                var seconds = Math.Max(15, receipt.RetryAfterSeconds ?? 15);
                try
                {
                    _journal.Transition(managerId, next.Id, InboxStates.Dispatching,
                        message => message with { State = InboxStates.Queued,
                            DispatchedAt = null,
                            NotBefore = _clock.GetUtcNow().AddSeconds(seconds),
                            Reason = receipt.Reason });
                }
                catch
                {
                    TryMarkUncertain(managerId, next.Id, "preflight_checkpoint_failed");
                    throw;
                }
                return false;
            }

            try
            {
                _journal.Transition(managerId, next.Id, InboxStates.Dispatching,
                    message => message with { State = InboxStates.Rejected,
                        Reason = receipt.Reason, Retryable = receipt.Retryable,
                        NotBefore = receipt.RetryAfterSeconds is int seconds
                            ? _clock.GetUtcNow().AddSeconds(Math.Max(0, seconds)) : null });
            }
            catch
            {
                TryMarkUncertain(managerId, next.Id, "rejection_checkpoint_failed");
                throw;
            }
            return true;
        }
        finally { gate.Release(); }
    }

    // A reconciler may restore the exact turn, but never force-completes an
    // inbox entry merely because the manager became idle. ProcessAsync checks
    // the runtime's persisted exact operation ID on its next pass.
    public async Task ProcessAllAsync(CancellationToken cancellation)
    {
        foreach (var manager in _managers.List())
        {
            cancellation.ThrowIfCancellationRequested();
            await ProcessAsync(manager.Id, cancellation);
        }
    }

    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("Inbox worker already started.");
        _worker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (!_shutdown.IsCancellationRequested)
            {
                try { await ProcessAllAsync(_shutdown.Token); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Never bypass a failed journal checkpoint or dispatch gate.
                    // The next scan can resume safely from durable state.
                }
                try { if (!await timer.WaitForNextTickAsync(_shutdown.Token)) break; }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private void TryMarkUncertain(string managerId, string messageId, string reason)
    {
        try
        {
            _journal.Transition(managerId, messageId, InboxStates.Dispatching,
                message => message with { State = InboxStates.Uncertain, Reason = reason });
        }
        catch (Exception) { /* Durable dispatching remains fail-closed on restart. */ }
        // Hold the in-process manager gate even if disk persistence failed.
        _managers.BlockForInboxUncertainty(managerId, messageId);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* Worker cancellation. */ }
        _shutdown.Dispose();
    }
}
