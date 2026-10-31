using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

/// <summary>Repository-specific infrastructure. Domain policy knows no hosting vendor.</summary>
internal sealed class GhPrSourceAdapter : IPrSourceAdapter
{
    private readonly string? _trustedExecutable;

    // This is an opt-in adapter: missing configuration disables external PR
    // operations without preventing legacy headless hosting.
    public GhPrSourceAdapter(string? trustedExecutable)
    {
        if (string.IsNullOrWhiteSpace(trustedExecutable)) return;
        if (!Path.IsPathFullyQualified(trustedExecutable) ||
            !File.Exists(trustedExecutable) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(trustedExecutable), OperatingSystem.IsWindows() ? "gh.exe" : "gh"))
            throw new ArgumentException("A trusted absolute PR CLI executable is required.");
        _trustedExecutable = Path.GetFullPath(trustedExecutable);
    }

    private async Task<JsonDocument> ApiAsync(
        CancellationToken cancellation, params string[] args)
    {
        if (_trustedExecutable is null)
            throw new InvalidOperationException("External PR adapter is not configured.");
        // The executable is validated as an existing fully-qualified local
        // gh binary during configuration. No shell is invoked and arguments
        // are passed separately, including untrusted review text.
        var start = new ProcessStartInfo(_trustedExecutable) {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("api");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("External PR adapter unavailable.");
        using var killOnCancel = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var json = await output;
        await errors; // Never expose CLI diagnostics or credential-bearing paths.
        if (process.ExitCode != 0)
            throw new InvalidOperationException("External PR status could not be verified.");
        return JsonDocument.Parse(json);
    }

    private static JsonElement Member(JsonElement root, params string[] keys)
    {
        foreach (var key in keys) root = root.GetProperty(key);
        return root;
    }

    private static string? GetString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    // A deleted source account has no login. Keep those review identities
    // distinct so one deleted account's approval cannot erase another's
    // active change request. Such markers never match configured accounts.
    internal static string ReviewerIdentity(JsonElement review)
    {
        if (review.TryGetProperty("user", out var user) &&
            user.ValueKind == JsonValueKind.Object)
        {
            var login = GetString(user, "login");
            if (!string.IsNullOrWhiteSpace(login)) return login;
        }
        var id = review.GetProperty("id").GetInt64();
        if (id <= 0) throw new InvalidOperationException("Review identity unavailable.");
        return "deleted-review:" + id;
    }

    internal static string BodyDigest(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string? ProducerId(JsonElement source, string entity, string prefix)
    {
        if (!source.TryGetProperty(entity, out var publisher) ||
            publisher.ValueKind != JsonValueKind.Object ||
            !publisher.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var value))
            return null;
        return prefix + ":" + value;
    }

    internal static long? GitHubActionsRunId(JsonElement run, string repository)
    {
        if (!run.TryGetProperty("app", out var app) ||
            GetString(app, "slug") != "github-actions" ||
            !Uri.TryCreate(GetString(run, "details_url"), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "github.com")
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 7 ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                parts[0] + "/" + parts[1], repository) ||
            parts[2] != "actions" || parts[3] != "runs" ||
            parts[5] != "job" || !long.TryParse(parts[6], out _) ||
            !long.TryParse(parts[4], out var runId) || runId <= 0)
            return null;
        return runId;
    }

    internal static HashSet<long> AssociatedReviewIds(JsonElement comments) =>
        comments.EnumerateArray()
            .Where(c => !string.IsNullOrWhiteSpace(GetString(c, "body")))
            .Select(c => c.TryGetProperty("pull_request_review_id", out var linked) &&
                linked.ValueKind == JsonValueKind.Number &&
                linked.TryGetInt64(out var value) ? (long?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToHashSet();

    public async Task<PrSourceStatus> ReadAsync(
        string repository, int number, CancellationToken cancellation)
    {
        if (!PrWorkflowStore.ValidRepository(repository) || number < 1)
            throw new ArgumentException("Invalid external PR identity.");
        var route = "repos/" + repository + "/pulls/" + number;
        using var pull = await ApiAsync(cancellation, route);
        var pr = pull.RootElement;
        var head = Member(pr, "head", "sha").GetString() ?? "";
        var baseSha = Member(pr, "base", "sha").GetString() ?? "";
        if (!PrReviewGate.ValidSha(head) || !PrReviewGate.ValidSha(baseSha))
            throw new InvalidOperationException("Source SHAs unavailable.");
        var state = Member(pr, "state").GetString() ?? "";
        var merged = Member(pr, "merged").GetBoolean();
        var mergeable = pr.TryGetProperty("mergeable", out var merge) &&
            merge.ValueKind == JsonValueKind.True;
        var author = Member(pr, "user", "login").GetString() ?? "";
        var canonicalRepository = Member(pr, "base", "repo", "full_name").GetString() ?? "";
        if (!PrWorkflowStore.ValidRepository(canonicalRepository) ||
            !StringComparer.OrdinalIgnoreCase.Equals(canonicalRepository, repository))
            throw new InvalidOperationException("Canonical repository identity unavailable.");

        using var checks = await ApiAsync(cancellation,
            "repos/" + repository + "/commits/" + head + "/check-runs?per_page=100");
        var checkRoot = checks.RootElement;
        var checkRuns = Member(checkRoot, "check_runs");
        if (checkRoot.GetProperty("total_count").GetInt32() > 100)
            throw new InvalidOperationException("Check list exceeds verified page.");
        var gathered = new List<PrCheck>();
        var actionWorkflows = new Dictionary<long, string>();
        // Keep distinct publishers and workflows. A same-named untrusted or
        // failing run must not shadow or satisfy the configured trusted run.
        foreach (var run in checkRuns.EnumerateArray())
        {
            var name = Member(run, "name").GetString() ?? "";
            var isActions = GetString(Member(run, "app"), "slug") == "github-actions";
            string? workflowId = null;
            if (isActions)
            {
                var runId = GitHubActionsRunId(run, canonicalRepository);
                if (runId is not null)
                {
                    if (!actionWorkflows.TryGetValue(runId.Value, out workflowId))
                    {
                        using var detail = await ApiAsync(cancellation, "repos/" +
                            canonicalRepository + "/actions/runs/" + runId.Value);
                        if (GetString(detail.RootElement, "head_sha") != head ||
                            !detail.RootElement.TryGetProperty(
                                "workflow_id", out var workflow) ||
                            workflow.ValueKind != JsonValueKind.Number ||
                            !workflow.TryGetInt64(out var workflowNumber) ||
                            workflowNumber <= 0)
                            throw new InvalidOperationException(
                                "Action workflow identity unavailable.");
                        workflowId = "workflow:" + workflowNumber;
                        actionWorkflows.Add(runId.Value, workflowId);
                    }
                }
            }
            gathered.Add(new PrCheck(name, head,
                GetString(run, "status") ?? "", GetString(run, "conclusion"),
                ProducerId(run, "app", "app"), workflowId, isActions,
                run.GetProperty("id").GetInt64()));
        }
        using var statuses = await ApiAsync(cancellation,
            "repos/" + repository + "/commits/" + head + "/status");
        var statusEntries = Member(statuses.RootElement, "statuses");
        if (statusEntries.GetArrayLength() >= 100)
            throw new InvalidOperationException("Commit status list exceeds verified page.");
        foreach (var item in statusEntries.EnumerateArray())
        {
            var name = GetString(item, "context") ?? "";
            gathered.Add(new PrCheck(name, head, "completed",
                GetString(item, "state") == "success" ? "success" : "failure",
                ProducerId(item, "creator", "user"), RunId:
                    item.TryGetProperty("id", out var statusId) &&
                    statusId.ValueKind == JsonValueKind.Number
                        ? statusId.GetInt64() : 0));
        }

        using var reviews = await ApiAsync(cancellation, route + "/reviews?per_page=100");
        using var comments = await ApiAsync(cancellation, route + "/comments?per_page=100");
        if (reviews.RootElement.GetArrayLength() >= 100 ||
            comments.RootElement.GetArrayLength() >= 100)
            throw new InvalidOperationException("Review list exceeds verified page.");
        // Standalone PR comments can legitimately have a null review ID.
        // They do not count as evidence for a particular submitted review.
        var reviewComments = AssociatedReviewIds(comments.RootElement);
        var sourceReviews = new List<PrReview>();
        foreach (var r in reviews.RootElement.EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(GetString(r, "submitted_at"), out var submitted))
                continue;
            var verdict = (GetString(r, "state") ?? "").ToLowerInvariant() switch {
                "approved" => "approved",
                "commented" => "commented",
                "changes_requested" => "changes_requested",
                _ => "unrecognized"
            };
            var body = GetString(r, "body") ?? "";
            sourceReviews.Add(new PrReview(GetString(r, "html_url") ?? "",
                ReviewerIdentity(r),
                GetString(r, "commit_id") ?? "", verdict,
                !string.IsNullOrWhiteSpace(body) ||
                    reviewComments.Contains(r.GetProperty("id").GetInt64()),
                submitted, BodyDigest(body)));
        }

        var parts = repository.Split('/');
        const string query = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100){nodes{isResolved}pageInfo{hasNextPage}}}}}";
        using var threads = await ApiAsync(cancellation, "graphql", "-f", "query=" + query,
            "-f", "owner=" + parts[0], "-f", "name=" + parts[1],
            "-F", "number=" + number);
        var threadsRoot = Member(threads.RootElement, "data", "repository", "pullRequest",
            "reviewThreads");
        if (Member(threadsRoot, "pageInfo", "hasNextPage").GetBoolean())
            throw new InvalidOperationException("Review threads exceed verified page.");
        var unresolved = Member(threadsRoot, "nodes").EnumerateArray()
            .Count(n => !Member(n, "isResolved").GetBoolean());
        return new PrSourceStatus(canonicalRepository, number, head, baseSha, state, author,
            merged, mergeable, gathered.ToArray(), sourceReviews, unresolved);
    }

    public async Task<string> AuthenticatedPublisherAsync(CancellationToken cancellation)
    {
        using var user = await ApiAsync(cancellation, "user");
        var login = GetString(user.RootElement, "login");
        if (string.IsNullOrWhiteSpace(login))
            throw new InvalidOperationException("PR publishing principal unverified.");
        return login;
    }

    public async Task<PrReview> PostReviewAsync(string repository, int number,
        string expectedHead, string body, CancellationToken cancellation)
    {
        if (!PrWorkflowStore.ValidRepository(repository) || number < 1 ||
            !PrReviewGate.ValidSha(expectedHead) ||
            string.IsNullOrWhiteSpace(body) || body.Length > 12000)
            throw new ArgumentException("Invalid external review post request.");
        using var result = await ApiAsync(cancellation, "-X", "POST",
            "repos/" + repository + "/pulls/" + number + "/reviews",
            "-f", "commit_id=" + expectedHead, "-f", "event=COMMENT",
            "-f", "body=" + body);
        var root = result.RootElement;
        if (GetString(root, "commit_id") != expectedHead ||
            GetString(root, "body") != body ||
            !DateTimeOffset.TryParse(GetString(root, "submitted_at"), out var submitted) ||
            !Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var url) ||
            url.Scheme != "https")
            throw new InvalidOperationException("Published review receipt is incomplete.");
        return new PrReview(url.ToString(), Member(root, "user", "login").GetString() ?? "",
            expectedHead, (GetString(root, "state") ?? "").ToLowerInvariant(),
            true, submitted, BodyDigest(body));
    }

    public async Task<string> MergeAsync(string repository, int number,
        string expectedHead, CancellationToken cancellation)
    {
        if (!PrWorkflowStore.ValidRepository(repository) || number < 1 ||
            !PrReviewGate.ValidSha(expectedHead))
            throw new ArgumentException("Invalid merge precondition.");
        // Server-side sha predicate is atomic with merge. No speculative retries.
        using var result = await ApiAsync(cancellation, "-X", "PUT",
            "repos/" + repository + "/pulls/" + number + "/merge",
            "-f", "sha=" + expectedHead, "-f", "merge_method=squash");
        if (!Member(result.RootElement, "merged").GetBoolean())
            throw new InvalidOperationException("Merge was not confirmed.");
        return GetString(result.RootElement, "sha")
            ?? throw new InvalidOperationException("Merge SHA missing.");
    }
}
