using Microsoft.AspNetCore.Http;

namespace Neko.Headless;

internal sealed record PrWorkflowCreateRequest(string? Repository, int Number,
    string? CreatorConversationId, string? DeveloperConversationId,
    string? ManagerConversationId);
internal sealed record PrReviewerRequest(string? Task, string? ReviewerAccount);

internal static class PrWorkflowEndpoints
{
    public static async Task<IResult> CreateAsync(PrWorkflowService service,
        PrWorkflowCreateRequest body, CancellationToken cancellation)
    {
        try
        {
            return Results.Json(await service.CreateAsync(body.Repository ?? "", body.Number,
                body.CreatorConversationId ?? "", body.DeveloperConversationId ?? "",
                body.ManagerConversationId ?? "", cancellation));
        }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "invalid_workflow" }); }
        catch (InvalidOperationException) { return Results.Conflict(new { reason = "source_or_workflow_unavailable" }); }
    }

    public static async Task<IResult> InspectAsync(PrWorkflowService service,
        string repository, int number, CancellationToken cancellation)
    {
        if (service.Get(repository, number) is null) return Results.NotFound();
        try { return Results.Json(await service.InspectAsync(repository, number, cancellation)); }
        catch (Exception) { return Results.Json(new {
            workflow = service.Get(repository, number),
            gate = new { ready = false, state = "blocked",
                blockers = new[] { "authoritative_source_unavailable" } }
        }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }

    public static async Task<IResult> RefreshAsync(PrWorkflowService service,
        string repository, int number, CancellationToken cancellation)
    {
        if (service.Get(repository, number) is null) return Results.NotFound();
        try { return Results.Json(await service.RefreshRevisionAsync(
            repository, number, cancellation)); }
        catch (Exception) { return Results.Conflict(new { reason = "revision_refresh_blocked" }); }
    }

    public static async Task<IResult> LaunchAsync(PrWorkflowService service,
        string repository, int number, PrReviewerRequest body, CancellationToken cancellation)
    {
        if (service.Get(repository, number) is null) return Results.NotFound();
        try { return Results.Json(await service.LaunchReviewerAsync(repository, number,
            body.Task ?? "", body.ReviewerAccount ?? "", cancellation)); }
        catch (ArgumentException) { return Results.BadRequest(new { reason = "invalid_reviewer_request" }); }
        catch (Exception) { return Results.Conflict(new { reason = "review_launch_blocked" }); }
    }

    public static async Task<IResult> PublishAsync(PrWorkflowService service,
        string repository, int number, CancellationToken cancellation)
    {
        if (service.Get(repository, number) is null) return Results.NotFound();
        try { return Results.Json(await service.PublishReviewAsync(
            repository, number, cancellation)); }
        catch (Exception) { return Results.Conflict(new { reason = "review_post_unverified_or_blocked" }); }
    }

    public static async Task<IResult> MergeAsync(PrWorkflowService service,
        string repository, int number, CancellationToken cancellation)
    {
        if (service.Get(repository, number) is null) return Results.NotFound();
        try
        {
            var result = await service.MergeAsync(repository, number, cancellation);
            return Results.Json(result, statusCode: result.Outcome == "merged" ? 200 :
                result.Outcome == "uncertain" ? 202 : 409);
        }
        catch (Exception) { return Results.Conflict(new { reason = "merge_blocked_or_source_unavailable" }); }
    }
}
