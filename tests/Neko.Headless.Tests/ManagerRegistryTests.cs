using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neko;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed partial class HeadlessRuntimeTests
{
    private sealed partial class FakeProvider
    {
        public bool RejectNextManagerCreation { get; set; }
        public string? ManagerCreationConversationIdOverride { get; set; }
    }

    [Fact]
    public async Task ManagerIdentityAndCheckpointSurviveRestartAndFollowUp()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        ManagerSnapshot initial;
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            var creation = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            Assert.Equal(SendDisposition.Accepted, creation.Receipt.Disposition);
            Assert.Equal("chat-1", creation.Manager.ConversationId);
            await WaitForTerminal(runtime, "chat-1");
            initial = managers.Get("manager/main")!;
            Assert.Equal("idle", initial.LifecycleState);
            Assert.Equal("manager/main", initial.Id);
            Assert.Equal(ManagerRegistry.CheckpointFor("chat-1"), initial.CheckpointPointer);
            Assert.True(File.Exists(Path.Combine(folder.Path, initial.CheckpointPointer!)));
            Assert.Empty(initial.OutstandingJobReferences);
        }

        using (var restarted = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), restarted);
            var recovered = await managers.GetAsync("manager/main", CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal("manager/main", recovered.Id);
            Assert.Equal(initial.ConversationId, recovered.ConversationId);
            Assert.Equal(initial.CreatedAt, recovered.CreatedAt);
            Assert.Equal(initial.CheckpointPointer, recovered.CheckpointPointer);
            Assert.Equal("idle", recovered.LifecycleState);
            Assert.Equal("available", recovered.Availability);
            Assert.Single(managers.List());
            Assert.Equal(1, provider.ManagerCreationCalls);

            var continued = await managers.SendAsync("manager/main", "follow-up", CancellationToken.None);
            Assert.Equal(SendDisposition.Accepted, continued!.Receipt.Disposition);
            await WaitForTerminal(restarted, "chat-1");
            Assert.Equal("idle", managers.Get("manager/main")!.LifecycleState);
            Assert.Equal("chat-1", managers.Get("manager/main")!.ConversationId);
            var history = await managers.ReadHistoryAsync("manager/main", CancellationToken.None);
            Assert.Equal(4, history!.History!.Messages.Count);
            Assert.Equal("follow-up", history.History.Messages[2].Text);
            Assert.Equal(1, provider.ManagerCreationCalls);
        }
    }

    [Fact]
    public async Task UnknownManagerIs404WithoutReadingProvider()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        Assert.Empty(managers.List());
        Assert.Null(await managers.GetAsync("manager/unknown", CancellationToken.None));
        Assert.Null(await managers.SendAsync("manager/unknown", "text", CancellationToken.None));
        Assert.Null(await managers.ReadHistoryAsync("manager/unknown", CancellationToken.None));
        var (status, _) = await ExecuteManagerResult(
            await ManagerEndpoints.GetAsync(managers, "unknown", CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(0, provider.ReadCalls);
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal(0, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task TemporaryProviderUnavailabilityPreservesLogicalMappingAndCanRecover()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        await WaitForTerminal(runtime, "chat-1");

        provider.ReturnNullHistory = true;
        var unavailable = await managers.GetAsync("manager/main", CancellationToken.None);
        Assert.Equal("temporarily_unavailable", unavailable!.Availability);
        Assert.Equal("idle", unavailable.LifecycleState);
        Assert.Equal("chat-1", unavailable.ConversationId);
        var (status, body) = await ExecuteManagerResult(
            await ManagerEndpoints.GetAsync(managers, "main", CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, status);
        using (var json = JsonDocument.Parse(body))
            Assert.Equal("temporarily_unavailable",
                json.RootElement.GetProperty("availability").GetString());

        var blocked = await managers.SendAsync("manager/main", "not sent", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, blocked!.Receipt.Disposition);
        Assert.Equal("provider_history_unavailable", blocked.Receipt.Reason);
        Assert.Equal(0, provider.SendCalls);
        Assert.Equal("chat-1", managers.Get("manager/main")!.ConversationId);

        provider.ReturnNullHistory = false;
        Assert.Equal("available",
            (await managers.GetAsync("manager/main", CancellationToken.None))!.Availability);
        var sent = await managers.SendAsync("manager/main", "later", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, sent!.Receipt.Disposition);
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal(1, provider.ManagerCreationCalls);
        Assert.Equal("chat-1", managers.Get("manager/main")!.ConversationId);
    }

    [Fact]
    public async Task DefinitivelyRejectedCreationCanBeExplicitlyRetriedWithSameId()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { RejectNextManagerCreation = true };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var rejected = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected, rejected.Receipt.Disposition);
        Assert.True(rejected.Receipt.Retryable);
        Assert.Equal("rejected", rejected.Manager.LifecycleState);
        Assert.Null(rejected.Manager.ConversationId);
        Assert.False(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Equal(1, provider.ManagerCreationCalls);

        // No automatic retry on another service instance.
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        Assert.Equal("rejected", recovered.Get("manager/main")!.LifecycleState);
        Assert.Equal(1, provider.ManagerCreationCalls);

        var accepted = await recovered.CreateAsync("manager/main", "explicit retry", CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted, accepted.Receipt.Disposition);
        Assert.Equal(rejected.Manager.Id, accepted.Manager.Id);
        Assert.Equal(rejected.Manager.CreatedAt, accepted.Manager.CreatedAt);
        Assert.Equal("chat-1", accepted.Manager.ConversationId);
        Assert.Equal(2, provider.ManagerCreationCalls);
        await WaitForTerminal(runtime, "chat-1");
    }

    [Fact]
    public async Task UncertainCreationKeepsLogicalIdAcrossRestartWithoutDuplicateCreate()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider { UncertainNextCreation = true };
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            var receipt = await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, receipt.Receipt.Disposition);
            Assert.Equal("uncertain", receipt.Manager.LifecycleState);
            Assert.Null(receipt.Manager.ConversationId);
            Assert.True(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        }

        var offline = new FakeProvider();
        using var restarted = ManagerRuntime(folder.Path, offline);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), restarted);
        Assert.Equal("uncertain", recovered.Get("manager/main")!.LifecycleState);
        var duplicate = await recovered.CreateAsync("manager/main", "repeat", CancellationToken.None);
        Assert.Equal("manager_already_exists", duplicate.Receipt.Reason);
        var blocked = await recovered.SendAsync("manager/main", "repeat", CancellationToken.None);
        Assert.Equal("manager_not_idle", blocked!.Receipt.Reason);
        Assert.Equal(0, offline.ManagerCreationCalls);
        Assert.Equal(0, offline.SendCalls);
    }

    [Fact]
    public async Task InterruptedManagerReservationCannotBeReplayedAfterRestart()
    {
        using var folder = new TemporaryDirectory();
        var registry = new ManagerRegistry(folder.Path);
        Assert.True(registry.TryReserve("manager/main", out var reserved));
        Assert.Equal("creating", reserved.LifecycleState);

        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        Assert.Equal("uncertain", managers.Get("manager/main")!.LifecycleState);
        var retry = await managers.CreateAsync("manager/main", "repeat", CancellationToken.None);
        Assert.Equal("manager_already_exists", retry.Receipt.Reason);
        var (status, body) = await ExecuteManagerResult(
            await ManagerEndpoints.ReadAsync(managers, "main", CancellationToken.None));
        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Contains("manager_creation_unresolved", body);
        Assert.Equal(0, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task InterruptedManagerSendStaysUncertainDespiteIdleJournal()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var registry = new ManagerRegistry(folder.Path);
            var managers = new ManagerService(registry, runtime);
            await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            await WaitForTerminal(runtime, "chat-1");
            registry.Update("manager/main", current => current with {
                LifecycleState = "sending"
            });
            Assert.Equal("idle", runtime.Get("chat-1")!.Status);
        }

        using var restarted = ManagerRuntime(folder.Path, provider);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), restarted);
        Assert.Equal("uncertain", recovered.Get("manager/main")!.LifecycleState);
        var blocked = await recovered.SendAsync("manager/main", "never replay", CancellationToken.None);
        Assert.Equal("manager_not_idle", blocked!.Receipt.Reason);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public async Task UncertainManagerSendIsNeverAutomaticallyResent()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            await WaitForTerminal(runtime, "chat-1");
            provider.UncertainNextSend = true;
            var sent = await managers.SendAsync("manager/main", "maybe delivered", CancellationToken.None);
            Assert.Equal(SendDisposition.Uncertain, sent!.Receipt.Disposition);
            Assert.Equal("uncertain", sent.Manager.LifecycleState);
        }

        using var restarted = ManagerRuntime(folder.Path, provider);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), restarted);
        var rejected = await recovered.SendAsync("manager/main", "duplicate", CancellationToken.None);
        Assert.Equal("manager_not_idle", rejected!.Receipt.Reason);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public async Task FailedReservationStoragePreventsAnyRemoteCreation()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var registry = new ManagerRegistry(folder.Path, beforePersist: _ =>
            throw new IOException("Simulated registry write failure"));
        var managers = new ManagerService(registry, runtime);

        await Assert.ThrowsAsync<IOException>(() =>
            managers.CreateAsync("manager/main", "first", CancellationToken.None));
        Assert.Equal(0, provider.ManagerCreationCalls);
        Assert.False(File.Exists(Path.Combine(folder.Path, "creation.pending")));
        Assert.Null(new ManagerRegistry(folder.Path).Get("manager/main"));
    }

    [Fact]
    public async Task PostDispatchRegistryFailureKeepsDurableReservationUnresolved()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var registry = new ManagerRegistry(folder.Path, beforePersist: record =>
        {
            if (record.ConversationId is not null)
                throw new IOException("Simulated binding failure");
        });
        var managers = new ManagerService(registry, runtime);
        await Assert.ThrowsAsync<IOException>(() =>
            managers.CreateAsync("manager/main", "first", CancellationToken.None));
        Assert.Equal(1, provider.ManagerCreationCalls);

        var recovered = new ManagerService(new ManagerRegistry(folder.Path), runtime);
        Assert.Equal("uncertain", recovered.Get("manager/main")!.LifecycleState);
        var rejected = await recovered.CreateAsync("manager/main", "duplicate", CancellationToken.None);
        Assert.Equal("manager_already_exists", rejected.Receipt.Reason);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public void CorruptManagerCheckpointFailsClosedInsteadOfBecomingUnknown()
    {
        using var folder = new TemporaryDirectory();
        var registry = new ManagerRegistry(folder.Path);
        registry.TryReserve("manager/main", out _);
        File.WriteAllText(Directory.GetFiles(Path.Combine(folder.Path, "managers"), "*.json").Single(),
            "{ not valid json }");
        Assert.Throws<InvalidDataException>(() => registry.Get("manager/main"));
        Assert.Throws<InvalidDataException>(() => registry.List());
    }

    [Fact]
    public async Task ConcurrentManagerCreationCannotDispatchTwice()
    {
        using var folder = new TemporaryDirectory();
        var pending = new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { PendingCreate = pending, DeferCompletion = true };
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var first = managers.CreateAsync("manager/main", "first", CancellationToken.None);
        Assert.False(first.IsCompleted);
        var second = await managers.CreateAsync("manager/main", "second", CancellationToken.None);
        Assert.Equal("manager_already_exists", second.Receipt.Reason);
        Assert.Equal("creating", second.Manager.LifecycleState);
        Assert.Equal(1, provider.ManagerCreationCalls);

        pending.SetResult(new SendReceipt(SendDisposition.Accepted, ConversationId: "chat-1"));
        Assert.Equal(SendDisposition.Accepted, (await first).Receipt.Disposition);
        provider.CompletePending();
        await WaitForTerminal(runtime, "chat-1");
        Assert.Equal("chat-1", managers.Get("manager/main")!.ConversationId);
        Assert.Equal(1, provider.ManagerCreationCalls);
    }

    [Fact]
    public async Task ManagerHttpContractsUseStableIdAndLeaveLegacyRoutesIndependent()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        using var runtime = ManagerRuntime(folder.Path, provider);
        var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);

        var (invalidStatus, _) = await ExecuteManagerResult(
            await ManagerEndpoints.CreateAsync(managers,
                new ManagerCreateRequest("../invalid", "text"), CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, invalidStatus);
        Assert.Empty(managers.List());
        var (createStatus, createBody) = await ExecuteManagerResult(
            await ManagerEndpoints.CreateAsync(managers,
                new ManagerCreateRequest("manager/main", "hello"), CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, createStatus);
        using (var created = JsonDocument.Parse(createBody))
        {
            Assert.Equal("manager/main",
                created.RootElement.GetProperty("manager").GetProperty("id").GetString());
            Assert.Equal("chat-1",
                created.RootElement.GetProperty("manager").GetProperty("conversationId").GetString());
        }
        await WaitForTerminal(runtime, "chat-1");

        var (listStatus, listBody) = await ExecuteManagerResult(ManagerEndpoints.List(managers));
        Assert.Equal(StatusCodes.Status200OK, listStatus);
        using (var listed = JsonDocument.Parse(listBody))
        {
            Assert.Equal(1, listed.RootElement.GetArrayLength());
            Assert.Equal("manager/main", listed.RootElement[0].GetProperty("id").GetString());
            Assert.Equal(0, listed.RootElement[0]
                .GetProperty("outstandingJobReferences").GetArrayLength());
        }
        var (duplicateStatus, _) = await ExecuteManagerResult(
            await ManagerEndpoints.CreateAsync(managers,
                new ManagerCreateRequest("manager/main", "not another chat"), CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, duplicateStatus);
        Assert.Equal(1, provider.ManagerCreationCalls);

        var (getStatus, getBody) = await ExecuteManagerResult(
            await ManagerEndpoints.GetAsync(managers, "main", CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, getStatus);
        using (var gotten = JsonDocument.Parse(getBody))
            Assert.Equal("available", gotten.RootElement.GetProperty("availability").GetString());

        var (sendStatus, _) = await ExecuteManagerResult(
            await ManagerEndpoints.SendAsync(managers, "main", "follow-up", CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, sendStatus);
        await WaitForTerminal(runtime, "chat-1");
        var (readStatus, readBody) = await ExecuteManagerResult(
            await ManagerEndpoints.ReadAsync(managers, "main", CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, readStatus);
        using var history = JsonDocument.Parse(readBody);
        Assert.Equal(4, history.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task MissingConversationCheckpointCannotBecomeAnUnknownManagerOrResend()
    {
        using var folder = new TemporaryDirectory();
        var provider = new FakeProvider();
        string pointer;
        using (var runtime = ManagerRuntime(folder.Path, provider))
        {
            var managers = new ManagerService(new ManagerRegistry(folder.Path), runtime);
            await managers.CreateAsync("manager/main", "first", CancellationToken.None);
            await WaitForTerminal(runtime, "chat-1");
            pointer = managers.Get("manager/main")!.CheckpointPointer!;
        }
        File.Delete(Path.Combine(folder.Path, pointer));

        using var restarted = ManagerRuntime(folder.Path, provider);
        var recovered = new ManagerService(new ManagerRegistry(folder.Path), restarted);
        Assert.Equal("uncertain", recovered.Get("manager/main")!.LifecycleState);
        var blocked = await recovered.SendAsync("manager/main", "never resend", CancellationToken.None);
        Assert.Equal("manager_not_idle", blocked!.Receipt.Reason);
        Assert.Equal("chat-1", recovered.Get("manager/main")!.ConversationId);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public void StaleMetadataRefreshNeverClearsAConcurrentSendingGuard()
    {
        using var folder = new TemporaryDirectory();
        var registry = new ManagerRegistry(folder.Path);
        registry.TryReserve("manager/main", out _);
        registry.Update("manager/main", current => current with {
            ConversationId = "chat-1",
            CheckpointPointer = ManagerRegistry.CheckpointFor("chat-1"),
            LifecycleState = "idle"
        });
        // An old snapshot observed idle, but another request began sending.
        registry.Update("manager/main", current => current with {
            LifecycleState = "sending"
        });
        var fresh = registry.RefreshLifecycle("manager/main", "idle", "idle");
        Assert.Equal("sending", fresh.LifecycleState);
        Assert.Equal("sending", new ManagerRegistry(folder.Path)
            .Get("manager/main")!.LifecycleState);
    }

    private static ChatRuntime ManagerRuntime(string directory, FakeProvider provider) =>
        new(new ScopedConversationProvider(provider, "scope-a"), new ChatJournal(directory));

    private static async Task<(int Status, string Body)> ExecuteManagerResult(IResult result)
    {
        var context = new DefaultHttpContext {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}
