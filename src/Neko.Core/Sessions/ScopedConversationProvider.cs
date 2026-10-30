namespace Neko;

/// <summary>
/// A provider may offer creation in a specific, provider-defined scope.
/// The scope is supplied at runtime and must never be inferred or ignored.
/// </summary>
public interface IScopedConversationProvider : IConversationProvider
{
    Task<SendReceipt> CreateInScopeAsync(
        string scope, PreparedOutboundMessage submission, CancellationToken cancellation);

    /// <summary>
    /// Must use authoritative provider state, not the requested scope or a local
    /// cache. False/unknown prevents follow-up sends. Never guess membership.
    /// </summary>
    Task<bool> BelongsToScopeAsync(
        string scope, string conversationId, CancellationToken cancellation);
}

/// <summary>
/// Used by external adapters loaded from a private, locally configured assembly.
/// </summary>
public interface IConversationProviderFactory
{
    IConversationProvider Create(string settingsPath);
}

/// <summary>
/// Fail-closed adapter: all conversation creation passes through the scoped contract.
/// Other operations delegate to the original provider without changing transport data.
/// </summary>
public sealed class ScopedConversationProvider :
    IConversationProvider, IPreparedConversationProvider, IDisposable
{
    private readonly IScopedConversationProvider _provider;
    private readonly string _scope;

    public ScopedConversationProvider(IConversationProvider provider, string scope)
    {
        _provider = provider as IScopedConversationProvider
            ?? throw new ArgumentException("Provider does not support scoped creation.", nameof(provider));
        _scope = !string.IsNullOrWhiteSpace(scope) ? scope
            : throw new ArgumentException("A creation scope is required.", nameof(scope));
    }

    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
        _provider.BrowseAsync(cancellation);
    public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation) =>
        _provider.ReadAsync(id, cancellation);
    public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
        _provider.StopAsync(id, cancellation);
    public IAsyncEnumerable<PublicConversationUpdate> WatchAsync(string id, CancellationToken cancellation) =>
        _provider.WatchAsync(id, cancellation);

    public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation)
    {
        var prepared = OutboundMessagePreparation.ForNew(text);
        return prepared.Message is null
            ? Task.FromResult(new SendReceipt(SendDisposition.Rejected, prepared.ErrorCode))
            : CreatePreparedAsync(prepared.Message, cancellation);
    }

    public async Task<SendReceipt> CreatePreparedAsync(
        PreparedOutboundMessage submission, CancellationToken cancellation)
    {
        if (!OutboundMessagePreparation.IsValid(submission) || !submission.CreatesConversation)
            return new SendReceipt(SendDisposition.Rejected, "invalid_submission");

        var receipt = await _provider.CreateInScopeAsync(_scope, submission, cancellation);
        if (receipt.Disposition != SendDisposition.Accepted) return receipt;
        if (string.IsNullOrWhiteSpace(receipt.ConversationId))
            return new SendReceipt(SendDisposition.Uncertain, "conversation_id_unknown");

        try
        {
            return await VerifyAsync(receipt.ConversationId, cancellation)
                ? receipt : new SendReceipt(SendDisposition.Uncertain, "scope_not_verified",
                    ConversationId: receipt.ConversationId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Creation already happened. Cancellation cannot prove that the
            // resulting conversation was rejected or that it is safe to retry.
            return new SendReceipt(SendDisposition.Uncertain,
                "scope_verification_cancelled",
                ConversationId: receipt.ConversationId);
        }
    }

    public Task<SendReceipt> SendAsync(string conversationId, string text, CancellationToken cancellation)
    {
        var prepared = OutboundMessagePreparation.ForExisting(conversationId, text);
        return prepared.Message is null
            ? Task.FromResult(new SendReceipt(SendDisposition.Rejected, prepared.ErrorCode))
            : SendPreparedAsync(prepared.Message, cancellation);
    }

    public async Task<SendReceipt> SendPreparedAsync(
        PreparedOutboundMessage submission, CancellationToken cancellation)
    {
        if (!OutboundMessagePreparation.IsValid(submission) || submission.ConversationId is null)
            return new SendReceipt(SendDisposition.Rejected, "invalid_submission");
        bool scoped;
        try { scoped = await VerifyAsync(submission.ConversationId, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The provider has not been invoked. This is a rejected preflight,
            // not an uncertain delivery requiring a permanent retry lock.
            return new SendReceipt(SendDisposition.Rejected,
                "scope_verification_cancelled_before_send");
        }
        if (!scoped)
            return new SendReceipt(SendDisposition.Rejected, "scope_not_verified");
        return _provider is IPreparedConversationProvider prepared
            ? await prepared.SendPreparedAsync(submission, cancellation)
            : await _provider.SendAsync(submission.ConversationId, submission.Text, cancellation);
    }

    private async Task<bool> VerifyAsync(string id, CancellationToken cancellation)
    {
        try { return await _provider.BelongsToScopeAsync(_scope, id, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_provider is IDisposable disposable) disposable.Dispose();
    }
}
