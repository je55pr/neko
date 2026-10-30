using Neko;

namespace Neko.Core.Tests;

public sealed class ScopedConversationProviderTests
{
    [Fact]
    public async Task CreationUsesConfiguredScopeAndNeverCallsUnscopedCreate()
    {
        var inner = new ScopedProbe();
        using var wrapped = new ScopedConversationProvider(inner, "private-scope");
        var coordinator = new ConversationCommandCoordinator(wrapped);

        var receipt = await coordinator.CreateAsync("hello");

        Assert.Equal(SendDisposition.Accepted, receipt.Disposition);
        Assert.Equal("private-scope", inner.LastScope);
        Assert.Equal("hello", inner.LastSubmission?.Text);
        Assert.NotNull(inner.LastSubmission?.ClientMessageId);
        Assert.Equal(0, inner.UnscopedCalls);
    }

    [Fact]
    public void RefusesMissingScopeOrUnsupportedAdapter()
    {
        Assert.Throws<ArgumentException>(() => new ScopedConversationProvider(
            new DisconnectedConversationProvider(), "required"));
        Assert.Throws<ArgumentException>(() => new ScopedConversationProvider(
            new ScopedProbe(), ""));
    }

    [Fact]
    public async Task InvalidCreationDoesNotCallAdapter()
    {
        var inner = new ScopedProbe();
        using var wrapped = new ScopedConversationProvider(inner, "required");
        var receipt = await wrapped.CreateAsync("  ", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, receipt.Disposition);
        Assert.Null(inner.LastScope);
    }

    [Fact]
    public async Task UnverifiedCreationIsUncertainAndCannotSendFollowup()
    {
        var inner = new ScopedProbe { Verify = false };
        using var wrapped = new ScopedConversationProvider(inner, "private-scope");
        var created = await wrapped.CreateAsync("hello", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, created.Disposition);
        Assert.Equal("scope_not_verified", created.Reason);
        Assert.Equal("scoped-conversation", created.ConversationId);

        var sent = await wrapped.SendAsync("scoped-conversation", "next", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, sent.Disposition);
        Assert.Equal("scope_not_verified", sent.Reason);
        Assert.Equal(0, inner.Sends);
    }

    [Fact]
    public async Task VerifiedCreationAllowsScopedFollowup()
    {
        var inner = new ScopedProbe();
        using var wrapped = new ScopedConversationProvider(inner, "private-scope");
        Assert.Equal(SendDisposition.Accepted,
            (await wrapped.CreateAsync("hello", CancellationToken.None)).Disposition);
        Assert.Equal(SendDisposition.Accepted,
            (await wrapped.SendAsync("scoped-conversation", "followup", CancellationToken.None)).Disposition);
        Assert.Equal(1, inner.Sends);
    }

    private sealed class ScopedProbe : IScopedConversationProvider
    {
        public int UnscopedCalls;
        public int Sends;
        public bool Verify = true;
        public string? LastScope;
        public PreparedOutboundMessage? LastSubmission;
        public Task<bool> BelongsToScopeAsync(string scope, string id, CancellationToken cancellation) =>
            Task.FromResult(Verify && scope == "private-scope" && id == "scoped-conversation");
        public Task<SendReceipt> CreateInScopeAsync(
            string scope, PreparedOutboundMessage submission, CancellationToken cancellation)
        {
            LastScope = scope;
            LastSubmission = submission;
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted,
                ConversationId: "scoped-conversation"));
        }
        public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation)
        {
            UnscopedCalls++;
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected, "unscoped"));
        }
        public Task<SendReceipt> SendAsync(string id, string text, CancellationToken cancellation)
        {
            Sends++;
            return Task.FromResult(new SendReceipt(SendDisposition.Accepted));
        }
        public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation) =>
            Task.FromResult<VisibleConversation?>(null);
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
