using System.Text.Json;
using Neko;
using Builder=Neko.ChatGptNativeRequestPreparation;

namespace Neko.App.Tests;

public sealed class ChatGptNativeRequestPreparationTests
{
    private static readonly DateTimeOffset Now=
        new(2026,10,29,12,0,0,TimeSpan.Zero);

    private static Builder.ModelChoice Model(string? effort="max")=>
        new("example-model",effort,Now.AddSeconds(-2));

    private static Builder.NewConversationContext NewParent()=>
        Builder.NewConversationContext.Create();

    private static (PreparedOutboundMessage Submission,Builder.FollowUpContext Context)
        Existing(Builder.ParentEvidenceSource source=
            Builder.ParentEvidenceSource.FreshServerCurrentNode)
    {
        var id=Guid.NewGuid().ToString("D");
        var prepared=OutboundMessagePreparation.ForExisting(
            id,"  line one\r\nline two ✅  ").Message!;
        return (prepared,new Builder.FollowUpContext(
            id,Guid.NewGuid().ToString("D"),source,true,Now.AddSeconds(-2)));
    }

    [Fact]
    public void ExistingConversationHasExactNativeDispatcherOptions()
    {
        var (submission,context)=Existing();
        var result=Builder.ForExisting(submission,context,Model(),Now);

        Assert.True(result.Ready);
        var ready=Assert.IsType<Builder.PreparedDispatch>(result.Dispatch);
        Assert.Equal(submission.ClientMessageId,ready.ClientMessageId);
        using var options=JsonDocument.Parse(ready.SerializeOptionsForFirstPartySubmit());
        var root=options.RootElement;
        Assert.Equal(submission.ConversationId,
            root.GetProperty("conversationId").GetString());
        Assert.Equal(context.ParentMessageId,
            root.GetProperty("parentMessageId").GetString());
        Assert.Equal(submission.Text,root.GetProperty("prompt").GetString());
        Assert.Equal("example-model",root.GetProperty("model").GetString());
        Assert.Equal("max",root.GetProperty("thinkingEffort").GetString());
        Assert.False(root.GetProperty("isTemporaryChat").GetBoolean());
        Assert.Equal(6,root.EnumerateObject().Count());
        Assert.DoesNotContain(submission.ClientMessageId,
            ready.SerializeOptionsForFirstPartySubmit());
        Assert.True(ready.Preview.HasVerifiedParent);
        Assert.Equal("server_current_node",ready.Preview.ParentSource);
    }

    [Fact]
    public void NewConversationUsesFreshParentAndNullConversation()
    {
        var submission=OutboundMessagePreparation.ForNew("new message").Message!;
        var result=Builder.ForNew(submission,NewParent(),Model(null),Now);
        Assert.True(result.Ready);
        var ready=result.Dispatch!;
        using var json=JsonDocument.Parse(ready.SerializeOptionsForFirstPartySubmit());
        var root=json.RootElement;
        Assert.Equal(JsonValueKind.Null,root.GetProperty("conversationId").ValueKind);
        Assert.True(Guid.TryParseExact(
            root.GetProperty("parentMessageId").GetString(),"D",out _));
        Assert.False(root.TryGetProperty("thinkingEffort",out _));
        Assert.True(ready.Preview.CreatesConversation);
        Assert.False(ready.Preview.HasVerifiedParent);
        Assert.Equal("new_conversation",ready.Preview.ParentSource);
    }

    [Fact]
    public void RebuildingOnePreparedMessageKeepsTheSameParent()
    {
        var submission=OutboundMessagePreparation.ForNew("new message").Message!;
        var parent=NewParent();
        var first=Builder.ForNew(submission,parent,Model(),Now).Dispatch!;
        var second=Builder.ForNew(submission,parent,Model(),Now).Dispatch!;
        using var a=JsonDocument.Parse(first.SerializeOptionsForFirstPartySubmit());
        using var b=JsonDocument.Parse(second.SerializeOptionsForFirstPartySubmit());
        Assert.Equal(
            a.RootElement.GetProperty("parentMessageId").GetString(),
            b.RootElement.GetProperty("parentMessageId").GetString());
        Assert.Equal(first.SerializeOptionsForFirstPartySubmit(),
            second.SerializeOptionsForFirstPartySubmit());
        Assert.NotEqual(parent.ParentMessageId,NewParent().ParentMessageId);
    }

    [Fact]
    public void NewConversationCannotBePreparedWithoutParentContext()
    {
        var submission=OutboundMessagePreparation.ForNew("new message").Message!;
        Assert.Equal("parent_unavailable",
            Builder.ForNew(submission,null,Model(),Now).ErrorCode);
    }

    [Fact]
    public void FollowUpRequiresMatchingTargetAndCompletedStream()
    {
        var (submission,context)=Existing();
        Assert.Equal("verified_parent_unavailable",
            Builder.ForExisting(submission,context with
                {ConversationId=Guid.NewGuid().ToString("D")},Model(),Now).ErrorCode);
        Assert.Equal("verified_parent_unavailable",
            Builder.ForExisting(submission,context with
                {ConversationStreamComplete=false},Model(),Now).ErrorCode);
        Assert.Equal("verified_parent_unavailable",
            Builder.ForExisting(submission,context with
                {ParentMessageId="invalid"},Model(),Now).ErrorCode);
        Assert.Equal("verified_parent_unavailable",
            Builder.ForExisting(submission,null,Model(),Now).ErrorCode);
    }

