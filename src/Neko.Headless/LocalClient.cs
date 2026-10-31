using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal static class LocalClient
{
    public static async Task RunAsync(HostConfiguration config, string command, string[] args)
    {
        using var client = new HttpClient { BaseAddress =
            new Uri($"http://127.0.0.1:{config.Port}/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.AccessToken);
        var exitCode = await RunCommandAsync(client, command, args, Console.Out, Console.Error);
        if (exitCode != 0) Environment.ExitCode = exitCode;
    }

    // Exercise the actual command and HTTP path with injected in-memory responses.
    internal static async Task<int> RunCommandAsync(
        HttpClient client, string command, string[] args, TextWriter output, TextWriter error)
    {
        var positional = new List<string>();
        string? idempotencyKey = null;
        string? operationId = null;
        DateTimeOffset? deadline = null;
        var i = 1;
        while (i < args.Length)
        {
            if (args[i] == "--config")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--config requires a file path.");
                i += 2;
                continue;
            }
            if (args[i] == "--wait") { i++; continue; }
            if (args[i] == "--operation")
            {
                if (command != "manager-track" || i + 1 >= args.Length ||
                    args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--operation requires a value for manager-track.");
                operationId = args[i + 1];
                i += 2;
                continue;
            }
            if (args[i] == "--deadline")
            {
                if (command != "manager-track" || i + 1 >= args.Length ||
                    !DateTimeOffset.TryParse(args[i + 1],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                    throw new ArgumentException("--deadline requires an ISO timestamp.");
                deadline = parsed;
                i += 2;
                continue;
            }
            if (args[i] == "--key")
            {
                if (command != "manager-queue" || i + 1 >= args.Length ||
                    args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--key requires a value for manager-queue.");
                idempotencyKey ??= args[i + 1];
                i += 2;
                continue;
            }
            positional.Add(args[i]);
            i++;
        }

        var wait = args.Contains("--wait");
        string route;
        HttpMethod method = HttpMethod.Get;
        string? body = null;
        switch (command)
        {
            case "health": route = "health"; break;
            case "list": route = "conversations"; break;
            case "create" when positional.Count == 1:
                route = "conversations";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { text = positional[0] });
                break;
            case "send" when positional.Count == 2:
                route = $"conversations/{Uri.EscapeDataString(positional[0])}/messages";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { text = positional[1] });
                break;
            case "read" when positional.Count == 1:
                route = $"conversations/{Uri.EscapeDataString(positional[0])}";
                break;
            case "status" when positional.Count == 1:
                route = $"conversations/{Uri.EscapeDataString(positional[0])}/status";
                break;
            case "stop" when positional.Count == 1:
                route = $"conversations/{Uri.EscapeDataString(positional[0])}/stop";
                method = HttpMethod.Post;
                break;
            case "reconcile" when positional.Count == 1:
                route = $"conversations/{Uri.EscapeDataString(positional[0])}/reconcile";
                method = HttpMethod.Post;
                break;
            case "reconcile-creation" when positional.Count == 0:
                route = "conversations/creation/reconcile";
                method = HttpMethod.Post;
                break;
            case "manager-create" when positional.Count == 2:
                var managerId = positional[0].StartsWith("manager/", StringComparison.Ordinal)
                    ? positional[0] : "manager/" + positional[0];
                if (!ManagerRegistry.IsValidId(managerId))
                    throw new ArgumentException("Invalid manager identity.");
                route = "managers";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { id = managerId, text = positional[1] });
                break;
            case "manager-list" when positional.Count == 0:
                route = "managers";
                break;
            case "manager-status" when positional.Count == 1:
                route = $"managers/{ManagerName(positional[0])}";
                break;
            case "manager-history" when positional.Count == 1:
                route = $"managers/{ManagerName(positional[0])}/history";
                break;
            case "manager-send" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/messages";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { text = positional[1] });
                break;
            case "reconcile-manager" when positional.Count == 1:
                var name = positional[0].StartsWith("manager/", StringComparison.Ordinal)
                    ? positional[0]["manager/".Length..] : positional[0];
                route = $"managers/{Uri.EscapeDataString(name)}/reconcile";
                method = HttpMethod.Post;
                break;
            case "manager-jobs" when positional.Count == 1:
                route = $"managers/{ManagerName(positional[0])}/jobs";
                break;
            case "manager-job" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/jobs/{Uri.EscapeDataString(positional[1])}";
                break;
            case "manager-track" when positional.Count is 2 or 4 &&
                (positional.Count == 2 || int.TryParse(positional[3], out _)):
                route = $"managers/{ManagerName(positional[0])}/jobs";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new {
                    workerConversationId = positional[1], operationId,
                    repository = positional.Count == 4 ? positional[2] : null,
                    prNumber = positional.Count == 4 ? int.Parse(positional[3]) : (int?)null,
                    deadline
                });
                break;
            case "manager-interrupt" when positional.Count == 3:
                route = $"managers/{ManagerName(positional[0])}/jobs/{Uri.EscapeDataString(positional[1])}/interrupt";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { reason = positional[2] });
                break;
            case "manager-queue" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/inbox";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new { text = positional[1], idempotencyKey });
                break;
            case "manager-messages" when positional.Count == 1:
                route = $"managers/{ManagerName(positional[0])}/inbox";
                break;
            case "manager-message" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/inbox/{Uri.EscapeDataString(positional[1])}";
                break;
            case "manager-cancel" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/inbox/{Uri.EscapeDataString(positional[1])}/cancel";
                method = HttpMethod.Post;
                break;
            case "manager-retry" when positional.Count == 2:
                route = $"managers/{ManagerName(positional[0])}/inbox/{Uri.EscapeDataString(positional[1])}/retry";
                method = HttpMethod.Post;
                break;
            case "pr-register" when positional.Count == 5 &&
                int.TryParse(positional[1], out var newNumber) && newNumber > 0:
                route = "pr-workflows";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new {
                    repository = positional[0], number = newNumber,
                    creatorConversationId = positional[2],
                    developerConversationId = positional[3],
                    managerConversationId = positional[4]
                });
                break;
            case "pr-status" when positional.Count == 2 &&
                int.TryParse(positional[1], out var statusNumber) && statusNumber > 0:
                route = PrRoute(positional[0], statusNumber);
                break;
            case "pr-refresh" when positional.Count == 2 &&
                int.TryParse(positional[1], out var refreshNumber) && refreshNumber > 0:
                route = PrRoute(positional[0], refreshNumber) + "/refresh";
                method = HttpMethod.Post;
                break;
            case "pr-reviewer" when positional.Count == 4 &&
                int.TryParse(positional[1], out var reviewerNumber) && reviewerNumber > 0:
                route = PrRoute(positional[0], reviewerNumber) + "/reviewer";
                method = HttpMethod.Post;
                body = JsonSerializer.Serialize(new {
                    reviewerAccount = positional[2], task = positional[3]
                });
                break;
            case "pr-publish-review" when positional.Count == 2 &&
                int.TryParse(positional[1], out var reviewNumber) && reviewNumber > 0:
                route = PrRoute(positional[0], reviewNumber) + "/publish-review";
                method = HttpMethod.Post;
                break;
            case "pr-merge" when positional.Count == 2 &&
                int.TryParse(positional[1], out var mergeNumber) && mergeNumber > 0:
                route = PrRoute(positional[0], mergeNumber) + "/merge";
                method = HttpMethod.Post;
                break;
            case "watch" when positional.Count == 1:
                await WatchAsync(client, positional[0]);
                return 0;
            default: throw new ArgumentException("Unsupported command or incorrect arguments.");
        }

        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        if (command == "read" && TryWriteReadStatus(
            response.StatusCode, result, output, error, out var exitCode))
            return exitCode;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The local service rejected the request.");
        output.WriteLine(result);
        if (!wait || command is not ("create" or "send")) return 0;

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        if (!root.TryGetProperty("disposition", out var disposition) ||
            (disposition.ValueKind == JsonValueKind.String
                ? disposition.GetString() != nameof(SendDisposition.Accepted)
                : disposition.GetInt32() != (int)SendDisposition.Accepted)) return 0;
        var id = command == "send" ? positional[0] :
            root.TryGetProperty("conversationId", out var property) ? property.GetString() : null;
        if (id is not null) await WatchAsync(client, id);
        return 0;
    }

    private static string PrRoute(string repository, int number)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || !PrWorkflowStore.ValidRepository(repository))
            throw new ArgumentException("Invalid repository identity.");
        return "pr-workflows/" + Uri.EscapeDataString(parts[0]) + "/" +
            Uri.EscapeDataString(parts[1]) + "/" + number;
    }

    private static string ManagerName(string id) =>
        Uri.EscapeDataString(id.StartsWith("manager/", StringComparison.Ordinal)
            ? id["manager/".Length..] : id);

    // A pending transcript is an explicit, successful HTTP read state rather than
    // an unknown conversation or a fabricated empty history.
    internal static bool TryWriteReadStatus(
        HttpStatusCode status, string payload, TextWriter output, TextWriter error,
        out int exitCode)
    {
        exitCode = 0;
        if (status == HttpStatusCode.Accepted)
        {
            output.WriteLine(payload);
            error.WriteLine("History pending for a known conversation; retry read later.");
            return true;
        }
        if (status == HttpStatusCode.NotFound)
        {
            exitCode = 1;
            error.WriteLine("Conversation not found.");
            return true;
        }
        return false;
    }

    private static async Task WatchAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(
            $"conversations/{Uri.EscapeDataString(id)}/events", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line[6..]);
            var update = document.RootElement;
            var kind = update.GetProperty("kind").GetString();
            var operation = update.GetProperty("operation").GetString();
            if (kind == "Final" && operation is "Append" or "Replace")
            {
                var text = update.GetProperty("text").GetString();
                if (operation == "Replace") Console.WriteLine();
                Console.Write(text);
            }
            if (kind == "Final" && operation is "Complete" or "Fail" or "ObservationExpired")
            {
                Console.WriteLine(operation == "Complete" ? "\n[complete]" :
                    operation == "ObservationExpired" ? "\n[observer expired; remote outcome unknown]" :
                        "\n[unconfirmed]");
                return;
            }
        }
    }
}
