using Neko;

namespace Neko.Core.Tests;

public sealed class ConversationMessageOrderTests
{
    private static ConversationMessage Message(string id, string role, double created) =>
        new() { Id = id, Role = role, Created = created };

    [Fact]
    public void KeepsCausalSourceOrderWhenAnAssistantClockIsEarlierThanItsUser()
    {
        var messages = new[]
        {
            Message("user-1", "user", 100),
            Message("reply-1", "assistant", 90),
            Message("user-2", "user", 200),
            Message("reply-2", "assistant", 210)
        };

        var ordered = ConversationMessageOrder.Chronological(messages);

        Assert.Equal(new[] { "user-1", "reply-1", "user-2", "reply-2" },
            ordered.Select(x => x.Id));
        Assert.Equal(new[] { "user-1", "reply-1", "user-2", "reply-2" },
            messages.Select(x => x.Id));
    }

    [Fact]
    public void SortsGenuinelyUnorderedTraversalChronologically()
    {
        var messages = new[]
        {
            Message("user-later", "user", 300),
            Message("reply-later", "assistant", 350),
            Message("user-first", "user", 100),
            Message("reply-first", "assistant", 150)
        };

        Assert.Equal(new[] { "user-first", "reply-first", "user-later", "reply-later" },
            ConversationMessageOrder.Chronological(messages).Select(x => x.Id));
    }

    [Fact]
    public void LeavesUndatedAndInvalidTimestampSlotsInPlace()
    {
        var messages = new[]
        {
            Message("later", "assistant", 300),
            Message("undated", "tool", 0),
            Message("invalid", "tool", double.PositiveInfinity),
            Message("earlier", "assistant", 100)
        };

        Assert.Equal(new[] { "earlier", "undated", "invalid", "later" },
            ConversationMessageOrder.Chronological(messages).Select(x => x.Id));
    }

    [Fact]
    public void PreservesSourceOrderForEqualTimestamps()
    {
        var messages = new[]
        {
            Message("first", "assistant", 100),
            Message("second", "assistant", 100),
            Message("third", "assistant", 200)
        };

        Assert.Equal(new[] { "first", "second", "third" },
            ConversationMessageOrder.Chronological(messages).Select(x => x.Id));
    }