    [Fact]
    public void StaleOrFutureEvidenceNeverBuildsRequest()
    {
        var (submission,context)=Existing();
        Assert.Equal("verified_parent_stale",
            Builder.ForExisting(submission,
                context with {ObservedAtUtc=Now.AddMinutes(-1)},
                Model(),Now).ErrorCode);
        Assert.Equal("verified_parent_stale",
            Builder.ForExisting(submission,
                context with {ObservedAtUtc=Now.AddSeconds(1)},
                Model(),Now).ErrorCode);
        Assert.Equal("verified_parent_stale",
            Builder.ForExisting(submission,
                context with {ObservedAtUtc=Now.ToOffset(TimeSpan.FromHours(1))},
                Model(),Now).ErrorCode);
    }

    [Fact]
    public void RecentLocallyObservedCompletedReplyHasBoundedFallback()
    {
        var (submission,context)=Existing(
            Builder.ParentEvidenceSource.ObservedCompletedReply);
        var near=Builder.ForExisting(submission,context with
            {ObservedAtUtc=Now.AddMinutes(-4)},Model(),Now);
        Assert.True(near.Ready);
        Assert.Equal("completed_stream",near.Dispatch!.Preview.ParentSource);
        var tooOld=Builder.ForExisting(submission,context with
            {ObservedAtUtc=Now.AddMinutes(-6)},Model(),Now);
        Assert.Equal("verified_parent_stale",tooOld.ErrorCode);
    }

    [Theory]
    [InlineData("not a model")]
    [InlineData("bad/model")]
    [InlineData("")]
    public void UnavailableModelsAreRejected(string slug)
    {
        var submission=OutboundMessagePreparation.ForNew("hello").Message!;
        Assert.Equal("model_unavailable",
            Builder.ForNew(submission,NewParent(),Model() with {Slug=slug},Now).ErrorCode);
    }

    [Fact]
    public void StaleOrInventedThinkingEffortIsRejected()
    {
        var submission=OutboundMessagePreparation.ForNew("hello").Message!;
        Assert.Equal("model_unavailable",
            Builder.ForNew(submission,NewParent(),Model() with
                {ObservedAtUtc=Now.AddMinutes(-6)},Now).ErrorCode);
        Assert.Equal("model_unavailable",
            Builder.ForNew(submission,NewParent(),Model("imaginary"),Now).ErrorCode);
        Assert.Equal("model_unavailable",
            Builder.ForNew(submission,NewParent(),null,Now).ErrorCode);
    }

    [Fact]
    public void SubmissionTypeCannotBeSwapped()
    {
        var (existing,context)=Existing();
        var first=OutboundMessagePreparation.ForNew("first").Message!;
        Assert.Equal("invalid_submission",Builder.ForNew(existing,NewParent(),Model(),Now).ErrorCode);
        Assert.Equal("invalid_submission",
            Builder.ForExisting(first,context,Model(),Now).ErrorCode);
    }

    [Fact]
    public void PreviewAndDiagnosticFormattingNeverRevealPromptOrIdentifiers()
    {
        var secret="DO_NOT_DISPLAY_MY_PRIVATE_MESSAGE_123";
        var submission=OutboundMessagePreparation.ForNew(secret).Message!;
        var result=Builder.ForNew(submission,NewParent(),Model(),Now);
        Assert.True(result.Ready);

        var display=string.Join(" ",result.ToString(),
            result.Dispatch!.ToString(),result.Dispatch.Preview.ToString(),
            JsonSerializer.Serialize(result.Dispatch.Preview));
        Assert.DoesNotContain(secret,display);
        Assert.DoesNotContain("example-model",display);
        Assert.DoesNotContain(submission.ClientMessageId,display);
        Assert.DoesNotContain("Authorization",display);
        Assert.DoesNotContain("cookie",display,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secret.Length,result.Dispatch.Preview.PromptLength);

        // Only explicit serialization for native invocation contains text.
        Assert.Contains(secret,result.Dispatch.SerializeOptionsForFirstPartySubmit());
    }

    [Fact]
    public void BuildsOnlyDispatcherArgumentsNotHttpHeadersOrEndpoints()
    {
        var submission=OutboundMessagePreparation.ForNew("hi").Message!;
        var result=Builder.ForNew(submission,NewParent(),Model(),Now);
        using var root=JsonDocument.Parse(result.Dispatch!.SerializeOptionsForFirstPartySubmit());
        var keys=root.RootElement.EnumerateObject().Select(p=>p.Name).ToArray();
        Assert.DoesNotContain("headers",keys);
        Assert.DoesNotContain("url",keys);
        Assert.DoesNotContain("cookie",keys);
        Assert.DoesNotContain("authorization",keys);
        Assert.DoesNotContain("clientMessageId",keys);
    }
}
