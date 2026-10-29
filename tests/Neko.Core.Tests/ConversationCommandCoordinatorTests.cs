using System.Runtime.CompilerServices;
using Neko;

namespace Neko.Core.Tests;

public sealed class ConversationCommandCoordinatorTests
{
    private sealed class Provider : IConversationProvider
    {
        public Func<string,string,CancellationToken,Task<SendReceipt>>? OnSend;
        public Func<string,CancellationToken,Task<StopReceipt>>? OnStop;
        public Func<string,CancellationToken,Task<SendReceipt>>? OnCreate;
        public int SendCalls;
        public int StopCalls;
        public int CreateCalls;

        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(
            CancellationToken cancellation)=>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);

        public Task<VisibleConversation?> ReadAsync(
            string id,CancellationToken cancellation)=>
            Task.FromResult<VisibleConversation?>(null);

        public Task<SendReceipt> SendAsync(string id,string text,
            CancellationToken cancellation)
        {
            Interlocked.Increment(ref SendCalls);
            return OnSend?.Invoke(id,text,cancellation) ??
                Task.FromResult(new SendReceipt(SendDisposition.Rejected,"disconnected"));
        }

        public Task<StopReceipt> StopAsync(
            string id,CancellationToken cancellation)
        {
            Interlocked.Increment(ref StopCalls);
            return OnStop?.Invoke(id,cancellation) ??
                Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        }

        public Task<SendReceipt> CreateAsync(string text,CancellationToken cancellation)
        {
            Interlocked.Increment(ref CreateCalls);
            return OnCreate?.Invoke(text,cancellation) ??
                Task.FromResult(new SendReceipt(SendDisposition.Rejected,"disconnected"));
        }

        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,[EnumeratorCancellation]CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task OverlappingSendCannotDispatchTwice()
    {
        var provider=new Provider();
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnSend=(_,_,_)=>pending.Task;
        var coordinator=new ConversationCommandCoordinator(provider);
        var first=coordinator.SendAsync("conversation","hello");
        Assert.True(coordinator.GetState("conversation").Sending);
        var second=await coordinator.SendAsync("conversation","again");
        Assert.Equal(SendDisposition.Rejected,second.Disposition);
        Assert.Equal("send_in_progress",second.Reason);
        Assert.Equal(1,provider.SendCalls);
        pending.SetResult(new SendReceipt(SendDisposition.Accepted));
        Assert.Equal(SendDisposition.Accepted,(await first).Disposition);
        Assert.False(coordinator.GetState("conversation").Sending);
    }

    [Fact]
    public async Task StopCannotRaceAnUnresolvedSend()
    {
        var provider=new Provider();
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnSend=(_,_,_)=>pending.Task;
        var coordinator=new ConversationCommandCoordinator(provider);
        var send=coordinator.SendAsync("conversation","hello");
        var stop=await coordinator.StopAsync("conversation");
        Assert.Equal(StopDisposition.NotActive,stop.Disposition);
        Assert.Equal("send_in_progress",stop.Reason);
        Assert.Equal(0,provider.StopCalls);
        pending.SetResult(new SendReceipt(SendDisposition.Accepted));
        await send;
    }

    [Fact]
    public async Task SendCannotRaceAnUnresolvedStop()
    {
        var provider=new Provider();
        var pending=new TaskCompletionSource<StopReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnStop=(_,_)=>pending.Task;
        var coordinator=new ConversationCommandCoordinator(provider);
        var stop=coordinator.StopAsync("conversation");
        Assert.True(coordinator.GetState("conversation").Stopping);
        var send=await coordinator.SendAsync("conversation","hello");
        Assert.Equal(SendDisposition.Rejected,send.Disposition);
        Assert.Equal("stop_in_progress",send.Reason);
        Assert.Equal(0,provider.SendCalls);
        pending.SetResult(new StopReceipt(StopDisposition.Requested));
        await stop;
    }

