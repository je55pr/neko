using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Neko;
using Neko.Headless;

var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
var configIndex = Array.IndexOf(args, "--config");
if (configIndex >= 0 &&
    (configIndex + 1 >= args.Length || args[configIndex + 1].StartsWith("--")))
{
    Console.Error.WriteLine("The --config option requires a file path.");
    Environment.ExitCode = 2;
    return;
}
var configPath = configIndex >= 0 && configIndex + 1 < args.Length
    ? args[configIndex + 1]
    : Environment.GetEnvironmentVariable("NEKO_CONFIG_FILE") ?? "secrets.json";

if (command == "help")
{
    Console.WriteLine("neko serve|health|list|create|send|read|status|stop|watch|reconcile|reconcile-creation|reconcile-manager|manager-queue|manager-messages|manager-message|manager-cancel|manager-retry|pr-register|pr-status|pr-refresh|pr-reviewer|pr-publish-review|pr-merge [args] [--config path] [--wait]");
    Console.WriteLine("pr-register <repo> <number> <creator> <developer> <manager>; pr-status|pr-merge <repo> <number>");
    Console.WriteLine("pr-reviewer <repo> <number> <reviewer-account> <task>; pr-publish-review <repo> <number>");
    Console.WriteLine("create <text>; send <id> <text>; read|status|stop|watch|reconcile <id>; reconcile-creation; reconcile-manager <manager/name>");
    Console.WriteLine("manager-queue <manager/name> <text> [--key key]; manager-messages <manager/name>; manager-message|manager-cancel|manager-retry <manager/name> <message-id>");
    Console.WriteLine("Stop requires provider support; otherwise it returns Unsupported/stop_not_supported without cancelling.");
    return;
}

