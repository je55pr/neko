using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal sealed record PendingTurnEvidence(
    string PromptSha256, IReadOnlyList<string> BaselineMessageIds,
    bool AcceptedSubmission = false, string? CorrelatedUserMessageId = null,
    string? OperationId = null);

internal sealed record ReconciliationDecision(
    DateTimeOffset CheckedAt, string Outcome, string Reason,
    string PromptSha256, string? UserMessageId = null,
    string? AssistantMessageId = null, string? OperationId = null,
    string? TurnId = null, int? DispatchCount = null);

internal sealed record CreationPendingRecord(
    string? PromptSha256, string? OwnerId,
    string? CorrelatedConversationId, DateTimeOffset StartedAt,
    DateTimeOffset? LastCheckedAt = null, string? LastCheckReason = null);

internal sealed record ChatRecord(
    string Id, string Status, VisibleConversation? History = null,
    DateTimeOffset UpdatedAt = default, string? ProgressTurnId = null,
    PendingTurnEvidence? PendingTurn = null,
    ReconciliationDecision? LastReconciliation = null,
    int DispatchCount = 0);

/// <summary>Local checkpoint data. Never stored in the repository.</summary>
internal sealed class ChatJournal
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Action<ChatRecord>? _beforePersist;
    private readonly Action<CreationPendingRecord>? _beforeCreationPersist;

    // Optional injection permits deterministic filesystem-failure regressions.
    public ChatJournal(string directory, Action<ChatRecord>? beforePersist = null,
        Action<CreationPendingRecord>? beforeCreationPersist = null)
    {
        _directory = Path.GetFullPath(directory);
        _beforePersist = beforePersist;
        _beforeCreationPersist = beforeCreationPersist;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Only one local host may mutate a journal directory at a time.</summary>
    public IDisposable AcquireHostLease()
    {
        try
        {
            return new FileStream(Path.Combine(_directory, "host.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Another Neko host owns this state directory.");
        }
    }

    // A crash during creation cannot prove that no remote chat was created.
    // The next process must not retry until the operator has reconciled it.
    public bool TryStartCreation(string promptSha256, string? ownerId = null)
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, "creation.pending");
            if (File.Exists(path)) return false;
            var pending = new CreationPendingRecord(promptSha256, ownerId, null,
                DateTimeOffset.UtcNow);
            _beforeCreationPersist?.Invoke(pending);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen:true))
            {
                writer.Write(JsonSerializer.Serialize(pending));
                writer.Flush();
            }
            stream.Flush(flushToDisk:true);
            return true;
        }
    }

    // A legacy/corrupt marker must remain blocking and is never inferred away.
    public CreationPendingRecord? GetPendingCreation()
    {
        lock (_gate)
        {
            var file = Path.Combine(_directory, "creation.pending");
            if (!File.Exists(file)) return null;
            try
            {
                return JsonSerializer.Deserialize<CreationPendingRecord>(File.ReadAllText(file))
                    ?? new CreationPendingRecord(null,null,null,default);
            }
            catch (JsonException)
            {
                return new CreationPendingRecord(null,null,null,default);
            }
        }
    }

    public void SetCreationCorrelation(string conversationId)
    {
        lock (_gate)
        {
            var pending=GetPendingCreation();
            if (pending?.PromptSha256 is null)
                throw new InvalidOperationException("Creation evidence is unavailable.");
            if (pending.CorrelatedConversationId is not null &&
                pending.CorrelatedConversationId != conversationId)
                throw new InvalidOperationException("Creation identity cannot change.");
            var updated=pending with { CorrelatedConversationId=conversationId };
            WriteCreationPending(updated);
        }
    }

    public void RecordCreationCheck(string reason)
    {
        lock (_gate)
        {
            var pending=GetPendingCreation();
            if (pending?.PromptSha256 is null) return;
            WriteCreationPending(pending with {
                LastCheckedAt=DateTimeOffset.UtcNow, LastCheckReason=reason
            });
        }
    }

    private void WriteCreationPending(CreationPendingRecord updated)
    {
        _beforeCreationPersist?.Invoke(updated);
        WriteAtomic(Path.Combine(_directory, "creation.pending"), updated);
    }

    public void ClearCreation()
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, "creation.pending");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // The identity check and removal are one critical section. Otherwise a
    // concurrent creation could replace the guard between read and delete.
    public bool ClearCorrelatedCreation(string ownerId, string conversationId)
    {
        lock (_gate)
        {
            var pending = GetPendingCreation();
            if (pending?.OwnerId != ownerId ||
                pending.CorrelatedConversationId != conversationId)
                return false;
            File.Delete(Path.Combine(_directory, "creation.pending"));
            return true;
        }
    }

    public IReadOnlyList<ChatRecord> List()
    {
        lock (_gate)
            return Directory.GetFiles(_directory, "*.json")
                .Select(Read).OfType<ChatRecord>()
                .OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    public ChatRecord? Get(string id)
    {
        lock (_gate) return Read(FileName(id));
    }

    /// <summary>Atomic read-modify-write so refreshing history never rewinds status.</summary>
    public ChatRecord? Update(string id, Func<ChatRecord, ChatRecord> change)
    {
        lock (_gate)
        {
            var current = Read(FileName(id));
            if (current is null) return null;
            var updated = change(current);
            if (updated.Id != id)
                throw new InvalidOperationException("Conversation identity cannot change.");
            Set(updated);
            return updated;
        }
    }

    public void Set(ChatRecord record)
    {
        lock (_gate)
        {
            _beforePersist?.Invoke(record);
            WriteAtomic(FileName(record.Id),
                record with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    // Shared durable checkpoint protocol for conversation journals and
    // creation markers. Faults must leave the previous file intact.
    private static void WriteAtomic<T>(string file, T value)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream,
                           new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(value));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private ChatRecord? Read(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(file));
            return record is not null && FileName(record.Id) == file ? record : null;
        }
        catch (JsonException) { return null; }
    }

    private string FileName(string id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return Path.Combine(_directory, hash + ".json");
    }
}
