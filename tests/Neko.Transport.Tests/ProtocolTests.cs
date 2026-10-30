using System.Text.Json;
using Neko;
using Neko.Transport;

namespace Neko.Transport.Tests;

public sealed class ProtocolTests
{
    private static readonly TransportSettings Wire=new()
    {
        ParentField="parent_message_id",
        ScopeField="scope_key",
        ConversationField="conversation_id"
    };

    private static TransportSettings ValidSettings(string profile) => new()
    {
        ProfileDirectory=profile,
        SiteOrigin="https://service.example.org",
        ApiPrefix="/service/",
        AssetPrefix="/assets/",
        ProjectListingPath="/service/groups/{scope}/list?cursor={cursor}",
        ConversationHistoryPath="/service/conversations/{id}",
        StreamStatusPath="/service/status/{id}",
        SubmitPath="/service/send",
        ScopePattern="^group-[a-z0-9]+$",
        ComposerSelector="form[data-composer]",
        SubmitExportName="submitCompletion",
        ModelMarker="global-selection",
        ModelCacheKey="model-list",
        FallbackMetadataKey="rich",
        ParentField="parent",
        ScopeField="group",
        ConversationField="id",
        ForwardHeaders=["authorization"]
    };

    [Fact]
    public void TransportRejectsNonCanonicalOriginsAndCrossOriginPaths()
    {
        var valid=ValidSettings(Path.GetTempPath());
        valid.Validate();
        foreach(var bad in new[]{
            "https://service.example.org/",
            "https://SERVICE.example.org",
            "http://service.example.org",
            new UriBuilder("https","service.example.org")
                {UserName="name",Password="pass"}.Uri.ToString()
        })
            Assert.Throws<InvalidOperationException>(()=>
                (valid with {SiteOrigin=bad}).Validate());
        Assert.Throws<InvalidOperationException>(()=>
            (valid with {ProjectListingPath="//elsewhere.example/list"}).Validate());
        Assert.Throws<InvalidOperationException>(()=>
            (valid with {ScopePattern="["}).Validate());
    }

    [Fact]
    public void TransportResolvesProfileRelativeToSettingsFile()
    {
        var folder=Path.Combine(Path.GetTempPath(),
            "neko-settings-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Directory.CreateDirectory(Path.Combine(folder,"profile"));
            var settings=ValidSettings("profile");
            var file=Path.Combine(folder,"adapter.json");
            File.WriteAllText(file,JsonSerializer.Serialize(new {transport=settings}));
            var parsed=TransportSettings.Read(file);
            Assert.Equal(Path.Combine(folder,"profile"),parsed.ProfileDirectory);
        }
        finally{Directory.Delete(folder,true);}
    }

    [Fact]
    public void PollingFallbackUsesPreviousPublishedText()
    {
        var id=Guid.NewGuid().ToString("D");
        var first=ProjectProvider.PollUpdate(id,"Hel","");
        Assert.NotNull(first);
        Assert.Equal(PublicUpdateOperation.Replace,first.Operation);
        Assert.Equal("Hel",first.Text);
        var second=ProjectProvider.PollUpdate(id,"Hello","Hel");
        Assert.NotNull(second);
        Assert.Equal(PublicUpdateOperation.Append,second.Operation);
        Assert.Equal("lo",second.Text);
        var replacement=ProjectProvider.PollUpdate(id,"Goodbye","Hello");
        Assert.NotNull(replacement);
        Assert.Equal(PublicUpdateOperation.Replace,replacement.Operation);
        Assert.Equal("Goodbye",replacement.Text);
        Assert.Null(ProjectProvider.PollUpdate(id,"Goodbye","Goodbye"));
    }

    [Fact]
    public void ParserAcceptsLargeChunksOfIndividuallyBoundedEvents()
    {
        var parser=new SseEventParser();
        var content=string.Concat(Enumerable.Repeat("event: message\ndata: small\n\n",50000));
        Assert.True(content.Length>1024*1024);
        var frames=parser.Push(content);
        Assert.Equal(50000,frames.Count);
        Assert.All(frames,frame=>Assert.Equal("small",frame.Data));
    }

    [Fact]
    public void ParserRejectsOversizedIncompleteLineOrAccumulatedData()
    {
        var parser=new SseEventParser();
        Assert.Throws<InvalidDataException>(()=>parser.Push(new string('x',1048577)));
        var others=new SseEventParser();
        others.Push("data: "+new string('y',600000)+"\n");
        Assert.Throws<InvalidDataException>(()=>others.Push(
            "data: "+new string('z',600000)+"\n"));
    }