    [Fact]
    public async Task UnknownDeliveryPreventsAutomaticRetry()
    {
        var provider=new Provider {
            OnSend=(_,_,_)=>throw new InvalidOperationException("private diagnostic value")
        };
        var coordinator=new ConversationCommandCoordinator(provider);
        var receipt=await coordinator.SendAsync("conversation","hello");
        Assert.Equal(SendDisposition.Uncertain,receipt.Disposition);
        Assert.Equal("delivery_unknown",receipt.Reason);
        Assert.True(coordinator.GetState("conversation").DeliveryUncertain);
        var retry=await coordinator.SendAsync("conversation","hello");
        Assert.Equal("previous_delivery_uncertain",retry.Reason);
        Assert.Equal(1,provider.SendCalls);
        Assert.True(coordinator.ConfirmReconciled("conversation"));
        provider.OnSend=(_,_,_)=>Task.FromResult(
            new SendReceipt(SendDisposition.Accepted));
        Assert.Equal(SendDisposition.Accepted,
            (await coordinator.SendAsync("conversation","new message")).Disposition);
    }

    [Fact]
    public async Task CancellationDuringUnknownDeliveryIsUncertain()
    {
        var provider=new Provider();
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnSend=(_,_,_)=>pending.Task;
        var coordinator=new ConversationCommandCoordinator(provider);
        using var source=new CancellationTokenSource();
        var send=coordinator.SendAsync("conversation","hello",source.Token);
        source.Cancel();
        pending.SetCanceled(source.Token);
        Assert.Equal(SendDisposition.Uncertain,(await send).Disposition);
        Assert.True(coordinator.GetState("conversation").DeliveryUncertain);
    }

    [Fact]
    public async Task NewChatCreationIsGloballySerialized()
    {
        var provider=new Provider();
        var pending=new TaskCompletionSource<SendReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnCreate=(_,_)=>pending.Task;
        var coordinator=new ConversationCommandCoordinator(provider);
        var first=coordinator.CreateAsync("hello");
        var second=await coordinator.CreateAsync("another");
        Assert.Equal("create_in_progress",second.Reason);
        Assert.Equal(1,provider.CreateCalls);
        pending.SetResult(new SendReceipt(SendDisposition.Accepted,
            ConversationId:"new-conversation"));
        Assert.Equal(SendDisposition.Accepted,(await first).Disposition);
    }

    [Fact]
    public async Task UnknownCreationRequiresReconciliationBeforeRetry()
    {
        var provider=new Provider {
            OnCreate=(_,_)=>throw new IOException("private provider detail")
        };
        var coordinator=new ConversationCommandCoordinator(provider);
        Assert.Equal(SendDisposition.Uncertain,
            (await coordinator.CreateAsync("hello")).Disposition);
        Assert.Equal("previous_creation_uncertain",
            (await coordinator.CreateAsync("hello")).Reason);
        Assert.Equal(1,provider.CreateCalls);
        Assert.True(coordinator.ConfirmCreationReconciled());
        provider.OnCreate=(_,_)=>Task.FromResult(
            new SendReceipt(SendDisposition.Rejected,"no provider"));
        Assert.Equal(SendDisposition.Rejected,
            (await coordinator.CreateAsync("hello")).Disposition);
    }

    [Fact]
    public async Task DisconnectedProviderDoesNotPretendToSend()
    {
        var coordinator=new ConversationCommandCoordinator(
            new DisconnectedConversationProvider());
        var attempt=await coordinator.SendAsync("conversation","hello");
        Assert.Equal(SendDisposition.Rejected,attempt.Disposition);
        Assert.False(coordinator.GetState("conversation").DeliveryUncertain);
        Assert.False(coordinator.GetState("conversation").Sending);
    }

    [Fact]
    public async Task InvalidInputDoesNotDispatchToProvider()
    {
        var provider=new Provider();
        var coordinator=new ConversationCommandCoordinator(provider);
        Assert.Equal("invalid_submission",
            (await coordinator.SendAsync("","hello")).Reason);
        Assert.Equal("invalid_submission",
            (await coordinator.SendAsync("id","   ")).Reason);
        Assert.Equal("invalid_submission",
            (await coordinator.CreateAsync("  ")).Reason);
        Assert.Equal(0,provider.SendCalls);
        Assert.Equal(0,provider.CreateCalls);
    }
}
