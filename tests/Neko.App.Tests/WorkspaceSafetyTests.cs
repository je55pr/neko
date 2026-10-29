using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Neko;

namespace Neko.App.Tests;

public sealed class WorkspaceSafetyTests
{
    private sealed class ControlledProvider : IConversationProvider
    {
        public Func<string,CancellationToken,Task<SendReceipt>>? OnSend;
        public Func<CancellationToken,Task<SendReceipt>>? OnCreate;
        public int SendCalls;
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<VisibleConversation?> ReadAsync(string id,CancellationToken ct)=>
            Task.FromResult<VisibleConversation?>(new VisibleConversation(id,"Chat",[]));
        public Task<SendReceipt> SendAsync(string id,string text,CancellationToken ct)
        {
            SendCalls++;
            return OnSend?.Invoke(id,ct) ??
                Task.FromResult(new SendReceipt(SendDisposition.Rejected,"rejected"));
        }
        public Task<SendReceipt> CreateAsync(string text,CancellationToken ct)=>
            OnCreate?.Invoke(ct) ?? Task.FromResult(
                new SendReceipt(SendDisposition.Rejected,"rejected"));
        public Task<StopReceipt> StopAsync(string id,CancellationToken ct)=>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,[EnumeratorCancellation]CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static (ConversationWorkspaceHost Host,string Folder) Setup(
        ControlledProvider provider)
    {
        var folder=Path.Combine(Path.GetTempPath(),
            "NekoSafety-"+Guid.NewGuid().ToString("N"));
        return (new ConversationWorkspaceHost(provider,new OfflineBoardStore(folder)),folder);
    }

    [Fact]
    public async Task UncertainDeliverySurvivesVerifyHistoryRefresh()
    {
        var provider=new ControlledProvider
        {
            OnSend=(_,_)=>Task.FromResult(
                new SendReceipt(SendDisposition.Uncertain,"delivery_unknown"))
        };
        var (host,folder)=Setup(provider);
        using(host)
        {
            try
            {
                await host.OpenPanelAsync("conversation");
                host.SetDraft("conversation","send once");
                await host.SendAsync("conversation","send once");
                await host.VerifyAsync("conversation");
                await host.SendAsync("conversation","send once");
                Assert.Equal(1,provider.SendCalls);
                using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(host.Snapshot()));
                var panel=Assert.Single(snapshot.RootElement.GetProperty("panels").EnumerateArray());
                Assert.True(panel.GetProperty("Uncertain").GetBoolean());
                Assert.Contains("unconfirmed",panel.GetProperty("Status").GetString()!,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal("send once",panel.GetProperty("Draft").GetString());
            }
            finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
        }
    }

    [Fact]
    public async Task UncertainCreationKeepsProviderSuppliedIdentifierWithoutOpeningPanel()
    {
        var id=Guid.NewGuid().ToString("D");
        var provider=new ControlledProvider
        {
            OnCreate=_=>Task.FromResult(
                new SendReceipt(SendDisposition.Uncertain,"delivery_unknown",id))
        };
        var (host,folder)=Setup(provider);
        using(host)
        {
            try
            {
                await host.CreateAsync("hello");
                using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(host.Snapshot()));
                var root=snapshot.RootElement;
                Assert.True(root.GetProperty("newChatUncertain").GetBoolean());
                Assert.Equal(id,root.GetProperty("createdChatId").GetString());
                Assert.Empty(root.GetProperty("panels").EnumerateArray());
            }
            finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
        }
    }

    [Fact]
    public async Task HostCancellationCannotLeavePanelSending()
    {
        var provider=new ControlledProvider();
        var (host,folder)=Setup(provider);
        using(host)
        {
            try
            {
                await host.OpenPanelAsync("conversation");
                // Cancel the window lifetime just before dispatch.
                var tokenSource=(CancellationTokenSource)
                    typeof(ConversationWorkspaceHost)
                    .GetField("_lifetime",System.Reflection.BindingFlags.Instance|
                        System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(host)!;
                tokenSource.Cancel();
                await host.SendAsync("conversation","hello");
                using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(host.Snapshot()));
                var panel=Assert.Single(snapshot.RootElement.GetProperty("panels").EnumerateArray());
                Assert.False(panel.GetProperty("Sending").GetBoolean());
                Assert.False(panel.GetProperty("Generating").GetBoolean());
                Assert.True(panel.GetProperty("Uncertain").GetBoolean());
            }
            finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
        }
    }
}
