using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal static class InboxStates
{
    public const string Queued = "queued";
    public const string Dispatching = "dispatching";
    public const string Accepted = "accepted";
    public const string Generating = "generating";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string Uncertain = "uncertain";
    public const string Cancelled = "cancelled";
    public static bool IsValid(string state) => state is
        Queued or Dispatching or Accepted or Generating or Completed or
        Rejected or Uncertain or Cancelled;
}

internal sealed record InboxAttemptEvidence(
    DateTimeOffset ObservedAt, string Disposition, string? Reason,
    bool? Retryable, int? RetryAfterSeconds,
    string? RemoteMessageId, string? ConversationId);

internal sealed record InboxMessage(
    string Id, string ManagerId, long Sequence, string Text,
    string PayloadSha256, string? IdempotencyKey, string State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DispatchedAt = null, DateTimeOffset? AcceptedAt = null,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? NotBefore = null,
    string? Reason = null, bool? Retryable = null,
    string? RemoteMessageId = null, string? ConversationId = null,
    IReadOnlyList<InboxAttemptEvidence>? Attempts = null);

internal sealed record InboxMessageView(
    string Id, string ManagerId, long Sequence, string State,
    string? IdempotencyKey, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? DispatchedAt, DateTimeOffset? AcceptedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? NotBefore,
    string? Reason, bool? Retryable, string? RemoteMessageId,
    string? ConversationId, IReadOnlyList<InboxAttemptEvidence>? Attempts)
{
    public static InboxMessageView From(InboxMessage message) =>
        new(message.Id, message.ManagerId, message.Sequence, message.State,
            message.IdempotencyKey, message.CreatedAt, message.UpdatedAt,
            message.DispatchedAt, message.AcceptedAt, message.CompletedAt,
            message.NotBefore, message.Reason, message.Retryable,
            message.RemoteMessageId, message.ConversationId, message.Attempts);
}

internal sealed record InboxEnqueueResult(InboxMessageView Message, bool Duplicate);
internal enum InboxEnqueueStatus { Enqueued, Duplicate, Conflict }

/// <summary>
/// One fsynced, atomically replaced journal per manager. Holding the lock over
/// read-modify-write makes idempotency and sequence reservation indivisible.
/// The host state lease excludes concurrent service processes.
/// </summary>
internal sealed class ManagerInboxJournal
{
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Action<InboxMessage>? _beforePersist;

    public ManagerInboxJournal(string stateDirectory, TimeProvider? clock = null,
        Action<InboxMessage>? beforePersist = null)
    {
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "manager-inbox");
        _clock = clock ?? TimeProvider.System;
        _beforePersist = beforePersist;
        Directory.CreateDirectory(_directory);
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string PathFor(string managerId)
    {
        if (!ManagerRegistry.IsValidId(managerId))
            throw new ArgumentException("Invalid manager ID.", nameof(managerId));
        return Path.Combine(_directory, Hash(managerId) + ".json");
    }

    public IReadOnlyList<InboxMessage> List(string managerId)
    {
        lock (_gate) return Read(managerId).ToArray();
    }

    public InboxMessage? Get(string managerId, string messageId)
    {
        lock (_gate) return Read(managerId).FirstOrDefault(m => m.Id == messageId);
    }

    // Keep legacy direct manager sends from overwriting the exact completion
    // proof before the inbox has durably consumed it.
    public bool HasOutstanding(string managerId)
    {
        lock (_gate) return Read(managerId).Any(m => m.State is
            InboxStates.Queued or InboxStates.Dispatching or InboxStates.Accepted or
            InboxStates.Generating or InboxStates.Uncertain);
    }

    public (InboxMessage Message, InboxEnqueueStatus Status) Enqueue(
        string managerId, string text, string? key)
    {
        lock (_gate)
        {
            var messages = Read(managerId);
            var hash = Hash(text);
            if (key is not null)
            {
                var existing = messages.FirstOrDefault(m =>
                    StringComparer.Ordinal.Equals(m.IdempotencyKey, key));
                if (existing is not null)
                    return (existing, existing.PayloadSha256 == hash &&
                        StringComparer.Ordinal.Equals(existing.Text, text)
                            ? InboxEnqueueStatus.Duplicate : InboxEnqueueStatus.Conflict);
            }
            if (messages.Count >= 100000)
                throw new InvalidOperationException("Manager inbox has reached its capacity.");
            var now = _clock.GetUtcNow();
            var next = messages.Count == 0 ? 1 : checked(messages[^1].Sequence + 1);
            var created = new InboxMessage(Guid.NewGuid().ToString("N"),
                managerId, next, text, hash, key, InboxStates.Queued, now, now);
            messages.Add(created);
            Write(managerId, messages, created);
            return (created, InboxEnqueueStatus.Enqueued);
        }
    }

