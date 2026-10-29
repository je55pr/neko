namespace Neko;

/// <summary>
/// Immutable, provider-neutral text submission prepared at the user-action
/// boundary. ClientMessageId identifies one attempted submission, not an
/// authoritative provider receipt or a guarantee of idempotency.
/// </summary>
public sealed record PreparedOutboundMessage(
    string ClientMessageId,
    string? ConversationId,
    string Text,
    DateTimeOffset PreparedAtUtc)
{
    public bool CreatesConversation=>ConversationId is null;
}

public sealed record MessagePreparationResult(
    PreparedOutboundMessage? Message,string ErrorCode="")
{
    public bool IsReady=>Message is not null;
}

/// <summary>
/// Prepares the exact user text without changing whitespace or line endings.
/// Attachment-only or multimodal messages need a separate explicit contract;
/// do not silently discard them or convert them to text-only submissions.
/// </summary>
public static class OutboundMessagePreparation
{
    // Runtime properties avoid compiling policy limits into consumers.
    public static int MaxTextLength {get;}=24_000;
    public static int MaxConversationIdLength {get;}=256;

    public static MessagePreparationResult ForExisting(
        string? conversationId,string? text)
    {
        if(string.IsNullOrWhiteSpace(conversationId) ||
           conversationId.Length>MaxConversationIdLength)
            return new(null,"invalid_conversation");
        return Prepare(conversationId,text);
    }

    public static MessagePreparationResult ForNew(string? text)=>
        Prepare(null,text);

    public static bool IsValid(PreparedOutboundMessage? submission)
    {
        if(submission is null ||
           !Guid.TryParseExact(submission.ClientMessageId,"D",out _) ||
           submission.PreparedAtUtc==default ||
           submission.PreparedAtUtc.Offset!=TimeSpan.Zero)
            return false;
        if(submission.ConversationId is { } id &&
           (string.IsNullOrWhiteSpace(id) || id.Length>MaxConversationIdLength))
            return false;
        return IsValidText(submission.Text);
    }

    private static MessagePreparationResult Prepare(string? conversationId,string? text)
    {
        if(!IsValidText(text))
            return new(null,"invalid_submission");
        return new(new PreparedOutboundMessage(
            Guid.NewGuid().ToString("D"),
            conversationId,
            text!,
            DateTimeOffset.UtcNow));
    }

    private static bool IsValidText(string? text)=>
        !string.IsNullOrWhiteSpace(text) && text.Length<=MaxTextLength &&
        !text.Contains('\0') && HasValidUtf16(text);

    private static bool HasValidUtf16(string text)
    {
        var index=0;
        while(index<text.Length)
        {
            var character=text[index];
            if(char.IsHighSurrogate(character))
            {
                if(index+1>=text.Length || !char.IsLowSurrogate(text[index+1]))
                    return false;
                index+=2;
                continue;
            }
            if(char.IsLowSurrogate(character))
                return false;
            index++;
        }
        return true;
    }
}

/// <summary>
/// Optional capability for providers that can consume prepared messages
/// directly. Legacy text-only adapters remain supported by IConversationProvider.
/// An adapter must never assume a client ID is a server acknowledgement.
/// </summary>
public interface IPreparedConversationProvider
{
    Task<SendReceipt> SendPreparedAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation);
    Task<SendReceipt> CreatePreparedAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation);
}
