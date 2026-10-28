using Neko;

namespace Neko.Core.Tests;

public sealed class LiveConversationReducerTests
{
    private static NormalizedStreamUpdate Event(
        string id,StreamContent kind,StreamOperation operation,
        string text="",int part=0)=>new(id,kind,operation,text,part);

    [Fact]
    public void CompletedAnswerCollectsBoundedPublicTextInPartOrder()
    {
        var reducer=new LiveConversationReducer();
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Start)));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"world",2)));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"Hello ",0)));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Append,"!",2)));
        Assert.Equal("Hello world!",reducer.FinalText);
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.True(reducer.Completed);
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Append,"should-not-appear")));
        Assert.Equal("Hello world!",reducer.FinalText);
    }

    [Fact]
    public void HiddenContentIsNeverExposedEvenAfterDelayedClassification()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("private",StreamContent.Final,
            StreamOperation.Replace,"internal text"));
        Assert.Equal("internal text",reducer.FinalText);
        Assert.True(reducer.Apply(Event("private",StreamContent.Hidden,
            StreamOperation.Replace,"another hidden chunk")));
        Assert.Empty(reducer.FinalText);
        Assert.False(reducer.Apply(Event("private",StreamContent.Final,
            StreamOperation.Replace,"late chunk")));
        Assert.False(reducer.Apply(Event("private",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.False(reducer.Completed);

        reducer.Apply(Event("public",StreamContent.Final,
            StreamOperation.Start));
        reducer.Apply(Event("public",StreamContent.Final,
            StreamOperation.Append,"Visible"));
        Assert.Equal("Visible",reducer.FinalText);
    }

    [Fact]
    public void StaleRepliesCannotReplaceCurrentAnswer()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("old",StreamContent.Final,
            StreamOperation.Replace,"old content"));
        reducer.Apply(Event("next",StreamContent.Final,
            StreamOperation.Start));
        reducer.Apply(Event("next",StreamContent.Final,
            StreamOperation.Replace,"new content"));
        Assert.False(reducer.Apply(Event("old",StreamContent.Final,
            StreamOperation.Append,"stale")));
        Assert.Equal("new content",reducer.FinalText);
        Assert.Equal("next",reducer.FinalMessageId);
    }

    [Fact]
    public void TotalTextBudgetAppliesAcrossAllParts()
    {
        var reducer=new LiveConversationReducer();
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,new string('a',600_000),0)));
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,new string('b',600_000),1)));
        Assert.Equal(600_000,reducer.FinalText.Length);
        Assert.Equal("text_limit_reached",reducer.Error);
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"short",0)));
        Assert.Equal("short",reducer.FinalText);
    }

    [Fact]
    public void ReplacingAPartWithEmptyTextClearsItsPreviousContents()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"before"));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"")));
        Assert.Empty(reducer.FinalText);
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Complete)));
    }

    [Fact]
    public void ToolActivityAndProgressNeverCompleteTheFinalAnswer()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Start));
        reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Append,"Searching"));
        Assert.True(reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Complete)));
        Assert.False(reducer.Completed);
        var activity=Assert.Single(reducer.Activities);
        Assert.Equal("Searching",activity.Text);
        Assert.True(activity.Completed);
        Assert.Empty(reducer.FinalText);
        Assert.False(reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Append,"late")));
    }

    [Fact]
    public void ActivityLimitPreventsAccumulatingUnlimitedToolOutput()
    {
        var reducer=new LiveConversationReducer();
        Assert.True(reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Replace,new string('a',15_000))));
        Assert.False(reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Append,new string('b',5_000))));
        Assert.Equal("activity_limit_reached",reducer.Error);
        Assert.Equal(15_000,Assert.Single(reducer.Activities).Text.Length);
        for(var i=1;i<60;i++)
            reducer.Apply(Event("tool-"+i,StreamContent.ToolActivity,
                StreamOperation.Replace,"test"));
        Assert.Equal(48,reducer.Activities.Length);
    }

    [Fact]
    public void InvalidPartIndicesAndEmptyIdentifiersAreRejected()
    {
        var reducer=new LiveConversationReducer();
        Assert.False(reducer.Apply(Event("",StreamContent.Final,
            StreamOperation.Replace,"content")));
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"content",-1)));
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"content",100)));
        Assert.Empty(reducer.FinalText);
    }

    [Fact]
    public void StartingNewAnswerResetsEarlierErrorAndCompletion()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("old",StreamContent.Final,
            StreamOperation.Replace,"reply"));
        reducer.Apply(Event("old",StreamContent.Final,
            StreamOperation.Fail));
        Assert.Equal("stream_failed",reducer.Error);
        reducer.Apply(Event("new",StreamContent.Final,
            StreamOperation.Start));
        Assert.Empty(reducer.Error);
        Assert.Empty(reducer.FinalText);
        Assert.False(reducer.Completed);
    }

    [Fact]
    public void DelayedHiddenClassificationAlsoErasesActivity()
    {
        var reducer=new LiveConversationReducer();
        Assert.True(reducer.Apply(Event("item",StreamContent.PublicProgress,
            StreamOperation.Replace,"sensitive if wrongly classified")));
        Assert.Single(reducer.Activities);
        Assert.True(reducer.Apply(Event("item",StreamContent.Hidden,
            StreamOperation.Start)));
        Assert.Empty(reducer.Activities);
        Assert.False(reducer.Apply(Event("item",StreamContent.PublicProgress,
            StreamOperation.Append,"late data")));
    }

    [Fact]
    public void ExhaustedHiddenIdBudgetFailsClosed()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("public",StreamContent.Final,
            StreamOperation.Replace,"visible earlier"));
        for(var i=0;i<257;i++)
            reducer.Apply(Event("hidden-"+i,StreamContent.Hidden,
                StreamOperation.Start));
        Assert.Empty(reducer.FinalText);
        Assert.Empty(reducer.Activities);
        Assert.Equal("visibility_limit_reached",reducer.Error);
        Assert.False(reducer.Apply(Event("next",StreamContent.Final,
            StreamOperation.Replace,"must never appear")));
        Assert.Empty(reducer.FinalText);
    }
}
