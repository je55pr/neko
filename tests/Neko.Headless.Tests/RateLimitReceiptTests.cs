using System.Runtime.CompilerServices;
using System.Text.Json;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class RateLimitReceiptTests
{
    [Fact]
    public async Task Upstream429DuringHistoryPreflightRejectsWithoutSendingAndAllowsLaterGuardedAttempt()
    {
        using var folder = new TempFolder();
        var provider = new FakeScopedProvider
        {
            HistoryUnavailable = true,
            RateLimitRetryAfterSeconds = 90
        };
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "idle"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);

        // The fake models a history GET returning HTTP 429 with Retry-After: 90.
        var rejected = await runtime.SendAsync("chat-1", "safe later", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        Assert.True(rejected.Retryable);
        Assert.Equal(90, rejected.RetryAfterSeconds);
        Assert.Equal(1, provider.HistoryReads);
        Assert.Equal(0, provider.ScopeChecks);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal("idle", runtime.Get("chat-1")?.Status);
        Assert.Equal(1, provider.HistoryReads); // No automatic cooldown poll.

        provider.HistoryUnavailable = false;
        provider.RateLimitRetryAfterSeconds = null;
        var later = await runtime.SendAsync("chat-1", "safe later", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, later.Disposition);
        Assert.Null(later.Retryable);
        Assert.Null(later.RetryAfterSeconds);
        Assert.Equal(2, provider.HistoryReads);
        Assert.Equal(1, provider.ScopeChecks);
        Assert.Equal(1, provider.Submissions);
    }

    [Fact]
    public async Task UnknownReadFailureDoesNotInventARetryDelay()
    {
        using var folder = new TempFolder();
        var provider = new FakeScopedProvider { HistoryUnavailable = true };
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "idle"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);

        var rejected = await runtime.SendAsync("chat-1", "later", CancellationToken.None);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        Assert.True(rejected.Retryable);
        Assert.Null(rejected.RetryAfterSeconds);
        Assert.Equal(0, provider.Submissions);
    }

    [Fact]
    public async Task ScopePreflight429ReportsCooldownWithoutInvokingSubmit()
    {
        using var folder = new TempFolder();
        var provider = new FakeScopedProvider
        {
            RejectScope429 = true,
            RateLimitRetryAfterSeconds = 60
        };
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "idle"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);

        var rejected = await runtime.SendAsync("chat-1", "not sent", CancellationToken.None);
        Assert.Equal("scope_not_verified", rejected.Reason);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.True(rejected.Retryable);
        Assert.Equal(60, rejected.RetryAfterSeconds);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal("idle", runtime.Get("chat-1")?.Status);
        provider.RejectScope429 = false;
        provider.RateLimitRetryAfterSeconds = null;
        var later = await runtime.SendAsync("chat-1", "explicit follow-up", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, later.Disposition);
        Assert.Equal(1, provider.Submissions);
    }

    [Fact]
    public async Task Confirmed429CreationDoesNotLatchAndNeverAutomaticallyRecreates()
    {
        using var folder = new TempFolder();
        var provider = new FakeScopedProvider
        {
            RejectCreation429 = true,
            RateLimitRetryAfterSeconds = 140
        };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"),
            new ChatJournal(folder.Path));

        var rejected = await runtime.CreateAsync("first", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.Equal("provider_rate_limited", rejected.Reason);
        Assert.True(rejected.Retryable);
        Assert.Equal(140, rejected.RetryAfterSeconds);
        Assert.Equal(1, provider.Creations);

        provider.RejectCreation429 = false;
        provider.RateLimitRetryAfterSeconds = null;
        Assert.Equal(1, provider.Creations); // Only the caller can try again.
        var accepted = await runtime.CreateAsync("first", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, accepted.Disposition);
        Assert.Equal("chat-1", accepted.ConversationId);
        Assert.Null(accepted.Retryable);
        Assert.Equal(2, provider.Creations);
    }

    [Fact]
    public async Task Explicit429SubmissionRejectionIsSafeButUncertainSubmissionIsNeverReplayable()
    {
        using var folder = new TempFolder();
        var provider = new FakeScopedProvider
        {
            RejectSubmission429 = true,
            RateLimitRetryAfterSeconds = 60
        };
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "idle"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);

        var rejected = await runtime.SendAsync("chat-1", "confirmed failure", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.True(rejected.Retryable);
        Assert.Equal(60, rejected.RetryAfterSeconds);
        Assert.Equal("idle", runtime.Get("chat-1")?.Status);
        Assert.Equal(1, provider.Submissions);

        provider.RejectSubmission429 = false;
        provider.RateLimitRetryAfterSeconds = null;
        provider.UncertainSubmission = true;
        var uncertain = await runtime.SendAsync("chat-1", "possibly delivered", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, uncertain.Disposition);
        Assert.Null(uncertain.Retryable);
        Assert.Null(uncertain.RetryAfterSeconds);
        Assert.Equal("uncertain", runtime.Get("chat-1")?.Status);
        Assert.Equal(2, provider.Submissions);
        var blocked = await runtime.SendAsync("chat-1", "do not duplicate", CancellationToken.None);
        Assert.Equal("conversation_not_idle", blocked.Reason);
        Assert.NotEqual(true, blocked.Retryable);
        Assert.Equal(2, provider.Submissions);
    }

    [Fact]
    public void ApiReceiptFieldsAreAdditiveAndLegacyConstructorRemainsValid()
    {
        var legacy = new SendReceipt(SendDisposition.Uncertain, "delivery_unknown");
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"disposition\"", json);
        Assert.Contains("\"retryable\":null", json);
        Assert.Contains("\"retryAfterSeconds\":null", json);
        var rejected = new SendReceipt(SendDisposition.Rejected, "provider_rate_limited")
        { Retryable = true, RetryAfterSeconds = 100 };
        var withHint = JsonSerializer.Serialize(rejected,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"retryable\":true", withHint);
        Assert.Contains("\"retryAfterSeconds\":100", withHint);
    }

    private sealed class FakeScopedProvider : IScopedConversationProvider, IProviderRateLimitStatus
    {
        public int? RateLimitRetryAfterSeconds { get; set; }
        public bool HistoryUnavailable { get; set; }
        public bool RejectCreation429 { get; set; }
        public bool RejectScope429 { get; set; }
        public bool RejectSubmission429 { get; set; }
        public bool UncertainSubmission { get; set; }
        public int HistoryReads { get; private set; }
        public int ScopeChecks { get; private set; }
        public int Submissions { get; private set; }
        public int Creations { get; private set; }

        public Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            HistoryReads++;
            return Task.FromResult<VisibleConversation?>(HistoryUnavailable ? null :
                new VisibleConversation(id, "Test", []));
        }

        public Task<bool> BelongsToScopeAsync(string scope, string id,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ScopeChecks++;
            return Task.FromResult(!RejectScope429 && scope == "scope-a" && id == "chat-1");
        }

        public Task<SendReceipt> CreateInScopeAsync(string scope,
            PreparedOutboundMessage submission, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.Equal("scope-a", scope);
            Assert.True(submission.CreatesConversation);
            Creations++;
            return Task.FromResult(RejectCreation429
                ? new SendReceipt(SendDisposition.Rejected, "provider_rate_limited")
                  { Retryable = true, RetryAfterSeconds = RateLimitRetryAfterSeconds }
                : new SendReceipt(SendDisposition.Accepted, ConversationId: "chat-1"));
        }

        public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            throw new InvalidOperationException("Unscoped create must never be called.");
        }

        public Task<SendReceipt> SendAsync(string id, string text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.Equal("chat-1", id);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Submissions++;
            return Task.FromResult(RejectSubmission429
                ? new SendReceipt(SendDisposition.Rejected, "http_rejected")
                  { Retryable = true, RetryAfterSeconds = RateLimitRetryAfterSeconds }
                : UncertainSubmission
                    ? new SendReceipt(SendDisposition.Uncertain, "delivery_unknown")
                    : new SendReceipt(SendDisposition.Accepted));
        }

        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        }

        public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            return Task.FromResult(new StopReceipt(StopDisposition.Unsupported));
        }

        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(string id,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new PublicConversationUpdate(id, PublicUpdateKind.Final,
                PublicUpdateOperation.Complete);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "neko-rate-tests-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
