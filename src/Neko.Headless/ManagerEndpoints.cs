using Microsoft.AspNetCore.Http;
using Neko;

namespace Neko.Headless;

internal sealed record ManagerCreateRequest(string? Id, string? Text);
internal sealed record InboxEnqueueRequest(string? Text, string? IdempotencyKey);

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

    public static async Task<IResult> ReconcileAsync(
        ManagerService managers, string name, CancellationToken cancellation)
    {
        var result = await managers.ReconcileAsync(IdFor(name), cancellation);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    public static IResult ListInbox(ManagerInboxService inbox, string name)
    {
        var list = inbox.List(IdFor(name));
        return list is null ? Results.NotFound() : Results.Json(list);
    }

    public static IResult EnqueueInbox(ManagerInboxService inbox, string name,
        InboxEnqueueRequest request)
    {
        var (result, error) = inbox.Enqueue(IdFor(name), request.Text ?? "",
            request.IdempotencyKey);
        return error switch {
            "manager_not_found" => Results.NotFound(),
            "invalid_submission" => Results.BadRequest(new { reason = error }),
            "idempotency_conflict" => Results.Conflict(new { reason = error }),
            null => Results.Json(result, statusCode: result!.Duplicate
                ? StatusCodes.Status200OK : StatusCodes.Status202Accepted),
            _ => Results.Conflict(new { reason = "inbox_unavailable" })
        };
    }

    public static IResult GetInbox(ManagerInboxService inbox, string name, string messageId)
    {
        var message = inbox.Get(IdFor(name), messageId);
        return message is null ? Results.NotFound() : Results.Json(message);
    }

    public static IResult CancelInbox(ManagerInboxService inbox, string name, string messageId)
    {
        if (inbox.Get(IdFor(name), messageId) is null) return Results.NotFound();
        var message = inbox.Cancel(IdFor(name), messageId);
        return message is null ? Results.Conflict(new { reason = "message_not_queued" })
            : Results.Json(message);
    }

    public static IResult RetryInbox(ManagerInboxService inbox, string name, string messageId)
    {
        if (inbox.Get(IdFor(name), messageId) is null) return Results.NotFound();
        try
        {
            var message = inbox.Retry(IdFor(name), messageId);
            return message is null ? Results.Conflict(new { reason = "message_not_rejected" })
                : Results.Json(message);
        }
        catch (InvalidOperationException)
        { return Results.Conflict(new { reason = "rejection_not_retryable" }); }
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
