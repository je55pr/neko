using Microsoft.AspNetCore.Http;
using Neko;

namespace Neko.Headless;

internal sealed record ManagerCreateRequest(string? Id, string? Text);

internal static class ManagerEndpoints
{
    // The route suffix is the manager's name. Its durable logical identity is
    // "manager/{name}", never the remote conversation ID.
    private static string IdFor(string name) => "manager/" + name;

    public static IResult List(ManagerService managers) => Results.Json(managers.List());

    public static async Task<IResult> GetAsync(
        ManagerService managers, string name, CancellationToken cancellation)
    {
        var manager = await managers.GetAsync(IdFor(name), cancellation);
        return manager is null ? Results.NotFound() : Results.Json(manager);
    }

    public static async Task<IResult> CreateAsync(
        ManagerService managers, ManagerCreateRequest request, CancellationToken cancellation)
    {
        if (!ManagerRegistry.IsValidId(request.Id) ||
            !OutboundMessagePreparation.ForNew(request.Text).IsReady)
            return Results.BadRequest(new { reason = "invalid_manager_submission" });

        var result = await managers.CreateAsync(request.Id!, request.Text!, cancellation);
        return Results.Json(result, statusCode:
            result.Receipt.Reason == "manager_already_exists"
                ? StatusCodes.Status409Conflict : StatusCodes.Status200OK);
    }

    public static async Task<IResult> SendAsync(
        ManagerService managers, string name, string text, CancellationToken cancellation)
    {
        var result = await managers.SendAsync(IdFor(name), text, cancellation);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    public static async Task<IResult> ReadAsync(
        ManagerService managers, string name, CancellationToken cancellation)
    {
        var id = IdFor(name);
        var result = await managers.ReadHistoryAsync(id, cancellation);
        if (result is null) return Results.NotFound();
        if (result.History is not null) return Results.Json(result.History);
        return Results.Json(new {
            status = "pending",
            reason = result.ConversationId is null
                ? "manager_creation_unresolved" : "provider_history_unavailable",
            managerId = id,
            result.ConversationId
        }, statusCode: StatusCodes.Status202Accepted);
    }
}
