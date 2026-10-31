using System.Diagnostics;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

/// <summary>Repository-specific infrastructure. Domain policy knows no hosting vendor.</summary>
internal sealed class GhPrSourceAdapter : IPrSourceAdapter
{
    private static async Task<JsonDocument> ApiAsync(
        CancellationToken cancellation, params string[] args)
    {
        var start = new ProcessStartInfo("gh") {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("api");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("External PR adapter unavailable.");
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var errors = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
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

        using var checks = await ApiAsync(cancellation,
            "repos/" + repository + "/commits/" + head + "/check-runs?per_page=100");
        var checkRoot = checks.RootElement;
        var checkRuns = Member(checkRoot, "check_runs");
        if (checkRoot.GetProperty("total_count").GetInt32() > 100)
            throw new InvalidOperationException("Check list exceeds verified page.");
        var gathered = new Dictionary<string, PrCheck>(StringComparer.Ordinal);
        // The most recent check run is authoritative for duplicate run names.
        foreach (var run in checkRuns.EnumerateArray().OrderByDescending(r =>
            r.GetProperty("id").GetInt64()))
        {
            var name = Member(run, "name").GetString() ?? "";
            gathered.TryAdd(name, new PrCheck(name, head,
                GetString(run, "status") ?? "", GetString(run, "conclusion")));
        }
        using var statuses = await ApiAsync(cancellation,
            "repos/" + repository + "/commits/" + head + "/status");
        var statusEntries = Member(statuses.RootElement, "statuses");
        if (statusEntries.GetArrayLength() >= 100)
            throw new InvalidOperationException("Commit status list exceeds verified page.");
        foreach (var item in statusEntries.EnumerateArray())
        {
            var name = GetString(item, "context") ?? "";
            gathered.TryAdd(name, new PrCheck(name, head, "completed",
                GetString(item, "state") == "success" ? "success" : "failure"));
        }

        using var reviews = await ApiAsync(cancellation, route + "/reviews?per_page=100");
        using var comments = await ApiAsync(cancellation, route + "/comments?per_page=100");
        if (reviews.RootElement.GetArrayLength() >= 100 ||
            comments.RootElement.GetArrayLength() >= 100)
            throw new InvalidOperationException("Review list exceeds verified page.");
        var reviewComments = comments.RootElement.EnumerateArray()
            .Where(c => !string.IsNullOrWhiteSpace(GetString(c, "body")))
            .Select(c => c.GetProperty("pull_request_review_id").GetInt64())
            .ToHashSet();
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
            sourceReviews.Add(new PrReview(GetString(r, "html_url") ?? "",
                Member(r, "user", "login").GetString() ?? "",
                GetString(r, "commit_id") ?? "", verdict,
                !string.IsNullOrWhiteSpace(GetString(r, "body")) ||
                    reviewComments.Contains(r.GetProperty("id").GetInt64()),
                submitted));
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
        return new PrSourceStatus(repository, number, head, baseSha, state, author,
            merged, mergeable, gathered.Values.ToArray(), sourceReviews, unresolved);
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
