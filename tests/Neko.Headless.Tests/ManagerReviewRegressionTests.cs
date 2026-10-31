using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    [Theory]
    [InlineData("manager/a")]
    [InlineData("manager/0")]
    [InlineData("manager/abc-09_xyz")]
    [InlineData("manager/" + "x" + "------------------------------" + "_______________________________")]
    public void ManagerIdAcceptsAllowedCharacters(string id)
    {
        Assert.True(ManagerRegistry.IsValidId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("manager/")]
    [InlineData("manager/_a")]
    [InlineData("manager/-a")]
    [InlineData("Manager/main")]
    [InlineData("manager/Main")]
    [InlineData("manager/a b")]
    [InlineData("manager/a.b")]
    [InlineData("manager/a/b")]
    [InlineData("manager/a\nb")]
    [InlineData("manager/á")]
    [InlineData("manager/Ａ")]
    [InlineData("manager/a\0b")]
    [InlineData("manager/a\tb")]
    [InlineData("other/main")]
    public void ManagerIdRejectsInvalidCharacters(string? id)
    {
        Assert.False(ManagerRegistry.IsValidId(id));
    }

    [Fact]
    public void ManagerIdBoundaryLengthsAreStrictWithoutRegex()
    {
        Assert.True(ManagerRegistry.IsValidId("manager/" + new string('a', 1)));
        Assert.True(ManagerRegistry.IsValidId("manager/" + new string('a', 64)));
        Assert.False(ManagerRegistry.IsValidId("manager/" + new string('a', 65)));
        Assert.False(ManagerRegistry.IsValidId("manager/" + new string('a', 100_000)));
        Assert.False(ManagerRegistry.IsValidId(new string('a', 100_000)));
        Assert.False(ManagerRegistry.IsValidId("manager/" + new string('a', 63) + " "));
        Assert.False(ManagerRegistry.IsValidId("manager/" + new string('a', 63) + "é"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public async Task MissingOrBlankAcceptedConversationIdNeverReturnsAccepted(string id)
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { ManagerCreationConversationIdOverride = id };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var outcome = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, outcome.Receipt.Disposition);
        Assert.Equal("uncertain", outcome.Manager.LifecycleState);
        Assert.Null(outcome.Manager.ConversationId);
        Assert.Null(outcome.Manager.CheckpointPointer);
        Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Equal("manager_already_exists",
            (await managers.CreateAsync("manager/main", "again", CancellationToken.None))
            .Receipt.Reason);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task InvalidAcceptedConversationIdCannotBeBoundOrReportedAsSuccess()
    {
        using var folder = new TemporaryDirectory();
        var oversizedId = new string('a', OutboundMessagePreparation.MaxConversationIdLength + 1);
        var provider = new FakeProvider { ManagerCreationConversationIdOverride = oversizedId };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var outcome = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, outcome.Receipt.Disposition);
        Assert.Equal("conversation_id_unknown", outcome.Receipt.Reason);
        Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Null(runtime.Get(oversizedId));
        Assert.Equal("uncertain", outcome.Manager.LifecycleState);
        Assert.Null(outcome.Manager.ConversationId);
        Assert.Null(outcome.Manager.CheckpointPointer);
        Assert.Equal(1, provider.ManagerCreationCalls);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        Assert.Equal("manager_already_exists",
            (await recovered.CreateAsync("manager/main", "again", CancellationToken.None))
            .Receipt.Reason);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Theory]
    [InlineData("remote\ninvalid")]
    [InlineData("remote\0invalid")]
    [InlineData("remote\tinvalid")]
    public async Task ControlCharactersInAcceptedConversationIdRemainUncertain(string id)
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { ManagerCreationConversationIdOverride = id };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var outcome = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.Equal(SendDisposition.Uncertain, outcome.Receipt.Disposition);
        Assert.Equal("conversation_id_unknown", outcome.Receipt.Reason);
        Assert.Null(outcome.Manager.ConversationId);
        Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Null(runtime.Get(id));
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task MaximumLengthConversationIdCanBeSafelyBound()
    {
        using var folder = new TemporaryDirectory();
        var maxId = new string('a', OutboundMessagePreparation.MaxConversationIdLength);
        var provider = new FakeProvider { ManagerCreationConversationIdOverride = maxId };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var outcome = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, outcome.Receipt.Disposition);
        Assert.Equal(maxId, outcome.Manager.ConversationId);
        Assert.Equal(ManagerRegistry.CheckpointFor(maxId), outcome.Manager.CheckpointPointer);
        Assert.False(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        await WaitForTerminal(runtime, maxId);
        Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
    }

    [Fact]
    public async Task LegacyTransientSendingDoesNotLatchManagerRegistry()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        var registry = new ManagerRegistry(folder.Path);
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(registry, runtime);
        await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");

        provider.PendingSend = new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var legacySend = runtime.SendAsync("chat-1", "legacy text", CancellationToken.None);
        Assert.False(legacySend.IsCompleted);
        Assert.Equal("sending", runtime.Get("chat-1")!.Status);

        Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
        Assert.Equal("idle", registry.Get("manager/main")!.LifecycleState);
        provider.PendingSend.SetResult(new SendReceipt(SendDisposition.Rejected, "test_reject"));
        Assert.Equal(SendDisposition.Rejected, (await legacySend).Disposition);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
        provider.PendingSend = null;

        var subsequent = await managers.SendAsync("manager/main", "real follow-up",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, subsequent!.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
        Assert.Equal(2, provider.SendCalls); // one legacy plus one manager send
    }

    [Fact]
    public void RefreshLifecycleStillPropagatesUncertain()
    {
        using var folder = new TemporaryDirectory();
        var registry = new ManagerRegistry(folder.Path);
        registry.TryReserve("manager/main", out _);
        registry.Update("manager/main", record => record with {
            ConversationId = "chat-1",
            CheckpointPointer = ManagerRegistry.CheckpointFor("chat-1"),
            LifecycleState = "idle"
        });

        Assert.Equal("idle",
            registry.RefreshLifecycle("manager/main", "idle", "sending").LifecycleState);
        Assert.Equal("uncertain",
            registry.RefreshLifecycle("manager/main", "idle", "uncertain").LifecycleState);
        Assert.Equal("uncertain", new ManagerRegistry(folder.Path)
            .Get("manager/main")!.LifecycleState);
    }

    [Fact]
    public async Task PreCancelledCreationNeverReservesOrDispatches()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            managers.CreateAsync("manager/main", "first", cancel.Token));
        Assert.Null(managers.Get("manager/main"));
        Assert.Equal(0, provider.ManagerCreationCalls);

        var explicitRetry = await managers.CreateAsync("manager/main", "first",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, explicitRetry.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task CancellationWhileWaitingCreationGateIsProvenNotDispatched()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            PendingCreate = new TaskCompletionSource<SendReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            DeferCompletion = true
        };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var legacyCreate = runtime.CreateAsync("legacy first", CancellationToken.None);
        Assert.False(legacyCreate.IsCompleted);
        Assert.Equal(1, provider.ManagerCreationCalls);

        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        using var cancelled = new CancellationTokenSource();
        var attempt = managers.CreateAsync("manager/main", "waiting", cancelled.Token);
        Assert.False(attempt.IsCompleted);
        cancelled.Cancel();

        var result = await attempt;
        Assert.Equal(SendDisposition.Rejected, result.Receipt.Disposition);
        Assert.Equal("request_cancelled", result.Receipt.Reason);
        Assert.True(result.Receipt.Retryable);
        Assert.Equal("rejected", result.Manager.LifecycleState);
        Assert.Null(result.Manager.ConversationId);
        Assert.Equal(1, provider.ManagerCreationCalls);

        provider.PendingCreate.SetResult(new SendReceipt(SendDisposition.Rejected, "busy"));
        Assert.Equal(SendDisposition.Rejected, (await legacyCreate).Disposition);
        provider.PendingCreate = null;

        var retry = await managers.CreateAsync("manager/main", "explicit retry", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, retry.Receipt.Disposition);
        Assert.Equal(2, provider.ManagerCreationCalls);
        provider.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
    }

    [Fact]
    public async Task CancellationAfterCreationDispatchRemainsUncertain()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider {
            PendingCreate = new TaskCompletionSource<SendReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        using var cancelled = new CancellationTokenSource();
        var attempt = managers.CreateAsync("manager/main", "first", cancelled.Token);
        Assert.False(attempt.IsCompleted);
        Assert.Equal(1, provider.ManagerCreationCalls);
        cancelled.Cancel();

        var result = await attempt;
        Assert.Equal(SendDisposition.Uncertain, result.Receipt.Disposition);
        Assert.Equal("uncertain", result.Manager.LifecycleState);
        Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Equal("manager_already_exists",
            (await managers.CreateAsync("manager/main", "not again", CancellationToken.None))
            .Receipt.Reason);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task CancellationAfterCreationReceiptDuringJournalFailureIsNotSafe()
    {
        using var folder = new TemporaryDirectory();
        using var cancelled = new CancellationTokenSource();
        var journal = new ChatJournal(folder.Path, record => {
            if (record.Status == "generating")
            {
                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            }
        });
        var provider = new FakeProvider();
        using var runtime = new ChatRuntime(
            new ScopedConversationProvider(provider, "scope-a"), journal);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var result = await managers.CreateAsync("manager/main", "first", cancelled.Token);
        Assert.Equal(SendDisposition.Uncertain, result.Receipt.Disposition);
        Assert.Equal("uncertain", result.Manager.LifecycleState);
        Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Equal("manager_already_exists",
            (await managers.CreateAsync("manager/main", "again", CancellationToken.None))
            .Receipt.Reason);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task PreCancelledManagerSendDoesNotSetGuardAndCanRetryExplicitly()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            managers.SendAsync("manager/main", "never dispatched", cancelled.Token));
        Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
        Assert.Equal(0, provider.SendCalls);

        var retry = await managers.SendAsync("manager/main", "explicit retry",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, retry!.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task CancellationDuringHistoryPreflightIsNotUncertain()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");

        provider.PendingRead = new TaskCompletionSource<VisibleConversation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var attempt = managers.SendAsync("manager/main", "waiting for history", cancelled.Token);
        Assert.False(attempt.IsCompleted);
        Assert.Equal("sending", managers.Get("manager/main")!.LifecycleState);
        Assert.Equal("idle", runtime.Get("chat-1")!.Status);
        Assert.Equal(0, provider.SendCalls);

        cancelled.Cancel();
        var result = await attempt;
        Assert.Equal(SendDisposition.Rejected, result!.Receipt.Disposition);
        Assert.Equal("request_cancelled", result.Receipt.Reason);
        Assert.True(result.Receipt.Retryable);
        Assert.Equal("idle", result.Manager.LifecycleState);
        Assert.Equal(0, provider.SendCalls);
        provider.PendingRead = null;

        var retry = await managers.SendAsync("manager/main", "explicit retry",
            CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, retry!.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task CancellationAfterSendDispatchRemainsUncertainAcrossRestart()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            await WaitForTerminal(runtime, "chat-1");
            provider.PendingSend = new TaskCompletionSource<SendReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = new CancellationTokenSource();
            var attempt = managers.SendAsync("manager/main", "maybe sent", cancelled.Token);
            Assert.False(attempt.IsCompleted);
            Assert.Equal(1, provider.SendCalls);
            Assert.Equal("sending", runtime.Get("chat-1")!.Status);
            cancelled.Cancel();

            var result = await attempt;
            Assert.Equal(SendDisposition.Uncertain, result!.Receipt.Disposition);
            Assert.Equal("uncertain", result.Manager.LifecycleState);
        }

        using var restarted = ManagerRuntime(folder.Path, provider);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), restarted);
        Assert.Equal("uncertain", recovered.Get("manager/main")!.LifecycleState);
        var blocked = await recovered.SendAsync("manager/main", "never replay", CancellationToken.None);
        Assert.Equal("manager_not_idle", blocked!.Receipt.Reason);
        Assert.Equal(1, provider.SendCalls);
    }
}
