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

        var positional = new List<string>();
        var skipConfigValue = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (skipConfigValue) { skipConfigValue = false; continue; }
            if (args[i] == "--config")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                    throw new ArgumentException("--config requires a file path.");
                skipConfigValue = true;
                continue;
            }
            if (args[i] == "--wait") continue;
            positional.Add(args[i]);
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
            case "watch" when positional.Count == 1:
                await WatchAsync(client, positional[0]);
                return;
            default: throw new ArgumentException("Unsupported command or incorrect arguments.");
        }

        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var result = await response.Content.ReadAsStringAsync();
        if (command == "read" && TryWriteReadStatus(
            response.StatusCode, result, Console.Out, Console.Error, out var exitCode))
        {
            if (exitCode != 0) Environment.ExitCode = exitCode;
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The local service rejected the request.");
        Console.WriteLine(result);
        if (!wait || command is not ("create" or "send")) return;

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        if (!root.TryGetProperty("disposition", out var disposition) ||
            (disposition.ValueKind == JsonValueKind.String
                ? disposition.GetString() != nameof(SendDisposition.Accepted)
                : disposition.GetInt32() != (int)SendDisposition.Accepted)) return;
        var id = command == "send" ? positional[0] :
            root.TryGetProperty("conversationId", out var property) ? property.GetString() : null;
        if (id is not null) await WatchAsync(client, id);
    }

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
            if (kind == "Final" && operation is "Complete" or "Fail")
            {
                Console.WriteLine(operation == "Complete" ? "\n[complete]" : "\n[unconfirmed]");
                return;
            }
        }
    }
}
