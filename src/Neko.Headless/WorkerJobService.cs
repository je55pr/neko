using System.Security.Cryptography;
using System.Text;
using Neko;

namespace Neko.Headless;

internal sealed record WorkerJobRegisterRequest(
    string? WorkerConversationId, string? OperationId = null,
    string? Repository = null, int? PrNumber = null,
    DateTimeOffset? Deadline = null);
internal sealed record WorkerJobRegisterResult(WorkerJob Job, bool Duplicate);
internal sealed record WorkerJobInterruptRequest(string? Reason);

/// <summary>
/// Observes accepted worker turns and authorized PR evidence. Never starts,
/// retries, stops, or sends to a worker. Only the durable manager inbox may
/// resume the manager after its current turn finishes.
/// </summary>
internal sealed class WorkerJobService : IDisposable
{
    private readonly WorkerJobJournal _jobs;
    private readonly ManagerRegistry _registry;
    private readonly ManagerService _managers;
    private readonly ManagerInboxService _inbox;
    private readonly ChatRuntime _runtime;
    private readonly PrWorkflowService _workflows;
    private readonly PrReviewPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _scan = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private long _scanIteration;

    public WorkerJobService(WorkerJobJournal jobs, ManagerRegistry registry,
        ManagerService managers, ManagerInboxService inbox, ChatRuntime runtime,
        PrWorkflowService workflows, PrReviewPolicy policy, TimeProvider? clock = null)
    {
        _jobs = jobs;
        _registry = registry;
        _managers = managers;
        _inbox = inbox;
        _runtime = runtime;
        _workflows = workflows;
        _policy = policy;
        _clock = clock ?? TimeProvider.System;
        // Repair the non-authoritative manager index following any interruption
        // between the job journal and registry checkpoints.
        foreach (var id in _jobs.ListAll().Select(x => x.ManagerId).Distinct())
            SynchronizeIndex(id);
    }

    public IReadOnlyList<WorkerJob>? List(string managerId) =>
        _managers.Get(managerId) is null ? null : _jobs.List(managerId);

    public WorkerJob? Get(string managerId, string jobId) =>
        _managers.Get(managerId) is null ? null : _jobs.Get(managerId, jobId);

    public (WorkerJobRegisterResult? Result, string? Error) Register(
        string managerId, WorkerJobRegisterRequest request)
    {
        var manager = _managers.Get(managerId);
        if (manager?.ConversationId is null) return (null, "manager_not_bound");
        var workerId = request.WorkerConversationId;
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 256 ||
            workerId.Any(char.IsControl) || workerId == manager.ConversationId ||
            _managers.List().Any(m => m.ConversationId == workerId) ||
            request.OperationId is { Length: > 128 } ||
            request.OperationId?.Any(char.IsControl) == true ||
            request.Deadline is { } deadline &&
                (deadline <= _clock.GetUtcNow() ||
                 deadline > _clock.GetUtcNow().AddDays(30)) ||
            (request.Repository is null) != (request.PrNumber is null) ||
            request.Repository is not null &&
                (!PrWorkflowStore.ValidRepository(request.Repository) ||
                 request.PrNumber is null or < 1))
            return (null, "invalid_job_binding");

        var checkpoint = _runtime.Get(workerId);
        if (checkpoint is null || checkpoint.DispatchCount < 1 ||
            string.IsNullOrWhiteSpace(checkpoint.ProgressTurnId) ||
            checkpoint.Status is not ("generating" or "idle" or "uncertain") ||
            (checkpoint.Status == "idle"
                ? checkpoint.PendingTurn is not null ||
                  checkpoint.LastReconciliation is not {
                      Outcome: "restored" } verified ||
                  verified.TurnId != checkpoint.ProgressTurnId ||
                  verified.DispatchCount != checkpoint.DispatchCount
                : checkpoint.PendingTurn?.AcceptedSubmission != true))
            return (null, "worker_acceptance_unverified");
        var actualOperation = checkpoint.Status == "idle"
            ? checkpoint.LastReconciliation?.OperationId
            : checkpoint.PendingTurn?.OperationId;
        if (request.OperationId is not null && request.OperationId != actualOperation)
            return (null, "worker_operation_mismatch");
        // Always bind the actual operation, even if the caller omitted it.
        var operationId = actualOperation;

        string? repo = null;
        string? head = null;
        if (request.Repository is not null)
        {
            var workflow = _workflows.Get(request.Repository, request.PrNumber!.Value);
            if (workflow is null ||
                workflow.ManagerConversationId != manager.ConversationId ||
                workerId != workflow.CreatorConversationId &&
                workerId != workflow.DeveloperConversationId &&
                workerId != workflow.Reviewer?.ConversationId)
                return (null, "pr_worker_binding_unverified");
            repo = workflow.Repository;
            head = workflow.HeadSha;
        }

