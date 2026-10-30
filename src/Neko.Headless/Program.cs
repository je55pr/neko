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
var configPath = configIndex >= 0 && configIndex + 1 < args.Length
    ? args[configIndex + 1]
    : Environment.GetEnvironmentVariable("NEKO_CONFIG_FILE") ?? "secrets.json";

if (command == "help")
{
    Console.WriteLine("neko serve|health|list|create|send|read|status|stop|watch [args] [--wait] [--config path]");
    Console.WriteLine("create <text>; send <id> <text>; read|status|stop|watch <id>");
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
    using var runtime = new ChatRuntime(provider, new ChatJournal(config.StateDirectory));

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
    app.MapGet("/conversations", () => Results.Json(runtime.List()));
    app.MapGet("/conversations/{id}/status", (string id) =>
    {
        var record = runtime.List().FirstOrDefault(x => x.Id == id);
        return record is null ? Results.NotFound() :
            Results.Json(new { record.Id, record.Status, record.UpdatedAt });
    });
    app.MapGet("/conversations/{id}", async (string id, CancellationToken cancellation) =>
    {
        var result = await runtime.ReadAsync(id, cancellation);
        return result is null ? Results.NotFound() : Results.Json(result);
    });
    app.MapPost("/conversations", async (TextRequest body, CancellationToken cancellation) =>
        Results.Json(await runtime.CreateAsync(body.Text ?? "", cancellation)));
    app.MapPost("/conversations/{id}/messages", async (
        string id, TextRequest body, CancellationToken cancellation) =>
        Results.Json(await runtime.SendAsync(id, body.Text ?? "", cancellation)));
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
