namespace Neko;

/// <summary>
/// Optional, read-only authoritative turn verification. Implementations must
/// use fresh remote history AND independent terminal status, never cache or a
/// successful history read alone. No submission or retry is allowed here.
/// </summary>
public interface IAuthoritativeReconciliationProvider
{
    Task<ReconciliationInspection> InspectAsync(
        ReconciliationProbe probe, CancellationToken cancellation);
}

/// <param name="BaselineMessageIds">Ordered remote messages present before dispatch.</param>
/// <param name="AcceptedSubmission">True only after correlated remote acceptance was observed.</param>
/// <param name="CorrelatedCreation">True only for an ID returned by the original creation call.</param>
public sealed record ReconciliationProbe(
    string ConversationId, string PromptSha256,
    IReadOnlyList<string> BaselineMessageIds,
    bool AcceptedSubmission = false,
    bool CorrelatedCreation = false,
    string? CorrelatedUserMessageId = null);

public sealed record ReconciliationInspection(
    bool VerifiedComplete, string Reason,
    VisibleConversation? History = null,
    string? UserMessageId = null, string? AssistantMessageId = null);

/// <summary>Pure, content-free local outcome. A blocked result never releases a guard.</summary>
public sealed record ReconciliationResult(
    string Outcome, string Reason, string? ConversationId = null,
    string? UserMessageId = null, string? AssistantMessageId = null,
    int? RetryAfterSeconds = null);
