using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal sealed record WorkerTerminalEvidence(
    DateTimeOffset ObservedAt, string TurnId, int DispatchCount,
    string? OperationId, string? AssistantMessageId, string Reason);

internal sealed record WorkerJobEvent(
    string Key, string Kind, string Detail, DateTimeOffset ObservedAt,
    string? InboxMessageId = null, string? CheckName = null,
    long? CheckRunId = null, string? ReviewUrl = null);

/// <summary>
/// An observation-only binding to an already accepted worker turn. Jobs NEVER
/// own a remote create/send reservation, and cannot be used to retry a worker.
/// </summary>
internal sealed record WorkerJob(
    string Id, string ManagerId, string WorkerConversationId, string TurnId,
    int DispatchCount, string? OperationId, string? Repository, int? PrNumber,
    string? HeadSha, DateTimeOffset? Deadline, string State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? NextPollAt, int PollFailures,
    WorkerTerminalEvidence? Terminal, IReadOnlyList<WorkerJobEvent> Events,
    bool WatchClosed = false, DateTimeOffset? NextExternalPollAt = null,
    int ExternalFailures = 0);

/// <summary>
/// Atomic manager-scoped snapshots under the host's exclusive state lease.
/// An event is committed before inbox enqueue; its stable key makes a crash
/// between the two checkpoints harmless to notification cardinality.
/// </summary>
internal sealed class WorkerJobJournal
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Action<WorkerJob>? _beforePersist;

    public WorkerJobJournal(string stateDirectory, TimeProvider? clock = null,
        Action<WorkerJob>? beforePersist = null)
    {
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "worker-jobs");
        _clock = clock ?? TimeProvider.System;
        _beforePersist = beforePersist;
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<WorkerJob> List(string managerId)
    {
        lock (_gate) return Read(managerId).ToArray();
    }

    public IReadOnlyList<WorkerJob> ListAll()
    {
        lock (_gate) return Directory.GetFiles(_directory, "*.json")
            .SelectMany(ReadFile).OrderBy(x => x.CreatedAt).ToArray();
    }

    public WorkerJob? Get(string managerId, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        lock (_gate) return Read(managerId).FirstOrDefault(x => x.Id == id);
    }

    // Caller supplies verified runtime evidence; the journal alone does not
    // mistake a remote operation request for an accepted worker turn.
    public (WorkerJob Job, bool Duplicate) Register(WorkerJob candidate)
    {
        lock (_gate)
        {
            var all = Directory.GetFiles(_directory, "*.json").SelectMany(ReadFile).ToArray();
            var existing = all.FirstOrDefault(x =>
                x.WorkerConversationId == candidate.WorkerConversationId &&
                x.DispatchCount == candidate.DispatchCount);
            if (existing is not null)
            {
                if (existing.ManagerId != candidate.ManagerId ||
                    existing.TurnId != candidate.TurnId ||
                    existing.OperationId != candidate.OperationId ||
                    existing.Repository != candidate.Repository ||
                    existing.PrNumber != candidate.PrNumber ||
                    existing.HeadSha != candidate.HeadSha ||
                    existing.Deadline != candidate.Deadline)
                    throw new InvalidOperationException("Worker turn already bound to another job.");
                return (existing, true);
            }
            var messages = Read(candidate.ManagerId);
            if (messages.Count >= 10000)
                throw new InvalidOperationException("Worker job journal is full.");
            messages.Add(candidate);
            Write(candidate.ManagerId, messages, candidate);
            return (candidate, false);
        }
    }

    public WorkerJob? Update(string managerId, string id,
        Func<WorkerJob, WorkerJob> change)
    {
        lock (_gate)
        {
            var jobs = Read(managerId);
            var index = jobs.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var before = jobs[index];
            var updated = change(before);
            if (updated.Id != before.Id || updated.ManagerId != before.ManagerId ||
                updated.WorkerConversationId != before.WorkerConversationId ||
                updated.TurnId != before.TurnId ||
                updated.DispatchCount != before.DispatchCount ||
                updated.OperationId != before.OperationId ||
                updated.Repository != before.Repository ||
                updated.PrNumber != before.PrNumber ||
                updated.HeadSha != before.HeadSha ||
                updated.Deadline != before.Deadline ||
                updated.CreatedAt != before.CreatedAt ||
                (before.Terminal is not null && updated.Terminal != before.Terminal) ||
                (before.WatchClosed && !updated.WatchClosed) ||
                updated.Events.Count < before.Events.Count ||
                !updated.Events.Take(before.Events.Count).SequenceEqual(before.Events,
                    new EventIdentityComparer()))
                throw new InvalidOperationException("Worker job evidence is immutable.");
            if (updated == before) return before;
            updated = updated with { UpdatedAt = _clock.GetUtcNow() };
            jobs[index] = updated;
            Write(managerId, jobs, updated);
            return updated;
        }
    }

    private sealed class EventIdentityComparer : IEqualityComparer<WorkerJobEvent>
    {
        public bool Equals(WorkerJobEvent? x, WorkerJobEvent? y) =>
            x is not null && y is not null &&
            x.Key == y.Key && x.Kind == y.Kind &&
            x.Detail == y.Detail && x.ObservedAt == y.ObservedAt &&
            x.CheckName == y.CheckName && x.CheckRunId == y.CheckRunId &&
            x.ReviewUrl == y.ReviewUrl &&
            (y.InboxMessageId is null || x.InboxMessageId == y.InboxMessageId);
        public int GetHashCode(WorkerJobEvent obj) => obj.Key.GetHashCode();
    }

    private List<WorkerJob> Read(string managerId)
    {
        var path = PathFor(managerId);
        return File.Exists(path) ? ReadFile(path) : [];
    }

    private List<WorkerJob> ReadFile(string path)
    {
        List<WorkerJob>? jobs;
        try { jobs = JsonSerializer.Deserialize<List<WorkerJob>>(File.ReadAllText(path)); }
        catch (JsonException error)
        { throw new InvalidDataException("Worker job journal is unreadable.", error); }
        if (jobs is null || jobs.Count > 10000 ||
            jobs.Any(x => !ManagerRegistry.IsValidId(x.ManagerId) ||
                PathFor(x.ManagerId) != path ||
                !Guid.TryParseExact(x.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(x.WorkerConversationId) ||
                x.WorkerConversationId.Length > 256 ||
                string.IsNullOrWhiteSpace(x.TurnId) || x.TurnId.Length > 128 ||
                x.DispatchCount < 1 || x.OperationId is { Length: > 128 } ||
                x.Repository is not null && (!PrWorkflowStore.ValidRepository(x.Repository) ||
                    x.PrNumber is null or < 1 || !PrReviewGate.ValidSha(x.HeadSha)) ||
                x.Repository is null && (x.PrNumber is not null || x.HeadSha is not null) ||
                x.State is not ("running" or "completed" or "attention_required") ||
                x.CreatedAt == default || x.UpdatedAt == default ||
                x.PollFailures is < 0 or > 1000 ||
                x.ExternalFailures is < 0 or > 1000 ||
                x.Events is null || x.Events.Count > 256 ||
                x.Events.Any(e => string.IsNullOrWhiteSpace(e.Key) ||
                    !ManagerInboxService.ValidKey(e.Key) ||
                    e.Kind.Length is < 1 or > 64 || e.Detail.Length > 512 ||
                    e.CheckName is { Length: > 200 } || e.CheckRunId is < 0 ||
                    e.ReviewUrl is { Length: > 1024 } ||
                    e.ReviewUrl?.Any(char.IsControl) == true ||
                    e.ObservedAt == default ||
                    e.InboxMessageId is not null &&
                    !Guid.TryParseExact(e.InboxMessageId, "N", out _)) ||
                x.Events.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != x.Events.Count ||
                (x.State == "completed") != (x.Terminal is not null) ||
                x.Terminal is { } terminal &&
                    (terminal.TurnId != x.TurnId ||
                     terminal.DispatchCount != x.DispatchCount ||
                     terminal.OperationId != x.OperationId ||
                     terminal.ObservedAt == default ||
                     string.IsNullOrWhiteSpace(terminal.Reason))) ||
            jobs.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != jobs.Count ||
            jobs.GroupBy(x => (x.WorkerConversationId, x.DispatchCount)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Worker job journal is invalid.");
        return jobs;
    }

    private string PathFor(string managerId)
    {
        if (!ManagerRegistry.IsValidId(managerId))
            throw new ArgumentException("Invalid manager ID.", nameof(managerId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(managerId)));
        return Path.Combine(_directory, hash + ".json");
    }

    private void Write(string managerId, List<WorkerJob> jobs, WorkerJob changed)
    {
        _beforePersist?.Invoke(changed);
        var path = PathFor(managerId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false),
                    leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(jobs));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
