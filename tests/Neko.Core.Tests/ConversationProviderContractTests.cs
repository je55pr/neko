using Neko;

namespace Neko.Core.Tests;

public sealed class ConversationProviderContractTests
{
    [Fact]
    public async Task DisconnectedProviderMakesNoPretendConversationsOrSubmissions()
    {
        IConversationProvider backend=new DisconnectedConversationProvider();
        Assert.False(backend.SupportsStop);
        Assert.Empty(await backend.BrowseAsync(CancellationToken.None));
        Assert.Null(await backend.ReadAsync("local",CancellationToken.None));

        var created=await backend.CreateAsync("hello",CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected,created.Disposition);
        Assert.Null(created.ConversationId);
        Assert.NotEmpty(created.Reason);

        var sent=await backend.SendAsync("local","hello",CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected,sent.Disposition);
        Assert.Null(sent.MessageId);

        var stopped=await backend.StopAsync("local",CancellationToken.None);
        Assert.Equal(StopDisposition.NotActive,stopped.Disposition);

        var updates=new List<PublicConversationUpdate>();
        await foreach(var update in backend.WatchAsync("local",CancellationToken.None))
            updates.Add(update);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task DisconnectedProviderHonoursCancellation()
    {
        IConversationProvider backend=new DisconnectedConversationProvider();
        using var source=new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            backend.BrowseAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            backend.CreateAsync("hello",source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            backend.SendAsync("local","hello",source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            backend.StopAsync("local",source.Token));
    }

    [Fact]
    public void ExposedMessageAndStreamCategoriesAreVisibleOnly()
    {
        Assert.Equal(
            new[]{"User","Assistant","Activity"},
            Enum.GetNames<VisibleMessageRole>());
        Assert.Equal(
            new[]{"Final","Progress","Activity"},
            Enum.GetNames<PublicUpdateKind>());
    }
}
