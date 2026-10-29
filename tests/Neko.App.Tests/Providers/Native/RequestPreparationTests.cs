using System.Text.Json;
using Neko;
using Builder=Neko.NativeConversationRequestPreparation;

namespace Neko.App.Tests;

public sealed class NativeConversationRequestPreparationTests
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
            id,"  line one\r\nline two âœ…  ").Message!;
        return (prepared,new Builder.FollowUpContext(
            id,Guid.NewGuid().ToString("D"),source,true,Now.AddSeconds(-2)));
    }

    [Fact]
    public void ExistingConversationHasExactNeutralAdapterArguments()
    {
        var (submission,context)=Existing();
        var result=Builder.ForExisting(submission,context,Model(),Now);

        Assert.True(result.Ready);
        var ready=Assert.IsType<Builder.PreparedDispatch>(result.Dispatch);
        Assert.Equal(submission.ClientMessageId,ready.ClientMessageId);
        var args=ready.GetArgumentsForAdapter();
        Assert.Equal(submission.ConversationId,args.ConversationId);
        Assert.Equal(context.ParentMessageId,args.ParentReference);
        Assert.Equal(submission.Text,args.Text);
        Assert.Equal("example-model",args.ModelId);
        Assert.Equal("max",args.QualitySetting);
        Assert.Equal(submission.ClientMessageId,args.ClientMessageId);
        Assert.DoesNotContain(submission.Text,args.ToString());
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
        var args=ready.GetArgumentsForAdapter();
        Assert.Null(args.ConversationId);
        Assert.True(Guid.TryParseExact(args.ParentReference,"D",out _));
        Assert.Null(args.QualitySetting);
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
        Assert.Equal(first.GetArgumentsForAdapter().ParentReference,
            second.GetArgumentsForAdapter().ParentReference);
        Assert.Equal(first.GetArgumentsForAdapter(),
            second.GetArgumentsForAdapter());
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
            Builder.ForNew(submission,NewParent(),Model("invalid effort"),Now).ErrorCode);
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
        var promptText="Sample ordinary prompt content for the redaction test";
        var submission=OutboundMessagePreparation.ForNew(promptText).Message!;
        var result=Builder.ForNew(submission,NewParent(),Model(),Now);
        Assert.True(result.Ready);

        var display=string.Join(" ",result.ToString(),
            result.Dispatch!.ToString(),result.Dispatch.Preview.ToString(),
            JsonSerializer.Serialize(result.Dispatch.Preview));
        Assert.DoesNotContain(promptText,display);
        Assert.DoesNotContain("example-model",display);
        Assert.DoesNotContain(submission.ClientMessageId,display);
        Assert.DoesNotContain("Authorization",display);
        Assert.DoesNotContain("cookie",display,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(promptText.Length,result.Dispatch.Preview.PromptLength);

        // Only the explicit typed arguments expose message text to an adapter.
        Assert.Equal(promptText,result.Dispatch.GetArgumentsForAdapter().Text);
        Assert.DoesNotContain(promptText,result.Dispatch.GetArgumentsForAdapter().ToString());
    }

    [Fact]
    public void ArgumentsRemainProtocolNeutralAndContainNoEndpointOrCredentials()
    {
        var submission=OutboundMessagePreparation.ForNew("hi").Message!;
        var result=Builder.ForNew(submission,NewParent(),Model(),Now);
        var args=result.Dispatch!.GetArgumentsForAdapter();
        Assert.Equal("hi",args.Text);
        var names=typeof(Builder.AdapterArguments).GetProperties()
            .Select(p=>p.Name).ToArray();
        Assert.Equal(new[]{
            "ClientMessageId","ConversationId","ParentReference",
            "Text","ModelId","QualitySetting"},names);
        Assert.DoesNotContain("Headers",names);
        Assert.DoesNotContain("Url",names);
        Assert.DoesNotContain("Cookie",names);
        Assert.DoesNotContain("Authorization",names);
        Assert.DoesNotContain("HttpMethod",names);
    }
}