        var now = _clock.GetUtcNow();
        var candidate = new WorkerJob(Guid.NewGuid().ToString("N"),
            managerId, workerId, checkpoint.ProgressTurnId!,
            checkpoint.DispatchCount, operationId, repo, request.PrNumber,
            head, request.Deadline, "running", now, now, now, 0,
            null, [], NextExternalPollAt: repo is null ? null : now);
        try
        {
            var (record, duplicate) = _jobs.Register(candidate);
            SynchronizeIndex(managerId);
            return (new WorkerJobRegisterResult(record, duplicate), null);
        }
        catch (InvalidOperationException)
        { return (null, "worker_turn_already_bound"); }
    }

    public WorkerJob? Interrupt(string managerId, string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 ||
            reason.Any(char.IsControl)) return null;
        var current = Get(managerId, id);
        if (current is null) return null;
        var key = "interrupt." + Hash(reason);
        return _jobs.Update(managerId, id,
            job => AddEvent(job, "human_interruption", reason, key));
    }

    internal static IReadOnlyList<WorkerJob> SelectJobs(
        IReadOnlyList<WorkerJob> all, DateTimeOffset now, long scan)
    {
        var due = all.Where(j => j.NextPollAt is { } at && at <= now)
            .OrderBy(j => j.ManagerId, StringComparer.Ordinal)
            .ThenBy(j => j.Id, StringComparer.Ordinal).ToArray();
        var notifications = all.Where(j =>
                j.Events.Any(e => e.InboxMessageId is null))
            .OrderBy(j => j.ManagerId, StringComparer.Ordinal)
            .ThenBy(j => j.Id, StringComparer.Ordinal).ToArray();
        var both = due.Length > 0 && notifications.Length > 0;
        return Rotate(due, both ? 32 : 64, scan)
            .Concat(Rotate(notifications, both ? 32 : 64, scan))
            .DistinctBy(j => (j.ManagerId, j.Id))
            .Take(64).ToArray();
    }

    private static IEnumerable<WorkerJob> Rotate(WorkerJob[] candidates,
        int limit, long scan)
    {
        if (candidates.Length == 0) return [];
        var offset = (int)((ulong)scan % (uint)candidates.Length);
        return candidates.Skip(offset).Concat(candidates.Take(offset)).Take(limit);
    }

    public async Task ProcessAllAsync(CancellationToken cancellation)
    {
        await _scan.WaitAsync(cancellation);
        try
        {
            var now = _clock.GetUtcNow();
            // Reserve capacity for both due worker observations and pending
            // inbox notifications. A permanently failing inbox must not starve
            // unrelated workers, and a failing worker must not starve delivery.
            // Rotate each bounded scan so one failing prefix cannot monopolize
            // the same slots forever.
            var jobs = SelectJobs(_jobs.ListAll(), now,
                Interlocked.Increment(ref _scanIteration));
            var externalReads = 0;
            foreach (var candidate in jobs)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var job = _jobs.Get(candidate.ManagerId, candidate.Id)!;
                    if (job.NextPollAt is { } due && due <= _clock.GetUtcNow())
                    {
                        var fetchExternal = job.Repository is not null &&
                            !job.WatchClosed &&
                            job.NextExternalPollAt is { } ext && ext <= _clock.GetUtcNow() &&
                            externalReads < 2;
                        if (fetchExternal) externalReads++;
                        await ObserveAsync(job, fetchExternal, cancellation);
                    }
                    FlushEvents(candidate.ManagerId, candidate.Id);
                    SynchronizeIndex(candidate.ManagerId);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { throw; }
                catch (Exception)
                {
                    // One failing job must not suppress other watchers. Durable
                    // intent remains for the next bounded pass.
                }
            }
        }
        finally { _scan.Release(); }
    }

    private async Task ObserveAsync(WorkerJob current, bool fetchExternal,
        CancellationToken cancellation)
    {
        var now = _clock.GetUtcNow();
        var checkpoint = _runtime.Get(current.WorkerConversationId);
        var next = _jobs.Update(current.ManagerId, current.Id, job =>
        {
            if (job.State == "running")
            {
                if (checkpoint is null || checkpoint.DispatchCount != job.DispatchCount ||
                    checkpoint.ProgressTurnId != job.TurnId)
                {
                    job = AddEvent(job, "worker_binding_lost",
                        "Worker turn changed or its checkpoint is unavailable.",
                        "binding");
                    job = job with { State = "attention_required" };
                }
                else if (checkpoint.Status == "idle" &&
                         checkpoint.PendingTurn is null &&
                         checkpoint.LastReconciliation is { Outcome: "restored" } proof &&
                         proof.OperationId == job.OperationId &&
                         proof.TurnId == job.TurnId &&
                         proof.DispatchCount == job.DispatchCount)
                {
                    job = job with {
                        State = "completed",
                        Terminal = new WorkerTerminalEvidence(now, job.TurnId,
                            job.DispatchCount, proof.OperationId,
                            proof.AssistantMessageId, proof.Reason)
                    };
                    job = AddEvent(job, "worker_completed",
                        "Verified worker terminal checkpoint.", "completed");
                }
                else if (checkpoint.Status is "uncertain" or "stop_uncertain")
                    job = AddEvent(job, "worker_uncertain",
                        "Worker outcome requires explicit reconciliation.", "uncertain");
            }
            if (job.Deadline is { } deadline && deadline <= now &&
                job.State != "completed")
                job = AddEvent(job, "worker_deadline",
                    "Worker did not reach a verified terminal state by its deadline.",
                    "deadline");
            var nextLocal = job.State == "running" ? now.AddSeconds(10) : (DateTimeOffset?)null;
            if (nextLocal is null && job.Repository is not null && !job.WatchClosed)
                nextLocal = now.AddSeconds(30);
            return job with { NextPollAt = nextLocal };
        })!;

        if (!fetchExternal || next.Repository is null) return;
        try
        {
            var report = await _workflows.InspectAsync(next.Repository,
                next.PrNumber!.Value, cancellation);
            var source = report.Workflow.LastSourceEvidence;
            // Ignore stale inspection outcomes and mismatched revisions;
            // never present an old head's check as the current job's failure.
            _jobs.Update(next.ManagerId, next.Id, job =>
            {
                if (source is not null && !report.Gate.Blockers.Contains(
                        "inspection_obsoleted_by_workflow_change") &&
                    StringComparer.OrdinalIgnoreCase.Equals(source.Repository, job.Repository) &&
                    source.Number == job.PrNumber && source.HeadSha != job.HeadSha)
                {
                    job = AddEvent(job, "pr_head_changed",
                        "The source PR head changed; this job's previous revision is stale.",
                        "head." + Hash(source.HeadSha));
                    job = job with { WatchClosed = true };
                }
                else if (source is not null && !report.Gate.Blockers.Contains(
                        "inspection_obsoleted_by_workflow_change") &&
                    StringComparer.OrdinalIgnoreCase.Equals(source.Repository, job.Repository) &&
                    source.Number == job.PrNumber && source.HeadSha == job.HeadSha)
                {
                    foreach (var check in source.Checks.Where(c =>
                        c.HeadSha == job.HeadSha && c.Status == "completed" &&
                        c.Conclusion is not (null or "success") &&
                        TrustedCheck(c)))
                    {
                        var identity = check.Name + ":" + check.ProducerId + ":" +
                            check.WorkflowId + ":" + check.RunId + ":" + check.StartedAt;
                        job = AddEvent(job, "check_failed",
                            $"Check {check.Name} concluded {check.Conclusion} on {job.HeadSha}.",
                            "check." + Hash(identity), check.Name, check.RunId);
                    }

                    if (report.Workflow.Reviewer is { ReviewerVerdict: "blockers" } reviewer &&
                        reviewer.ConversationId == job.WorkerConversationId)
                        job = AddEvent(job, "review_findings",
                            "Independent worker reported actionable review findings.",
                            "reviewer." + Hash(reviewer.ConversationId + reviewer.CompletedAt));
                    foreach (var review in source.Reviews.Where(r =>
                        r.Substantive && r.Verdict == "changes_requested" &&
                        r.HeadSha == job.HeadSha))
                        job = AddEvent(job, "review_findings",
                            "Source review requested changes.",
                            "review." + Hash(review.Url), reviewUrl: review.Url);
                    // Marker-free empty reviews are a valid opt-in policy.
                    // Continue notifying the manager whenever the trusted bot
                    // instead publishes substantive findings on that head.
                    if (!string.IsNullOrWhiteSpace(_policy.AutomatedReviewer) &&
                        (!string.IsNullOrWhiteSpace(_policy.AutomatedNoFindingsMarker) ||
                         _policy.AllowVerifiedEmptyAutomatedReview))
                        foreach (var review in source.Reviews.Where(r =>
                            r.Substantive && r.HeadSha == job.HeadSha &&
                            StringComparer.OrdinalIgnoreCase.Equals(
                                r.Author, _policy.AutomatedReviewer) &&
                            !r.AutomatedNoFindings))
                            job = AddEvent(job, "review_findings",
                                "Automated review contains findings requiring triage.",
                                "review." + Hash(review.Url), reviewUrl: review.Url);
                    if (source.State != "open" || source.Merged)
                        job = job with { WatchClosed = true };
                }
                return job with {
                    NextExternalPollAt = now.AddMinutes(2), ExternalFailures = 0,
                    NextPollAt = job.WatchClosed && job.State != "running"
                        ? null : job.NextPollAt
                };
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw; }
        catch (Exception)
        {
            _jobs.Update(next.ManagerId, next.Id, job =>
            {
                var failures = Math.Min(10, job.ExternalFailures + 1);
                var seconds = Math.Min(1800, 60 * (1 << Math.Min(failures, 5)));
                return job with { ExternalFailures = failures,
                    NextExternalPollAt = now.AddSeconds(seconds),
                    NextPollAt = now.AddSeconds(Math.Min(seconds, 30)) };
            });
        }
    }

    private bool TrustedCheck(PrCheck check)
    {
        string? producer;
        string? workflow = null;
        if (check.Name == _policy.AutomatedCheck)
        {
            producer = _policy.AutomatedCheckProducer;
            workflow = _policy.AutomatedCheckWorkflow;
        }
        else
        {
            if (_policy.RequiredChecks?.Contains(check.Name) != true ||
                _policy.TrustedCheckProducers?.TryGetValue(check.Name, out producer) != true)
                return false;
            _policy.TrustedCheckWorkflows?.TryGetValue(check.Name, out workflow);
        }
        return !string.IsNullOrWhiteSpace(producer) && check.ProducerId == producer &&
            (string.IsNullOrWhiteSpace(workflow)
                ? !check.RequiresWorkflowIdentity
                : check.WorkflowId == workflow);
    }

    private void FlushEvents(string managerId, string id)
    {
        var job = _jobs.Get(managerId, id);
        if (job is null) return;
        foreach (var evt in job.Events.Where(e => e.InboxMessageId is null))
        {
            var text = $"Worker job {job.Id} ({job.WorkerConversationId}) " +
                $"{evt.Kind}: {evt.Detail}" +
                (job.Repository is null ? "" : $" Source: {job.Repository}#{job.PrNumber}.") +
                (evt.CheckName is null ? "" : $" Check: {evt.CheckName}; run: {evt.CheckRunId}.") +
                (evt.ReviewUrl is null ? "" : $" Review: {evt.ReviewUrl}.");
            var (receipt, error) = _inbox.Enqueue(managerId, text, evt.Key);
            if (error is not null || receipt is null) return;
            _jobs.Update(managerId, id, current => current with {
                Events = current.Events.Select(e =>
                    e.Key == evt.Key ? e with { InboxMessageId = receipt.Message.Id } : e)
                    .ToArray()
            });
        }
    }

    private void SynchronizeIndex(string managerId)
    {
        var outstanding = _jobs.List(managerId).Where(x => x.State != "completed")
            .Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (_registry.Get(managerId) is null) return;
        _registry.Update(managerId, record =>
            record.OutstandingJobReferences.OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(outstanding, StringComparer.Ordinal)
                ? record : record with { OutstandingJobReferences = outstanding });
    }

    private WorkerJob AddEvent(WorkerJob job, string kind, string detail, string suffix,
        string? checkName = null, long? checkRunId = null, string? reviewUrl = null)
    {
        var key = "job." + job.Id + "." + suffix;
        if (job.Events.Any(e => e.Key == key)) return job;
        if (job.Events.Count >= 256 || (job.Events.Count >= 200 &&
            kind is ("check_failed" or "review_findings"))) return job;
        if (reviewUrl is { Length: > 1024 } || reviewUrl?.Any(char.IsControl) == true)
            reviewUrl = null;
        if (checkName is { Length: > 200 }) checkName = checkName[..200];
        return job with { Events = job.Events.Append(new WorkerJobEvent(
            key, kind, detail, _clock.GetUtcNow(), CheckName: checkName,
            CheckRunId: checkRunId, ReviewUrl: reviewUrl)).ToArray() };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("Job watcher already started.");
        _worker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!_shutdown.IsCancellationRequested)
            {
                try { await ProcessAllAsync(_shutdown.Token); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                { break; }
                catch (Exception) { /* Durable intent survives failures and restart. */ }
                try { if (!await timer.WaitForNextTickAsync(_shutdown.Token)) break; }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _scan.Dispose();
        _shutdown.Dispose();
    }
}
