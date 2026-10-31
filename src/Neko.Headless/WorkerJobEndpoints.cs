using Microsoft.AspNetCore.Http;

namespace Neko.Headless;

internal static class WorkerJobEndpoints
{
    private static string ManagerId(string name) => "manager/" + name;

    public static IResult List(WorkerJobService jobs, string name)
    {
        var result = jobs.List(ManagerId(name));
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    public static IResult Get(WorkerJobService jobs, string name, string jobId)
    {
        var result = jobs.Get(ManagerId(name), jobId);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    public static IResult Register(WorkerJobService jobs, string name,
        WorkerJobRegisterRequest request)
    {
        var (result, error) = jobs.Register(ManagerId(name), request);
        if (error == "manager_not_bound") return Results.NotFound();
        if (error == "invalid_job_binding") return Results.BadRequest(new { reason = error });
        if (error is not null) return Results.Conflict(new { reason = error });
        return Results.Json(result, statusCode: result!.Duplicate
            ? StatusCodes.Status200OK : StatusCodes.Status201Created);
    }

    public static IResult Interrupt(WorkerJobService jobs, string name,
        string jobId, WorkerJobInterruptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > 256 || request.Reason.Any(char.IsControl))
            return Results.BadRequest(new { reason = "invalid_interruption" });
        var result = jobs.Interrupt(ManagerId(name), jobId, request.Reason);
        return result is null ? Results.NotFound() : Results.Json(result);
    }
}
