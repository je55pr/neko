using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal sealed record ChatRecord(
    string Id, string Status, VisibleConversation? History = null,
    DateTimeOffset UpdatedAt = default, string? ProgressTurnId = null);

/// <summary>Local checkpoint data. Never stored in the repository.</summary>
internal sealed class ChatJournal
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Action<ChatRecord>? _beforePersist;

    // Optional injection permits deterministic filesystem-failure regressions.
    public ChatJournal(string directory, Action<ChatRecord>? beforePersist = null)
    {
        _directory = Path.GetFullPath(directory);
        _beforePersist = beforePersist;
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
    public bool TryStartCreation()
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, "creation.pending");
            if (File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            return true;
        }
    }

    public void ClearCreation()
    {
        lock (_gate)
        {
            var path = Path.Combine(_directory, "creation.pending");
            if (File.Exists(path)) File.Delete(path);
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
            var file = FileName(record.Id);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Persist the new checkpoint before replacing the previous
                // one. A crash must not lose the last safe delivery state.
                using (var stream=new FileStream(temp,FileMode.CreateNew,
                           FileAccess.Write,FileShare.None))
                {
                    using (var writer=new StreamWriter(stream,new UTF8Encoding(false),
                               leaveOpen:true))
                    {
                        writer.Write(JsonSerializer.Serialize(record with {
                            UpdatedAt=DateTimeOffset.UtcNow }));
                        writer.Flush();
                    }
                    stream.Flush(flushToDisk:true);
                }
                File.Move(temp, file, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
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