    [Fact]
    public void UncorrelatedCreationCannotBeAcceptedFromMatchingPrompt()
    {
        var resolved=Guid.NewGuid().ToString("D");
        var additions=new []{resolved,Guid.NewGuid().ToString("D")};
        Assert.True(ProjectProvider.MatchesCreationReceipt(
            true,resolved,additions));
        Assert.False(ProjectProvider.MatchesCreationReceipt(
            true,null,additions));
        Assert.False(ProjectProvider.MatchesCreationReceipt(
            true,Guid.NewGuid().ToString("D"),additions));
        Assert.False(ProjectProvider.MatchesCreationReceipt(
            false,resolved,additions));
    }

    [Fact]
    public void StreamRequestParentUsesConfiguredFieldName()
    {
        var settings=new TransportSettings {ParentField="custom_parent"};
        var parent=Guid.NewGuid().ToString("D");
        using var payload=JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string,string>
            {
                ["custom_parent"]=parent,
                ["parent_message_id"]="wrong"
            }));
        Assert.Equal(parent,NativeLiveStreamHub.ExtractParent(
            payload.RootElement,settings));
        Assert.Null(NativeLiveStreamHub.ExtractParent(
            payload.RootElement,new TransportSettings {ParentField="missing"}));
    }

    [Fact]
    public void CreationRequiresTheRequestedScopeAndParent()
    {
        var scope="team-"+new string('c',24);
        var parent=Guid.NewGuid().ToString("D");
        object Build(string otherScope) => new Dictionary<string,object?>
        {
            [Wire.ParentField]=parent,
            [Wire.ConversationField]=Guid.NewGuid().ToString("D"),
            [Wire.ScopeField]=otherScope
        };
        using var correct=JsonDocument.Parse(JsonSerializer.Serialize(Build(scope)));
        using var wrong=JsonDocument.Parse(JsonSerializer.Serialize(Build("wrong")));
        using var missing=JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string,object?>{[Wire.ParentField]=parent}));
        Assert.True(NativeDispatchSender.MatchesWirePayload(
            correct.RootElement,null,parent,scope,Wire));
        Assert.False(NativeDispatchSender.MatchesWirePayload(
            wrong.RootElement,null,parent,scope,Wire));
        Assert.False(NativeDispatchSender.MatchesWirePayload(
            missing.RootElement,null,parent,scope,Wire));
        Assert.False(NativeDispatchSender.MatchesWirePayload(
            correct.RootElement,null,Guid.NewGuid().ToString("D"),scope,Wire));
    }

    [Fact]
    public void FollowUpRequiresExactConversationAndParent()
    {
        var parent=Guid.NewGuid().ToString("D");
        var conversation=Guid.NewGuid().ToString("D");
        using var payload=JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string,object?>
            {
                [Wire.ParentField]=parent,[Wire.ConversationField]=conversation
            }));
        Assert.True(NativeDispatchSender.MatchesWirePayload(
            payload.RootElement,conversation,parent,null,Wire));
        Assert.False(NativeDispatchSender.MatchesWirePayload(
            payload.RootElement,Guid.NewGuid().ToString("D"),parent,null,Wire));
    }

    [Fact]
    public void LiveSseArchivePreservesReplacedVisibleDraftsButNotPrivateActivity()
    {
        var folder=Path.Combine(Path.GetTempPath(),
            "neko-stream-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var id=Guid.NewGuid().ToString("D");
            using var attempt=new NativeLiveStreamHub.Attempt(
                Guid.NewGuid().ToString("D"),id,null,"sample user input",folder);
            void Frame(object payload)
            {
                var data="data: "+JsonSerializer.Serialize(payload)+"\n\n";
                var bytes=System.Text.Encoding.UTF8.GetBytes(data);
                for(var index=0;index<bytes.Length;index+=11)
                    attempt.ReceiveBase64(Convert.ToBase64String(
                        bytes,index,Math.Min(11,bytes.Length-index)));
            }

            var message=Guid.NewGuid().ToString("D");
            Frame(new {type="delta",v=new {message=new {
                id=message,author=new {role="assistant"},channel="final",
                recipient="all",status="in_progress",
                content=new {parts=new[]{"Hello"}}
            }}});
            Frame(new {type="delta",p="/message/content/parts/0",
                o="append",v=" world"});
            Frame(new {type="delta",p="/message/content/parts/0",
                o="replace",v="Goodbye"});
            Frame(new {type="delta",v=new {message=new {
                id=Guid.NewGuid().ToString("D"),author=new {role="assistant"},
                channel="analysis",recipient="all",status="in_progress",
                content=new {parts=new[]{"private reasoning - must not persist"}}
            }}});
            attempt.Finish("complete",id,"Goodbye");
            var updates=new List<PublicConversationUpdate>();
            while(attempt.Updates.Reader.TryRead(out var update))updates.Add(update);
            Assert.Equal(3,updates.Count);
            Assert.Equal(PublicUpdateOperation.Replace,updates[0].Operation);
            Assert.Equal("Hello",updates[0].Text);
            Assert.Equal(PublicUpdateOperation.Append,updates[1].Operation);
            Assert.Equal(" world",updates[1].Text);
            Assert.Equal(PublicUpdateOperation.Replace,updates[2].Operation);
            Assert.Equal("Goodbye",updates[2].Text);
            var human=File.ReadAllText(attempt.Archive.TextPath);
            var events=File.ReadAllText(attempt.Archive.JsonlPath);
            Assert.Contains("Hello",human);
            Assert.Contains(" world",human);
            Assert.Contains("Goodbye",human);
            Assert.Contains("sample user input",human);
            Assert.DoesNotContain("private reasoning",human);
            Assert.DoesNotContain("private reasoning",events);
            Assert.Contains("\"operation\":\"replace\"",events);
            Assert.Contains("\"operation\":\"append\"",events);
            // The per-conversation file accumulates turns without erasing
            // the individual revision logs.
            var aggregate=Path.Combine(folder,"conversation-"+id+".txt");
            Assert.Contains("Goodbye",File.ReadAllText(aggregate));
            using(var next=new VisibleTurnArchive(
                "second prompt",id,null,folder))
                next.Finish("complete",id,"second visible answer");
            var combined=File.ReadAllText(aggregate);
            Assert.Contains("sample user input",combined);
            Assert.Contains("Hello",combined);
            Assert.Contains("second visible answer",combined);
            Assert.Equal(1,combined.Split("second visible answer").Length-1);
        }
        finally {Directory.Delete(folder,true);}
    }

    [Fact]
    public void CreationResultSummarySerializesWithoutPropertyCollision()
    {
        var receipt=new NativeDispatchSender.Result(
            "sse_accepted",true,true,200,Guid.NewGuid().ToString("D"),1,1);
        var report=new {
            state="verified_in_project",
            submissionState=receipt.State,
            receipt.Dispatched,
            receipt.HttpStatus,
            receipt.ServerConversationId
        };
        var serialized=JsonSerializer.Serialize(report);
        Assert.Contains("verified_in_project",serialized,StringComparison.Ordinal);
        Assert.Contains("sse_accepted",serialized,StringComparison.Ordinal);
    }

    [Fact]
    public void StreamIdTrackerRecoversServerIdFromSplitUtf8Frames()
    {
        var id=Guid.NewGuid().ToString("D");
        var wire="event: message\ndata: {\"metadata\":\"caf\\u00e9\",\"v\":{\"conversation_id\":\""+
                 id+"\"}}\n\n";
        var bytes=System.Text.Encoding.UTF8.GetBytes(wire);
        var tracker=new SseConversationIdTracker();
        string? recovered=null;
        for(var offset=0;offset<bytes.Length;offset+=7)
        {
            var count=Math.Min(7,bytes.Length-offset);
            recovered??=tracker.PushBase64(
                Convert.ToBase64String(bytes,offset,count));
        }
        Assert.Equal(id,recovered);
    }

    [Fact]
    public void StreamIdTrackerRejectsUnrelatedIdsAndHugePayloads()
    {
        Assert.Null(SseConversationIdTracker.FindId(
            "{\"user_id\":\""+Guid.NewGuid()+"\"}"));
        Assert.Null(SseConversationIdTracker.FindId(
            "{\"conversation_id\":\"invalid\"}"));
        var tracker=new SseConversationIdTracker();
        Assert.Null(tracker.PushBase64(new string('x',1_000_001)));
    }

    [Fact]
    public void NativeCreationPassesTheActualFirstPartyProjectParameter()
    {
        // Creation takes projectId in the application's submission options.
        // A wrong option name silently falls back to frontend state.
        Assert.Contains("{projectId:scope}",NativeDispatchSender.Script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{ignoredScope:scope}",NativeDispatchSender.Script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SseFramesSurviveArbitraryNetworkSplits()
    {
        var parser=new SseEventParser();
        Assert.Empty(parser.Push("event: message\r\ndata: {\"he"));
        Assert.Empty(parser.Push("llo\":true}\r\n"));
        var frames=parser.Push("\r\nevent: end\ndata: [DONE]\n\n");
        Assert.Equal(2,frames.Count);
        Assert.Equal("message",frames[0].Name);
        Assert.Equal("{\"hello\":true}",frames[0].Data);
        Assert.Equal("[DONE]",frames[1].Data);
    }

}