    [Fact]
    public void ReverseOrderedTurnsKeepRepliesAfterTheirPrompts()
    {
        var messages=new[]
        {
            Message("user-2","user",200),
            Message("reply-2","assistant",190),
            Message("user-1","user",100),
            Message("reply-1","assistant",90)
        };
        Assert.Equal(new[]{"user-1","reply-1","user-2","reply-2"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void ParentLinksAssociateRepliesAcrossTraversalBoundaries()
    {
        var messages=new[]
        {
            Message("user-2","user",200),
            new ConversationMessage{Id="reply-1",ParentId="user-1",
                Role="assistant",Created=90},
            new ConversationMessage{Id="reply-2",ParentId="user-2",
                Role="assistant",Created=190},
            Message("user-1","user",100)
        };
        Assert.Equal(new[]{"user-1","reply-1","user-2","reply-2"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void UndatedActivityStaysInsideItsCausalTurn()
    {
        var messages=new[]
        {
            Message("user-2","user",200),
            Message("tool-2","tool",0),
            Message("reply-2","assistant",190),
            Message("user-1","user",100),
            Message("tool-1","tool",0),
            Message("reply-1","assistant",90)
        };
        Assert.Equal(new[]{
            "user-1","tool-1","reply-1","user-2","tool-2","reply-2"
        },ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void ReturnsAnIndependentArrayForAnEmptyOrSingleDatedConversation()
    {
        var empty = new List<ConversationMessage>();
        Assert.Empty(ConversationMessageOrder.Chronological(empty));

        var single = new[] { Message("one", "user", 100) };
        var ordered = ConversationMessageOrder.Chronological(single);
        Assert.NotSame(single, ordered);
        Assert.Same(single[0], Assert.Single(ordered));
    }
    [Fact]
    public void ReversedTraversalSortsToolBeforeItsParentLinkedReply()
    {
        var messages=new[]
        {
            new ConversationMessage{Id="reply-2",ParentId="tool-2",
                Role="assistant",Created=210},
            new ConversationMessage{Id="tool-2",ParentId="user-2",
                Role="tool",Created=205},
            Message("user-2","user",200),
            new ConversationMessage{Id="reply-1",ParentId="user-1",
                Role="assistant",Created=110},
            Message("user-1","user",100)
        };
        Assert.Equal(new[]{"user-1","reply-1","user-2","tool-2","reply-2"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void ParentLinkedReplyListedBeforeItsPromptIsStillAfterIt()
    {
        var messages=new[]
        {
            new ConversationMessage{Id="reply-1",ParentId="user-1",
                Role="assistant",Created=90},
            Message("user-1","user",100),
            Message("user-2","user",200),
            Message("reply-2","assistant",210)
        };
        Assert.Equal(new[]{"user-1","reply-1","user-2","reply-2"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void CrossTurnParentLinkOverridesMisleadingRawSourceOrder()
    {
        var messages=new[]
        {
            Message("user-1","user",100),
            Message("user-2","user",200),
            new ConversationMessage{Id="reply-1",ParentId="user-1",
                Role="assistant",Created=90},
            Message("reply-2","assistant",210)
        };
        Assert.Equal(new[]{"user-1","reply-1","user-2","reply-2"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }
    [Fact]
    public void ToolWithClockEarlierThanPromptNeverMovesAheadOfItsUser()
    {
        var messages=new[]
        {
            Message("user-1","user",100),
            Message("tool-1","tool",50),
            Message("reply-1","assistant",105)
        };
        Assert.Equal(new[]{"user-1","tool-1","reply-1"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void UndatedUserDoesNotHideReversedDatedUsers()
    {
        var messages=new[]
        {
            Message("user-3","user",300),
            Message("reply-3","assistant",290),
            Message("user-undated","user",0),
            Message("reply-undated","assistant",0),
            Message("user-1","user",100),
            Message("reply-1","assistant",90)
        };
        Assert.Equal(new[]{
            "user-1","reply-1",
            "user-undated","reply-undated",
            "user-3","reply-3"
        },ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void CyclicParentLinksDoNotDropMessages()
    {
        var messages=new[]
        {
            Message("user","user",100),
            new ConversationMessage{Id="a",ParentId="b",Role="tool",Created=102},
            new ConversationMessage{Id="b",ParentId="a",Role="tool",Created=103}
        };
        Assert.Equal(new[]{"user","a","b"},
            ConversationMessageOrder.Chronological(messages).Select(x=>x.Id));
    }

    [Fact]
    public void ManyTurnsCanBeOrderedWithoutQuadraticScanning()
    {
        var messages=new List<ConversationMessage>();
        for(var i=1200;i>=1;i--)
        {
            messages.Add(Message($"user-{i}","user",i*100));
            messages.Add(new ConversationMessage{Id=$"reply-{i}",ParentId=$"user-{i}",
                Role="assistant",Created=i*100-1});
        }
        var ordered=ConversationMessageOrder.Chronological(messages);
        Assert.Equal(2400,ordered.Length);
        Assert.Equal("user-1",ordered[0].Id);
        Assert.Equal("reply-1",ordered[1].Id);
        Assert.Equal("user-1200",ordered[^2].Id);
        Assert.Equal("reply-1200",ordered[^1].Id);
    }

}

public sealed class WorkspacePanelOrderingTests
{
    [Fact]
    public void MovesAnExistingPanelToTheFront()
    {
        var ids = new List<string> { "A", "B", "C", "D" };
        Assert.True(WorkspacePanelOrdering.Move(ids, "D", "A", after: false));
        Assert.Equal(new[] { "D", "A", "B", "C" }, ids);
    }

    [Fact]
    public void MovesAnExistingPanelToTheEnd()
    {
        var ids = new List<string> { "A", "B", "C", "D" };
        Assert.True(WorkspacePanelOrdering.Move(ids, "A", "D", after: true));
        Assert.Equal(new[] { "B", "C", "D", "A" }, ids);
    }

    [Fact]
    public void MovesAcrossTheTargetWithoutAnIndexShift()
    {
        var ids = new List<string> { "A", "B", "C", "D" };
        Assert.True(WorkspacePanelOrdering.Move(ids, "B", "C", after: true));
        Assert.Equal(new[] { "A", "C", "B", "D" }, ids);
    }

    [Theory]
    [InlineData("missing", "A")]
    [InlineData("A", "missing")]
    [InlineData("A", "A")]
    [InlineData("", "B")]
    public void InvalidMovesLeaveTheOriginalOrderUnchanged(string source, string target)
    {
        var ids = new List<string> { "A", "B", "C" };
        Assert.False(WorkspacePanelOrdering.Move(ids, source, target, after: false));
        Assert.Equal(new[] { "A", "B", "C" }, ids);
    }

    [Fact]
    public void AlreadyAdjacentOrderDoesNotCountAsAChange()
    {
        var ids = new List<string> { "A", "B", "C" };
        Assert.False(WorkspacePanelOrdering.Move(ids, "B", "C", after: false));
        Assert.Equal(new[] { "A", "B", "C" }, ids);
    }
}
