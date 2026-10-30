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

    private sealed class ScopedProbe : IScopedConversationProvider
    {
        public int UnscopedCalls;
        public string? LastScope;
        public PreparedOutboundMessage? LastSubmission;
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
        public Task<SendReceipt> SendAsync(string id, string text, CancellationToken cancellation) =>
            Task.FromResult(new SendReceipt(SendDisposition.Accepted));
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
