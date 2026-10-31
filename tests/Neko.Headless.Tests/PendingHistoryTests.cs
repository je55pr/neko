using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    // Controls for the pending-history scenarios live alongside the tests that assign them.
    private sealed partial class FakeProvider
    {
        public bool ReturnNullHistory { get; set; }
        public bool ThrowOnRead { get; set; }
        public TaskCompletionSource<VisibleConversation?>? PendingRead { get; set; }
    }

    [Fact]
    public async Task GeneratingConversationWithUnavailableHistoryReturns202ThenAuthoritative200()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider { DeferCompletion = true, ReturnNullHistory = true };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path));
        var receipt = await runtime.CreateAsync("first", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, receipt.Disposition);
        var before = runtime.Get("chat-1")!;
        Assert.Equal("generating", before.Status);
        Assert.Null(before.History);

        var (pendingStatus, pendingBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status202Accepted, pendingStatus);
        using (var json = JsonDocument.Parse(pendingBody))
        {
            Assert.Equal("pending", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("provider_history_unavailable", json.RootElement.GetProperty("reason").GetString());
            Assert.Equal("chat-1", json.RootElement.GetProperty("conversationId").GetString());
            Assert.False(json.RootElement.TryGetProperty("messages", out _));
        }
        inner.ReturnNullHistory = false;
        inner.ThrowOnRead = true;
        var (unavailableStatus, unavailableBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status202Accepted, unavailableStatus);
        Assert.Contains("provider_history_unavailable", unavailableBody);
        Assert.Equal(before.UpdatedAt, runtime.Get("chat-1")!.UpdatedAt);
        Assert.Equal("generating", runtime.Get("chat-1")!.Status);

        inner.ThrowOnRead = false;
        inner.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        var (completedStatus, completedBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status200OK, completedStatus);
        using var complete = JsonDocument.Parse(completedBody);
        Assert.Equal("chat-1", complete.RootElement.GetProperty("id").GetString());
        Assert.Equal(2, complete.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
    }

    [Fact]
    public async Task RateLimitedHistoryReadKeepsKnownPendingAndUnknownMissingWithoutReplay()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider
        {
            ReturnNullHistory = true,
            RateLimitRetryAfterSeconds = 75
        };
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("chat-1", "idle"));
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), journal);

        var (pendingStatus, pendingBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status202Accepted, pendingStatus);
        using (var pending = JsonDocument.Parse(pendingBody))
        {
            Assert.Equal("pending", pending.RootElement.GetProperty("status").GetString());
            Assert.Equal("provider_history_unavailable",
                pending.RootElement.GetProperty("reason").GetString());
            Assert.False(pending.RootElement.TryGetProperty("messages", out _));
        }
        var (missingStatus, _) = await RequestHistoryAsync(runtime, "missing");
        Assert.Equal(StatusCodes.Status404NotFound, missingStatus);
        Assert.Equal(1, inner.ReadCalls);
        Assert.Equal(0, inner.SendCalls);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);

        var rejected = await runtime.SendAsync("chat-1", "blocked by history", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Disposition);
        Assert.Equal("provider_history_unavailable", rejected.Reason);
        Assert.True(rejected.Retryable);
        Assert.Equal(75, rejected.RetryAfterSeconds);
        Assert.Equal(0, inner.SendCalls);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);

        inner.ReturnNullHistory = false;
        inner.RateLimitRetryAfterSeconds = null;
        inner.InjectMessage("remote transcript restored");
        var (restoredStatus, restoredBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status200OK, restoredStatus);
        using var restored = JsonDocument.Parse(restoredBody);
        Assert.Equal("remote transcript restored",
            restored.RootElement.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal(0, inner.SendCalls);
    }

    [Fact]
    public async Task UnknownConversationReturns404WithoutProviderRead()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path));

        var (status, _) = await RequestHistoryAsync(runtime, "missing");
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(0, inner.ReadCalls);
        Assert.Null(runtime.Get("missing"));
    }

    [Fact]
    public async Task UnavailableProviderReadReturnsSavedHistoryAndLaterRefreshes()
    {
        using var folder = new TemporaryDirectory();
        var inner = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), new ChatJournal(folder.Path));
        await runtime.CreateAsync("first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");

        inner.ReturnNullHistory = true;
        var (cachedStatus, cachedBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status200OK, cachedStatus);
        using (var cached = JsonDocument.Parse(cachedBody))
            Assert.Equal(2, cached.RootElement.GetProperty("messages").GetArrayLength());

        inner.InjectMessage("new authoritative message");
        inner.ReturnNullHistory = false;
        var (freshStatus, freshBody) = await RequestHistoryAsync(runtime, "chat-1");
        Assert.Equal(StatusCodes.Status200OK, freshStatus);
        using var fresh = JsonDocument.Parse(freshBody);
        Assert.Equal(3, fresh.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Contains(fresh.RootElement.GetProperty("messages").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "new authoritative message");
    }

    [Fact]
    public async Task ConcurrentCheckpointIsNotLostWhenRemoteReadReturnsNull()
    {
        using var folder = new TemporaryDirectory();
        var journal = new ChatJournal(folder.Path);
        var inner = new FakeProvider { DeferCompletion = true };
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(inner, "scope-a"), journal);
        await runtime.CreateAsync("first", CancellationToken.None);
        var pendingRead = new TaskCompletionSource<VisibleConversation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        inner.PendingRead = pendingRead;

        var request = RequestHistoryAsync(runtime, "chat-1");
        journal.Update("chat-1", current => current with
        {
            History = new VisibleConversation("chat-1", "Checkpoint",
                [new VisibleConversationMessage("saved", VisibleMessageRole.Assistant, "saved text")])
        });
        pendingRead.SetResult(null);
        var (status, body) = await request;
        Assert.Equal(StatusCodes.Status200OK, status);
        using var response = JsonDocument.Parse(body);
        Assert.Equal("saved text", response.RootElement.GetProperty("messages")[0]
            .GetProperty("text").GetString());
        Assert.Equal("generating", runtime.Get("chat-1")!.Status);
    }

    [Fact]
    public void CliExplainsPendingAndUnknownReadWithoutGenericFailure()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        const string pendingJson = "{\"status\":\"pending\",\"reason\":\"provider_history_unavailable\"}";

        Assert.True(LocalClient.TryWriteReadStatus(
            HttpStatusCode.Accepted, pendingJson, output, errors, out var pendingExit));
        Assert.Equal(0, pendingExit);
        Assert.Contains("provider_history_unavailable", output.ToString());
        Assert.Contains("History pending", errors.ToString());

        output.GetStringBuilder().Clear();
        errors.GetStringBuilder().Clear();
        Assert.True(LocalClient.TryWriteReadStatus(
            HttpStatusCode.NotFound, "", output, errors, out var notFoundExit));
        Assert.Equal(1, notFoundExit);
        Assert.Empty(output.ToString());
        Assert.Contains("Conversation not found", errors.ToString());
        Assert.False(LocalClient.TryWriteReadStatus(
            HttpStatusCode.OK, "{}", output, errors, out var normalExit));
        Assert.Equal(0, normalExit);
    }

    private static async Task<(int Status, string Body)> RequestHistoryAsync(
        ChatRuntime runtime, string id)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        await using var stream = new MemoryStream();
        context.Response.Body = stream;
        var result = await ConversationHistoryEndpoint.ReadAsync(
            runtime, id, CancellationToken.None);
        await result.ExecuteAsync(context);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

}
