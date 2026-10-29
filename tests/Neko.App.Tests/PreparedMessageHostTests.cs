using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Neko;

namespace Neko.App.Tests;

public sealed class PreparedMessageHostTests
{
    private sealed class Provider : IConversationProvider,IPreparedConversationProvider
    {
        public int SendCalls;
        public int CreateCalls;
        public PreparedOutboundMessage? LastSubmission;
        public SendDisposition NextSend=SendDisposition.Uncertain;

        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        public Task<VisibleConversation?> ReadAsync(string id,CancellationToken ct)=>
            Task.FromResult<VisibleConversation?>(null);
        public Task<StopReceipt> StopAsync(string id,CancellationToken ct)=>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,[EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<SendReceipt> SendAsync(string id,string text,CancellationToken ct)=>
            throw new InvalidOperationException("Text-only path should not be used.");
        public Task<SendReceipt> CreateAsync(string text,CancellationToken ct)=>
            throw new InvalidOperationException("Text-only path should not be used.");
        public Task<SendReceipt> SendPreparedAsync(PreparedOutboundMessage submission,CancellationToken ct)
        {
            SendCalls++;
            LastSubmission=submission;
            return Task.FromResult(new SendReceipt(NextSend,
                NextSend==SendDisposition.Uncertain?"delivery_unknown":""));
        }
        public Task<SendReceipt> CreatePreparedAsync(PreparedOutboundMessage submission,CancellationToken ct)
        {
            CreateCalls++;
            LastSubmission=submission;
            return Task.FromResult(new SendReceipt(SendDisposition.Uncertain,"delivery_unknown"));
        }
    }

    private static string TemporaryFolder()=>
        Path.Combine(Path.GetTempPath(),"Neko-PreparedHost-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExistingChatSendBuildsOneExactSubmissionAndBlocksUncertainRetry()
    {
        var path=TemporaryFolder();
        try
        {
            var boards=new OfflineBoardStore(path);
            var provider=new Provider();
            using var host=new ConversationWorkspaceHost(provider,boards);
            var id=Guid.NewGuid().ToString("D");
            await host.OpenPanelAsync(id);
            const string message="   exact line\r\nwith spaces  ";
            host.SetDraft(id,message);
            await host.SendAsync(id,message);

            var submission=Assert.IsType<PreparedOutboundMessage>(provider.LastSubmission);
            Assert.Equal(id,submission.ConversationId);
            Assert.Equal(message,submission.Text);
            Assert.True(Guid.TryParse(submission.ClientMessageId,out _));
            await host.SendAsync(id,message);
            Assert.Equal(1,provider.SendCalls);

            using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(host.Snapshot()));
            var panel=Assert.Single(snapshot.RootElement.GetProperty("panels").EnumerateArray());
            Assert.True(panel.GetProperty("Uncertain").GetBoolean());
            Assert.Equal(message,panel.GetProperty("Draft").GetString());
        }
        finally {if(Directory.Exists(path))Directory.Delete(path,true);}
    }

    [Fact]
    public async Task RejectedPreparedSendRestoresIdleStateAndKeepsDraft()
    {
        var path=TemporaryFolder();
        try
        {
            var provider=new Provider {NextSend=SendDisposition.Rejected};
            using var host=new ConversationWorkspaceHost(
                provider,new OfflineBoardStore(path));
            var id=Guid.NewGuid().ToString("D");
            await host.OpenPanelAsync(id);
            host.SetDraft(id,"draft retained");
            await host.SendAsync(id,"draft retained");
            using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(host.Snapshot()));
            var panel=Assert.Single(snapshot.RootElement.GetProperty("panels").EnumerateArray());
            Assert.False(panel.GetProperty("Sending").GetBoolean());
            Assert.False(panel.GetProperty("Generating").GetBoolean());
            Assert.False(panel.GetProperty("Uncertain").GetBoolean());
            Assert.Equal("draft retained",panel.GetProperty("Draft").GetString());
            var panels=(System.Collections.IDictionary)
                typeof(ConversationWorkspaceHost).GetField("_panels",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
            var panelObject=panels[id]!;
            Assert.Null(panelObject.GetType().GetProperty("PendingSubmission")!
                .GetValue(panelObject));
            await host.SendAsync(id,"second allowed");
            Assert.Equal(2,provider.SendCalls);
        }
        finally {if(Directory.Exists(path))Directory.Delete(path,true);}
    }

    [Fact]
    public async Task NewChatCreationCannotRepeatUncertainMessage()
    {
        var path=TemporaryFolder();
        try
        {
            var provider=new Provider();
            using var host=new ConversationWorkspaceHost(provider,new OfflineBoardStore(path));
            await host.CreateAsync("  first prompt ");
            await host.CreateAsync("  first prompt ");
            Assert.Equal(1,provider.CreateCalls);
            Assert.True(provider.LastSubmission!.CreatesConversation);
            Assert.Equal("  first prompt ",provider.LastSubmission.Text);
        }
        finally {if(Directory.Exists(path))Directory.Delete(path,true);}
    }
}
