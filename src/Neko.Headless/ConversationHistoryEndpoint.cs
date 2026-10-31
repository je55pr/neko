using Microsoft.AspNetCore.Http;
using Neko;

namespace Neko.Headless;

internal readonly record struct HistoryReadResult(
    bool Known, VisibleConversation? History, bool ProviderVerified = false);

internal static class ConversationHistoryEndpoint
{
    public static async Task<IResult> ReadAsync(
        ChatRuntime runtime, string id, CancellationToken cancellation)
    {
        var read = await runtime.ReadAsync(id, cancellation);
        if (!read.Known) return Results.NotFound();
        if (read.History is null)
            return Results.Json(new
            {
                status = "pending",
                reason = "provider_history_unavailable",
                conversationId = id
            }, statusCode: StatusCodes.Status202Accepted);
        return Results.Json(read.History);
    }
}
