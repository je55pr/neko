using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.App.Tests;

public sealed class ConfiguredProviderTests
{
    private sealed class SseHandler : HttpMessageHandler
    {
        public string? SentJson;
        public string? Authorization;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests++;
            SentJson=await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization=request.Headers.Authorization?.ToString();
            var payload="data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\n"+
                "data: {\"choices\":[{\"delta\":{\"content\":\"there\"},\"finish_reason\":\"stop\"}]}\n\n"+
                "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(payload,Encoding.UTF8,"text/event-stream")
            };
        }
    }

    private static ConfiguredChatProvider Provider(SseHandler handler)=>
        new(new HttpClient(handler),new Uri("https://example.test/chat/completions"),
            "example-model","local-test-key");

    [Fact]
    public async Task NewConversationSendsAndStreamsAssistantText()
    {
        var handler=new SseHandler();
        using var provider=Provider(handler);
        var created=await provider.CreateAsync("test prompt",CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted,created.Disposition);
        Assert.NotNull(created.ConversationId);
        var id=created.ConversationId!;
        var output=new StringBuilder();
        await foreach(var update in provider.WatchAsync(id,
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
        {
            if(update.Operation==PublicUpdateOperation.Append)
                output.Append(update.Text);
            if(update.Operation==PublicUpdateOperation.Complete)break;
        }
        Assert.Equal("Hello there",output.ToString());
        var saved=await provider.ReadAsync(id,CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Collection(saved!.Messages,
            user=>{Assert.Equal(VisibleMessageRole.User,user.Role);
                Assert.Equal("test prompt",user.Text);},
            assistant=>{Assert.Equal(VisibleMessageRole.Assistant,assistant.Role);
                Assert.Equal("Hello there",assistant.Text);});
        Assert.Equal("Bearer local-test-key",handler.Authorization);
        Assert.Contains("example-model",handler.SentJson);
        Assert.Contains("test prompt",handler.SentJson);
        Assert.Single(await provider.BrowseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SubsequentSendIncludesEarlierConversationHistory()
    {
        var handler=new SseHandler();
        using var provider=Provider(handler);
        var first=await provider.CreateAsync("first",CancellationToken.None);
        await foreach(var frame in provider.WatchAsync(first.ConversationId!,
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
            if(frame.Operation==PublicUpdateOperation.Complete)break;
        var second=await provider.SendAsync(first.ConversationId!,"second",CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted,second.Disposition);
        using var body=JsonDocument.Parse(handler.SentJson!);
        var messages=body.RootElement.GetProperty("messages");
        Assert.Equal(3,messages.GetArrayLength());
        Assert.Equal("first",messages[0].GetProperty("content").GetString());
        Assert.Equal("Hello there",messages[1].GetProperty("content").GetString());
        Assert.Equal("second",messages[2].GetProperty("content").GetString());
    }


    [Fact]
    public async Task PreparedMessageIdentityIsPreservedThroughNativeAdapter()
    {
        var handler=new SseHandler();
        using var provider=Provider(handler);
        var first=OutboundMessagePreparation.ForNew("  Keep spaces\r\n").Message!;
        var created=await provider.CreatePreparedAsync(first,CancellationToken.None);
        Assert.Equal(SendDisposition.Accepted,created.Disposition);
        var history=await provider.ReadAsync(created.ConversationId!,CancellationToken.None);
        var user=Assert.Single(history!.Messages,m=>m.Role==VisibleMessageRole.User);
        Assert.Equal(first.ClientMessageId,user.Id);
        Assert.Equal(first.Text,user.Text);
        using var payload=JsonDocument.Parse(handler.SentJson!);
        var posted=payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(first.Text,posted);
        Assert.DoesNotContain(first.ClientMessageId,handler.SentJson);
    }

    [Fact]
    public async Task RejectedLocalSubmissionNeverContactsEndpoint()
    {
        var handler=new SseHandler();
        using var provider=Provider(handler);
        var invalid=OutboundMessagePreparation.ForNew("hello").Message!
            with {ClientMessageId="invalid"};
        var receipt=await provider.CreatePreparedAsync(invalid,CancellationToken.None);
        Assert.Equal(SendDisposition.Rejected,receipt.Disposition);
        Assert.Equal(0,handler.Requests);
    }

    [Fact]
    public void PlaintextRemoteEndpointIsRejected()
    {
        using var client=new HttpClient(new SseHandler());
        Assert.Throws<ArgumentException>(()=>
            new ConfiguredChatProvider(client,
                new Uri("http://example.test/chat"),"m","key"));
    }


    [Fact]
    public async Task HostNewChatRendersCompletedReplyAndStopsThinking()
    {
        var path=Path.Combine(Path.GetTempPath(),"Neko-Host-Test-"+
            Guid.NewGuid().ToString("N"));
        try
        {
            var boards=new OfflineBoardStore(path);
            using var provider=Provider(new SseHandler());
            using var host=new ConversationWorkspaceHost(provider,boards);
            await host.CreateAsync("test message");
            var ended=false;
            for(var i=0;i<100;i++)
            {
                using var doc=JsonDocument.Parse(JsonSerializer.Serialize(
                    host.Snapshot(),
                    new JsonSerializerOptions{
                        PropertyNamingPolicy=JsonNamingPolicy.CamelCase
                    }));
                var panels=doc.RootElement.GetProperty("panels");
                if(panels.GetArrayLength()==1)
                {
                    var panel=panels[0];
                    var messages=panel.GetProperty("messages");
                    ended=!panel.GetProperty("generating").GetBoolean() &&
                        messages.EnumerateArray().Any(m=>
                            m.GetProperty("role").GetString()=="assistant" &&
                            m.GetProperty("text").GetString()=="Hello there");
                    if(ended)break;
                }
                await Task.Delay(20);
            }
            Assert.True(ended,"The host should settle after the stream completes.");
            Assert.Single(boards.ConversationsForActiveBoard());
        }
        finally {if(Directory.Exists(path))Directory.Delete(path,true);}
    }

    [Fact]
    public void BoardConversationMembershipPersists()
    {
        var path=Path.Combine(Path.GetTempPath(),"Neko-Board-Membership-"+
            Guid.NewGuid().ToString("N"));
        try
        {
            var boards=new OfflineBoardStore(path);
            var id=Guid.NewGuid().ToString("D");
            Assert.True(boards.AddConversation(id));
            Assert.True(boards.AddConversation(id));
            Assert.Single(boards.ConversationsForActiveBoard());
            var loaded=new OfflineBoardStore(path);
            Assert.Equal(id,Assert.Single(loaded.ConversationsForActiveBoard()));
            Assert.True(loaded.RemoveConversation(id));
            Assert.Empty(new OfflineBoardStore(path).ConversationsForActiveBoard());
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
}