try
{
    var config = HostConfiguration.Read(configPath);
    if (command != "serve")
    {
        await LocalClient.RunAsync(config, command, args);
        return;
    }

    var provider = config.CreateProvider(); // Fails closed if scoped creation is unsupported.
    var journal = new ChatJournal(config.StateDirectory);
    using var hostLease = journal.AcquireHostLease();
    using var runtime = new ChatRuntime(provider, journal);
    var managers = new ManagerService(new ManagerRegistry(config.StateDirectory), runtime);
    using var inbox = new ManagerInboxService(
        new ManagerInboxJournal(config.StateDirectory), managers, runtime);
    inbox.Start();
    var reviewPolicy = config.ReviewGate ?? new PrReviewPolicy([], "", "");
    var prWorkflows = new PrWorkflowService(new PrWorkflowStore(config.StateDirectory),
        new GhPrSourceAdapter(config.ExternalPrCliExecutable, reviewPolicy), runtime,
        reviewPolicy);

    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + config.AccessToken;
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });

    app.MapGet("/health", () => Results.Json(new { status = "ready" }));
    app.MapGet("/pr-workflows", () => Results.Json(prWorkflows.List()));
    app.MapPost("/pr-workflows", (PrWorkflowCreateRequest body, CancellationToken ct) =>
        PrWorkflowEndpoints.CreateAsync(prWorkflows, body, ct));
    app.MapGet("/pr-workflows/{owner}/{repo}/{number:int}",
        (string owner, string repo, int number, CancellationToken ct) =>
            PrWorkflowEndpoints.InspectAsync(prWorkflows, owner + "/" + repo, number, ct));
    app.MapPost("/pr-workflows/{owner}/{repo}/{number:int}/refresh",
        (string owner, string repo, int number, CancellationToken ct) =>
            PrWorkflowEndpoints.RefreshAsync(prWorkflows, owner + "/" + repo, number, ct));
    app.MapPost("/pr-workflows/{owner}/{repo}/{number:int}/reviewer",
        (string owner, string repo, int number, PrReviewerRequest body, CancellationToken ct) =>
            PrWorkflowEndpoints.LaunchAsync(prWorkflows, owner + "/" + repo, number, body, ct));
    app.MapPost("/pr-workflows/{owner}/{repo}/{number:int}/publish-review",
        (string owner, string repo, int number, CancellationToken ct) =>
            PrWorkflowEndpoints.PublishAsync(prWorkflows, owner + "/" + repo, number, ct));
    app.MapPost("/pr-workflows/{owner}/{repo}/{number:int}/merge",
        (string owner, string repo, int number, CancellationToken ct) =>
            PrWorkflowEndpoints.MergeAsync(prWorkflows, owner + "/" + repo, number, ct));
    app.MapGet("/managers", () => ManagerEndpoints.List(managers));
    app.MapPost("/managers", (ManagerCreateRequest body, CancellationToken cancellation) =>
        ManagerEndpoints.CreateAsync(managers, body, cancellation));
    app.MapGet("/managers/{name}", (string name, CancellationToken cancellation) =>
        ManagerEndpoints.GetAsync(managers, name, cancellation));
    app.MapPost("/managers/{name}/reconcile", (string name, CancellationToken cancellation) =>
        ManagerEndpoints.ReconcileAsync(managers, name, cancellation));
    app.MapGet("/managers/{name}/history", (string name, CancellationToken cancellation) =>
        ManagerEndpoints.ReadAsync(managers, name, cancellation));
    app.MapPost("/managers/{name}/messages", (string name, TextRequest body,
        CancellationToken cancellation) =>
        ManagerEndpoints.SendAsync(managers, name, body.Text ?? "", cancellation));
    app.MapGet("/managers/{name}/inbox", (string name) =>
        ManagerEndpoints.ListInbox(inbox, name));
    app.MapPost("/managers/{name}/inbox", (string name, InboxEnqueueRequest body) =>
        ManagerEndpoints.EnqueueInbox(inbox, name, body));
    app.MapGet("/managers/{name}/inbox/{messageId}", (string name, string messageId) =>
        ManagerEndpoints.GetInbox(inbox, name, messageId));
    app.MapPost("/managers/{name}/inbox/{messageId}/cancel", (string name, string messageId) =>
        ManagerEndpoints.CancelInbox(inbox, name, messageId));
    app.MapPost("/managers/{name}/inbox/{messageId}/retry", (string name, string messageId) =>
        ManagerEndpoints.RetryInbox(inbox, name, messageId));
    // Listing is metadata only; full transcripts belong in GET by ID.
    app.MapGet("/conversations", () => Results.Json(runtime.List()
        .Select(record => new {
            record.Id,record.Status,record.UpdatedAt,
            Title=record.History?.Title
        })));
    app.MapGet("/conversations/{id}/status", (string id) =>
    {
        var status = runtime.GetStatus(id);
        return status is null ? Results.NotFound() : Results.Json(status);
    });
    app.MapGet("/conversations/{id}", (string id, CancellationToken cancellation) =>
        ConversationHistoryEndpoint.ReadAsync(runtime, id, cancellation));
    app.MapPost("/conversations", async (TextRequest body, CancellationToken cancellation) =>
        Results.Json(await runtime.CreateAsync(body.Text ?? "", cancellation)));
    app.MapPost("/conversations/{id}/messages", async (
        string id, TextRequest body, CancellationToken cancellation) =>
        Results.Json(await runtime.SendAsync(id, body.Text ?? "", cancellation)));
    app.MapPost("/conversations/creation/reconcile", async (CancellationToken cancellation) =>
        Results.Json(await runtime.ReconcileCreationAsync(null, cancellation)));
    app.MapPost("/conversations/{id}/reconcile", async (string id, CancellationToken cancellation) =>
    {
        var result = await runtime.ReconcileAsync(id, cancellation);
        return result.Outcome == "not_found" ? Results.NotFound() : Results.Json(result);
    });
    app.MapPost("/conversations/{id}/stop", async (string id, CancellationToken cancellation) =>
        Results.Json(await runtime.StopAsync(id, cancellation)));

    app.MapGet("/conversations/{id}/events", async (string id, HttpContext context) =>
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await foreach (var update in runtime.WatchAsync(id, context.RequestAborted))
        {
            var json = JsonSerializer.Serialize(update, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new JsonStringEnumConverter() } });
            await context.Response.WriteAsync("data: " + json + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    });

    Console.WriteLine($"Neko headless listening on 127.0.0.1:{config.Port}.");
    await app.RunAsync();
}
catch (Exception exception)
{
    // Deliberately exclude provider exception messages and local paths.
    Console.Error.WriteLine(exception is OperationCanceledException
        ? "Cancelled." : "Neko failed to start or complete the command. Check local configuration and logs.");
    Environment.ExitCode = 1;
}

internal sealed record TextRequest(string? Text);