    public InboxMessage? Transition(string managerId, string messageId,
        string expected, Func<InboxMessage, InboxMessage> change)
    {
        lock (_gate)
        {
            var messages = Read(managerId);
            var index = messages.FindIndex(m => m.Id == messageId);
            if (index < 0 || messages[index].State != expected) return null;
            var before = messages[index];
            var updated = change(before);
            if (updated.Id != before.Id || updated.ManagerId != before.ManagerId ||
                updated.Sequence != before.Sequence || updated.Text != before.Text ||
                updated.PayloadSha256 != before.PayloadSha256 ||
                updated.IdempotencyKey != before.IdempotencyKey ||
                updated.CreatedAt != before.CreatedAt || !InboxStates.IsValid(updated.State))
                throw new InvalidOperationException("Inbox message identity cannot change.");
            updated = updated with { UpdatedAt = _clock.GetUtcNow() };
            messages[index] = updated;
            Write(managerId, messages, updated);
            return updated;
        }
    }

    // A crash at any point after the durable dispatch reservation is ambiguous.
    // The old entry is not placed back into the autonomous queue.
    public void MarkInterruptedDispatches()
    {
        lock (_gate)
        {
            foreach (var path in Directory.GetFiles(_directory, "*.json"))
            {
                var messages = ReadFile(path);
                foreach (var message in messages.Where(m => m.State == InboxStates.Dispatching).ToArray())
                {
                    var index = messages.FindIndex(m => m.Id == message.Id);
                    var changed = message with {
                        State = InboxStates.Uncertain, Reason = "dispatch_interrupted",
                        UpdatedAt = _clock.GetUtcNow()
                    };
                    messages[index] = changed;
                    Write(message.ManagerId, messages, changed);
                }
            }
        }
    }

    private List<InboxMessage> Read(string managerId)
    {
        var path = PathFor(managerId);
        if (!File.Exists(path)) return [];
        var messages = ReadFile(path);
        if (messages.Any(m => m.ManagerId != managerId))
            throw new InvalidDataException("Inbox manager binding is invalid.");
        return messages;
    }

    private List<InboxMessage> ReadFile(string path)
    {
        List<InboxMessage>? messages;
        try { messages = JsonSerializer.Deserialize<List<InboxMessage>>(File.ReadAllText(path)); }
        catch (JsonException error)
        { throw new InvalidDataException("Inbox journal is unreadable.", error); }
        if (messages is null || messages.Count > 100000 ||
            messages.Any(m => !ManagerRegistry.IsValidId(m.ManagerId) ||
                PathFor(m.ManagerId) != path ||
                !Guid.TryParseExact(m.Id, "N", out _) ||
                m.Sequence < 1 || m.CreatedAt == default || m.UpdatedAt == default ||
                !InboxStates.IsValid(m.State) ||
                m.Text is null || Hash(m.Text) != m.PayloadSha256 ||
                m.IdempotencyKey is { Length: > 128 or 0 } ||
                m.Attempts is { Count: > 1000 } ||
                m.Attempts?.Any(a => a.ObservedAt == default ||
                    a.Disposition is not ("accepted" or "rejected" or "uncertain")) == true) ||
            messages.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != messages.Count ||
            messages.Where(m => m.IdempotencyKey is not null).Select(m => m.IdempotencyKey)
                .Distinct(StringComparer.Ordinal).Count() !=
                messages.Count(m => m.IdempotencyKey is not null) ||
            messages.Where((m, i) => m.Sequence != i + 1).Any())
            throw new InvalidDataException("Inbox journal is invalid.");
        return messages;
    }

    private void Write(string managerId, List<InboxMessage> messages, InboxMessage changed)
    {
        _beforePersist?.Invoke(changed);
        var file = PathFor(managerId);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(messages));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
