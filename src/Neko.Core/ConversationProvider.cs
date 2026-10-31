using System.Runtime.CompilerServices;

namespace Neko;

/// <summary>
/// A transport-neutral contract. Adapters must expose only user-visible content.
/// Authentication and provider-specific serialization live outside this assembly.
/// </summary>
public interface IConversationProvider
{
    Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation);
    Task<VisibleConversation?> ReadAsync(string conversationId,CancellationToken cancellation);
    Task<SendReceipt> CreateAsync(string firstMessage,CancellationToken cancellation);
    Task<SendReceipt> SendAsync(string conversationId,string text,
        CancellationToken cancellation);
    Task<StopReceipt> StopAsync(string conversationId,CancellationToken cancellation);
    // Remote Stop is opt-in: unsupported providers must not dispatch or change
    // session/journal state, including when implementing adapters in the future.
    bool SupportsStop => false;
    IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string conversationId,CancellationToken cancellation);
}

public sealed record ConversationSummary(string Id,string Title);

public enum VisibleMessageRole { User, Assistant, Activity }

public sealed record VisibleConversationMessage(
    string Id,VisibleMessageRole Role,string Text,string Kind="message",
    DateTimeOffset? Timestamp=null);

public sealed record VisibleConversation(
    string Id,string Title,IReadOnlyList<VisibleConversationMessage> Messages);

public enum SendDisposition { Rejected, Accepted, Uncertain }

public sealed record SendReceipt(
    SendDisposition Disposition,string Reason="",string? ConversationId=null,
    string? MessageId=null)
{
    // Only true when the operation was definitively rejected before acceptance
    // and a later explicit attempt is safe. Null means no retry claim is made;
    // an Uncertain/Accepted receipt must never be automatically replayed.
    public bool? Retryable { get; init; }
    // Remaining local cooldown, if known. Null does not mean zero seconds.
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>
/// Optional, local-only rate-limit observation. Reading it must not issue
/// network requests; a null value means no active known provider cooldown.
/// </summary>
public interface IProviderRateLimitStatus
{
    int? RateLimitRetryAfterSeconds { get; }
}

public enum StopDisposition { NotActive, Requested, Uncertain, Unsupported }

public sealed record StopReceipt(StopDisposition Disposition,string Reason="");

public enum PublicUpdateKind { Final, Progress, Activity }
public enum PublicUpdateOperation { Start, Replace, Append, Complete, Fail }

public sealed record PublicConversationUpdate(
    string MessageId,PublicUpdateKind Kind,PublicUpdateOperation Operation,
    string Text="",int PartIndex=0,string Title="");

/// <summary>
/// Safe default for the public desktop shell. Does not perform network IO.
/// </summary>
public sealed class DisconnectedConversationProvider : IConversationProvider
{
    private const string Offline="No conversation provider is connected.";

    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
    }

    public Task<VisibleConversation?> ReadAsync(
        string conversationId,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<VisibleConversation?>(null);
    }

    public Task<SendReceipt> CreateAsync(string firstMessage,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new SendReceipt(SendDisposition.Rejected,Offline));
    }

    public Task<SendReceipt> SendAsync(
        string conversationId,string text,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new SendReceipt(SendDisposition.Rejected,Offline));
    }

    public Task<StopReceipt> StopAsync(string conversationId,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new StopReceipt(StopDisposition.NotActive,Offline));
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string conversationId,[EnumeratorCancellation]CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
