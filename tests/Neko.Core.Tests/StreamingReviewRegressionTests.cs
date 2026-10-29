using Neko;

namespace Neko.Core.Tests;

public sealed class StreamingReviewRegressionTests
{
    private static NormalizedStreamUpdate Update(
        string id,StreamContent content,StreamOperation operation,
        string text="",int part=0,string title="")=>
        new(id,content,operation,text,part,title);

    [Fact]
    public void AHiddenResetRequiresAnExplicitNewStart()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Update("old",StreamContent.Final,StreamOperation.Replace,"old"));
        reducer.Apply(Update("new",StreamContent.Final,StreamOperation.Start));
        reducer.Apply(Update("new",StreamContent.Hidden,StreamOperation.Start));
        Assert.False(reducer.Apply(Update("old",StreamContent.Final,
            StreamOperation.Append,"stale")));
        Assert.Empty(reducer.FinalText);
        Assert.True(reducer.Apply(Update("valid",StreamContent.Final,StreamOperation.Start)));
        Assert.True(reducer.Apply(Update("valid",StreamContent.Final,
            StreamOperation.Replace,"public")));
        Assert.Equal("public",reducer.FinalText);
    }

    [Fact]
    public void AFailedAnswerCannotReceiveMoreTextOrComplete()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Update("reply",StreamContent.Final,StreamOperation.Replace,"partial"));
        Assert.True(reducer.Apply(Update("reply",StreamContent.Final,StreamOperation.Fail)));
        Assert.False(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Append,"late")));
        Assert.False(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.False(reducer.Completed);
        Assert.Equal("stream_failed",reducer.Error);
    }

    [Fact]
    public void FailureCannotUndoAlreadyCompletedReply()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Replace,"complete"));
        Assert.True(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.False(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Fail)));
        Assert.True(reducer.Completed);
        Assert.Empty(reducer.Error);
    }

    [Fact]
    public void TruncatedTextCannotCompleteUntilLaterReplacementRecovers()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Replace,new string('a',900_000)));
        Assert.False(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Append,new string('b',200_000))));
        Assert.Equal("text_limit_reached",reducer.Error);
        Assert.False(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.True(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Replace,"safe")));
        Assert.Empty(reducer.Error);
        Assert.True(reducer.Apply(Update("reply",StreamContent.Final,
            StreamOperation.Complete)));
    }

    [Fact]
    public void RejectedNewActivityDoesNotConsumeCapacity()
    {
        var reducer=new LiveConversationReducer();
        for(var i=0;i<100;i++)
        {
            Assert.False(reducer.Apply(Update("bad-"+i,StreamContent.ToolActivity,
                StreamOperation.Append,"")));
            Assert.False(reducer.Apply(Update("large-"+i,StreamContent.ToolActivity,
                StreamOperation.Replace,new string('x',18_001))));
        }
        Assert.Empty(reducer.Activities);
        Assert.True(reducer.Apply(Update("valid",StreamContent.ToolActivity,
            StreamOperation.Replace,"small")));
        Assert.Single(reducer.Activities);
    }

    [Fact]
    public void ActivityTitlesAreBoundedAndFailuresAreVisible()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Update("tool",StreamContent.ToolActivity,
            StreamOperation.Start,title:new string('x',150)));
        Assert.Equal(120,Assert.Single(reducer.Activities).Title.Length);
        Assert.True(reducer.Apply(Update("tool",StreamContent.ToolActivity,
            StreamOperation.Fail)));
        Assert.Equal("Activity unavailable",Assert.Single(reducer.Activities).Title);
    }
}
