using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

/// <summary>Atomic fsynced PR state, under the host's existing exclusive state lease.</summary>
internal sealed class PrWorkflowStore
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Action<PrWorkflow>? _beforePersist;

    public PrWorkflowStore(string stateDirectory, TimeProvider? clock = null,
        Action<PrWorkflow>? beforePersist = null)
    {
        _directory = Path.Combine(Path.GetFullPath(stateDirectory), "pr-workflows");
        _clock = clock ?? TimeProvider.System;
        _beforePersist = beforePersist;
        Directory.CreateDirectory(_directory);
    }

    internal static bool ValidRepository(string? repository)
    {
        if (repository is null || repository.Length is < 3 or > 150) return false;
        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length is >= 1 and <= 75 &&
            p[0] is not ('.' or '-') && p.All(c =>
                c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or '-' or '_' or '.'));
    }

    public PrWorkflow? Get(string repository, int number)
    {
        if (!ValidRepository(repository) || number < 1) return null;
        lock (_gate) return Read(PathFor(repository, number));
    }

    public IReadOnlyList<PrWorkflow> List()
    {
        lock (_gate) return Directory.GetFiles(_directory, "*.json")
            .Select(ReadRequired).OrderBy(x => x.Repository).ThenBy(x => x.Number).ToArray();
    }

    public PrWorkflow Create(string repository, int number, string head, string baseSha,
        string creator, string developer, string manager)
    {
        if (!ValidRepository(repository) || number < 1 || !PrReviewGate.ValidSha(head) ||
            !PrReviewGate.ValidSha(baseSha) ||
            new[] { creator, developer, manager }.Any(x => string.IsNullOrWhiteSpace(x) ||
                x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException("Invalid workflow identity.");
        lock (_gate)
        {
            var file = PathFor(repository, number);
            if (File.Exists(file)) throw new InvalidOperationException("Workflow already exists.");
            var now = _clock.GetUtcNow();
            var workflow = new PrWorkflow(repository, number, head, baseSha, creator,
                developer, manager, now, Audit: new[] {
                    new PrAuditEvent(now, "created", "workflow_reserved")
                });
            Write(file, workflow);
            return workflow;
        }
    }

    public PrWorkflow Update(string repository, int number, Func<PrWorkflow, PrWorkflow> change)
    {
        lock (_gate)
        {
            var file = PathFor(repository, number);
            var old = ReadRequired(file);
            var changed = change(old);
            if (changed.Repository != old.Repository || changed.Number != old.Number ||
                changed.CreatorConversationId != old.CreatorConversationId ||
                changed.DeveloperConversationId != old.DeveloperConversationId ||
                changed.ManagerConversationId != old.ManagerConversationId ||
                changed.CreatedAt != old.CreatedAt)
                throw new InvalidOperationException("Workflow identity is immutable.");
            if (changed == old) return old;
            if (!PrReviewGate.ValidSha(changed.HeadSha) ||
                !PrReviewGate.ValidSha(changed.BaseSha) ||
                changed.Audit is null || changed.Audit.Count > 10000)
                throw new InvalidOperationException("Invalid workflow checkpoint.");
            Write(file, changed);
            return changed;
        }
    }

    internal PrWorkflow Append(PrWorkflow workflow, string action, string detail,
        string? attemptId = null)
    {
        var audit = workflow.Audit?.ToList() ?? [];
        audit.Add(new PrAuditEvent(_clock.GetUtcNow(), action, detail, attemptId));
        return workflow with { Audit = audit };
    }

    private string PathFor(string repository, int number) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(repository + "#" + number))) + ".json");

    private PrWorkflow? Read(string path) =>
        File.Exists(path) ? ReadRequired(path) : null;

    private PrWorkflow ReadRequired(string path)
    {
        PrWorkflow? value;
        try { value = JsonSerializer.Deserialize<PrWorkflow>(File.ReadAllText(path)); }
        catch (JsonException e) { throw new InvalidDataException("Workflow checkpoint unreadable.", e); }
        if (value is null || !ValidRepository(value.Repository) || value.Number < 1 ||
            !PrReviewGate.ValidSha(value.HeadSha) || !PrReviewGate.ValidSha(value.BaseSha) ||
            PathFor(value.Repository, value.Number) != path ||
            value.CreatedAt == default || value.Audit is null || value.Audit.Count > 10000 ||
            value.Audit.Any(a => a.At == default || string.IsNullOrWhiteSpace(a.Action)))
            throw new InvalidDataException("Workflow checkpoint invalid.");
        return value;
    }

    private void Write(string file, PrWorkflow workflow)
    {
        _beforePersist?.Invoke(workflow);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(JsonSerializer.Serialize(workflow));
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
