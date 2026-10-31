using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neko.Headless;

// Only provider-neutral routing metadata belongs in this registry. Conversation
// transcripts and delivery checkpoints remain owned by ChatJournal.
internal sealed record ManagerRecord(
    string Id,
    string? ConversationId,
    string LifecycleState,
    string? CheckpointPointer,
    IReadOnlyList<string> OutstandingJobReferences,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed class ManagerRegistry
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Action<ManagerRecord>? _beforePersist;

    public ManagerRegistry(string stateDirectory, TimeProvider? clock = null,
        Action<ManagerRecord>? beforePersist = null)
    {
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "managers");
        _clock = clock ?? TimeProvider.System;
        _beforePersist = beforePersist;
        Directory.CreateDirectory(_directory);
    }

    public static bool IsValidId(string? id) => id is { Length: >= 9 and <= 72 } &&
        Regex.IsMatch(id, @"\Amanager/[a-z0-9][a-z0-9_-]{0,63}\z",
            RegexOptions.CultureInvariant);

    public static string CheckpointFor(string conversationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId))) + ".json";

    public ManagerRecord? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate) return ReadIfPresent(FileName(id));
    }

    public IReadOnlyList<ManagerRecord> List()
    {
        lock (_gate)
            return Directory.GetFiles(_directory, "*.json")
                .Select(Read)
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    // The durable reservation must precede any remote creation attempt.
    public bool TryReserve(string id, out ManagerRecord record)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid manager ID.", nameof(id));
        lock (_gate)
        {
            var existing = ReadIfPresent(FileName(id));
            if (existing is not null)
            {
                // A definitively rejected attempt never created a remote
                // conversation. Only another explicit create may rearm it.
                if (existing.LifecycleState == "rejected" &&
                    existing.ConversationId is null)
                {
                    record = existing with {
                        LifecycleState = "creating", UpdatedAt = _clock.GetUtcNow()
                    };
                    Write(record);
                    return true;
                }
                record = existing;
                return false;
            }

            var now = _clock.GetUtcNow();
            record = new ManagerRecord(id, null, "creating", null,
                Array.Empty<string>(), now, now);
            Write(record);
            return true;
        }
    }

    public ManagerRecord Update(string id, Func<ManagerRecord, ManagerRecord> change)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid manager ID.", nameof(id));
        lock (_gate)
        {
            var current = Read(FileName(id))
                ?? throw new InvalidOperationException("Manager record is unavailable.");
            var updated = change(current);
            if (updated.Id != id || updated.CreatedAt != current.CreatedAt ||
                (current.ConversationId is not null &&
                 updated.ConversationId != current.ConversationId))
                throw new InvalidOperationException("Manager identity cannot be replaced.");
            if (updated == current) return current;
            updated = updated with { UpdatedAt = _clock.GetUtcNow() };
            Write(updated);
            return updated;
        }
    }

    // Conditional synchronization prevents a concurrent metadata read from
    // overwriting a freshly persisted send/creation safety guard.
    public ManagerRecord RefreshLifecycle(string id, string expected, string next) =>
        Update(id, current => current.LifecycleState == expected &&
            current.ConversationId is not null &&
            current.LifecycleState is not ("creating" or "sending" or "uncertain")
                ? current with { LifecycleState = next } : current);

    private ManagerRecord Read(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("Manager record is missing.", file);
        ManagerRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ManagerRecord>(File.ReadAllText(file));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Manager registry checkpoint is unreadable.", error);
        }

        if (record is null || !IsValidId(record.Id) ||
            !StringComparer.Ordinal.Equals(file, FileName(record.Id)) ||
            record.OutstandingJobReferences is null ||
            record.LifecycleState is not ("creating" or "rejected" or "sending" or
                "generating" or "idle" or "stopping" or "stop_uncertain" or "uncertain") ||
            record.CreatedAt == default || record.UpdatedAt == default ||
            (record.ConversationId is not null &&
                (string.IsNullOrWhiteSpace(record.ConversationId) ||
                 record.ConversationId.Length > 256)) ||
            (record.ConversationId is null
                ? record.CheckpointPointer is not null
                : record.CheckpointPointer != CheckpointFor(record.ConversationId)))
            throw new InvalidDataException("Manager registry checkpoint is invalid.");
        return record;
    }

    private ManagerRecord? ReadIfPresent(string file) =>
        File.Exists(file) ? Read(file) : null;

    private void Write(ManagerRecord record)
    {
        _beforePersist?.Invoke(record);
        var file = FileName(record.Id);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false),
                    leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(record));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string FileName(string id) =>
        Path.Combine(_directory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
}
