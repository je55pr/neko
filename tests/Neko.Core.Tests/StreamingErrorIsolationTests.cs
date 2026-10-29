using Neko;

namespace Neko.Core.Tests;

public sealed class StreamingErrorIsolationTests
{
    private static NormalizedStreamUpdate Event(
        string id,StreamContent content,StreamOperation operation,string text="")=>
        new(id,content,operation,text);

    [Fact]
    public void ActivityErrorCannotHideAFailedFinalAnswer()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"partial"));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Fail)));
        reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Start));
        Assert.False(reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Replace,new string('x',18_001))));
        Assert.Equal("activity_limit_reached",reducer.Error);
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Append,"should not be shown")));
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.False(reducer.Completed);
        Assert.Equal("partial",reducer.FinalText);
    }

    [Fact]
    public void ActivityErrorCannotMaskATruncatedFinalAnswer()
    {
        var reducer=new LiveConversationReducer();
        reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,new string('x',950_000)));
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Append,new string('y',100_000))));
        Assert.Equal("text_limit_reached",reducer.Error);
        reducer.Apply(Event("tool",StreamContent.ToolActivity,
            StreamOperation.Replace,new string('a',18_001)));
        Assert.Equal("activity_limit_reached",reducer.Error);
        Assert.False(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Complete)));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Replace,"recovered")));
        Assert.True(reducer.Apply(Event("reply",StreamContent.Final,
            StreamOperation.Complete)));
    }

    [Fact]
    public void OversizedFirstActivityReportsTheLimitWithoutCreatingASlot()
    {
        var reducer=new LiveConversationReducer();
        Assert.False(reducer.Apply(Event("first",StreamContent.PublicProgress,
            StreamOperation.Replace,new string('x',18_001))));
        Assert.Equal("activity_limit_reached",reducer.Error);
        Assert.Empty(reducer.Activities);
        Assert.True(reducer.Apply(Event("later",StreamContent.ToolActivity,
            StreamOperation.Replace,"visible")));
        Assert.Single(reducer.Activities);
    }
}
