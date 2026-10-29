using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neko;

/// <summary>
/// Pure preparation of options for the signed-in page's own ChatGPT submitter.
/// No network, WebView2, authentication, endpoint or session-cookie handling.
/// Based on the frozen adapter's high-level submit options, not a replay of
/// an observed first-party HTTP request.
/// </summary>
internal static class ChatGptNativeRequestPreparation
{
    private static readonly TimeSpan MaxParentAge=TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxObservedReplyAge=TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxModelAge=TimeSpan.FromMinutes(5);
    private static readonly Regex ModelPattern=new(
        "^[a-zA-Z0-9_.-]{1,90}$",RegexOptions.CultureInvariant);

    internal enum ParentEvidenceSource
    {
        FreshServerCurrentNode,
        ObservedCompletedReply
    }

    /// <summary>
    /// Collected by a read-only preflight, never from a locally saved
    /// transcript. The builder checks shape and freshness; the adapter must
    /// independently establish that the claimed evidence is authentic.
    /// </summary>
    internal sealed record FollowUpContext(
        string ConversationId,
        string ParentMessageId,
        ParentEvidenceSource Source,
        bool ConversationStreamComplete,
        DateTimeOffset ObservedAtUtc);

    /// <summary>
    /// The calling adapter must resolve the selection against the currently
    /// signed-in account's first-party model catalog before constructing it.
    /// </summary>
    internal sealed record ModelChoice(
        string Slug,string? ThinkingEffort,DateTimeOffset ObservedAtUtc);

    /// <summary>
    /// Mint once per intended new conversation, then retain alongside the
    /// prepared message. A dry-run must not mint a different parent on every
    /// render. This is not server acknowledgement or retry permission.
    /// </summary>
    internal sealed class NewConversationContext
    {
        internal string ParentMessageId {get;}
        private NewConversationContext(string parentMessageId)=>
            ParentMessageId=parentMessageId;
        internal static NewConversationContext Create()=>
            new(Guid.NewGuid().ToString("D"));
    }

    internal sealed record SafePreview(
        bool CreatesConversation,
        int PromptLength,
        bool HasModel,
        bool HasThinkingEffort,
        bool HasVerifiedParent,
        string ParentSource)
    {
        public override string ToString()=>
            "ChatGPT request preview (content, identifiers and credentials omitted)";
    }

    internal sealed class PreparedDispatch
    {
        // The only place where the prompt and provider identifiers are
        // serialized together. Never log or persist this string.
        private readonly string _optionsJson;
        internal string SerializeOptionsForFirstPartySubmit()=>_optionsJson;
        internal SafePreview Preview {get;}
        internal string ClientMessageId {get;}

        internal PreparedDispatch(
            PreparedOutboundMessage submission,string optionsJson,SafePreview preview)
        {
            ClientMessageId=submission.ClientMessageId;
            _optionsJson=optionsJson;
            Preview=preview;
        }

        public override string ToString()=>
            "Prepared ChatGPT dispatch (sensitive fields redacted)";
    }

    internal sealed record Result(PreparedDispatch? Dispatch,string ErrorCode="")
    {
        internal bool Ready=>Dispatch is not null;
        public override string ToString()=>Ready?
            "ChatGPT dispatch ready (redacted)":"ChatGPT dispatch blocked: "+ErrorCode;
    }

    internal static Result ForNew(
        PreparedOutboundMessage submission,NewConversationContext? context,
        ModelChoice? model,DateTimeOffset nowUtc)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           !submission.CreatesConversation)
            return new(null,"invalid_submission");
        if(context is null)
            return new(null,"parent_unavailable");
        if(!ValidModel(model,nowUtc))
            return new(null,"model_unavailable");
        return Build(submission,context.ParentMessageId,model!,
            "new_conversation");
    }

    internal static Result ForExisting(
        PreparedOutboundMessage submission,FollowUpContext? context,
        ModelChoice? model,DateTimeOffset nowUtc)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           submission.CreatesConversation)
            return new(null,"invalid_submission");
        if(!ValidModel(model,nowUtc))
            return new(null,"model_unavailable");
        if(context is null ||
           context.ConversationId!=submission.ConversationId ||
           !Guid.TryParseExact(context.ConversationId,"D",out _) ||
           !Guid.TryParseExact(context.ParentMessageId,"D",out _) ||
           !context.ConversationStreamComplete)
            return new(null,"verified_parent_unavailable");

        var maxAge=context.Source switch
        {
            ParentEvidenceSource.FreshServerCurrentNode=>MaxParentAge,
            ParentEvidenceSource.ObservedCompletedReply=>MaxObservedReplyAge,
            _=>TimeSpan.Zero
        };
        if(!Fresh(context.ObservedAtUtc,nowUtc,maxAge))
            return new(null,"verified_parent_stale");

        return Build(submission,context.ParentMessageId,model!,
            context.Source==ParentEvidenceSource.FreshServerCurrentNode?
                "server_current_node":"completed_stream");
    }

    private static Result Build(
        PreparedOutboundMessage submission,string parentMessageId,
        ModelChoice model,string parentSource)
    {
        // These are the options to the site's native submit function,
        // not the shape of its HTTP request. The site owns HTTP serialization.
        object options=model.ThinkingEffort is {Length:>0} effort
            ?new
            {
                conversationId=submission.ConversationId,
                parentMessageId,
                prompt=submission.Text,
                model=model.Slug,
                thinkingEffort=effort,
                isTemporaryChat=false
            }
            :new
            {
                conversationId=submission.ConversationId,
                parentMessageId,
                prompt=submission.Text,
                model=model.Slug,
                isTemporaryChat=false
            };

        var serialized=JsonSerializer.Serialize(options);
        var preview=new SafePreview(
            submission.CreatesConversation,
            submission.Text.Length,
            true,
            model.ThinkingEffort is {Length:>0},
            !submission.CreatesConversation,
            parentSource);
        return new(new PreparedDispatch(submission,serialized,preview));
    }

    private static bool ValidModel(ModelChoice? model,DateTimeOffset nowUtc)
    {
        if(model is null || string.IsNullOrWhiteSpace(model.Slug) ||
           !ModelPattern.IsMatch(model.Slug) ||
           !Fresh(model.ObservedAtUtc,nowUtc,MaxModelAge))
            return false;
        return model.ThinkingEffort is null ||
            model.ThinkingEffort is "zero" or "min" or "standard" or
                "extended" or "xhigh" or "max" or "ultra";
    }

    private static bool Fresh(
        DateTimeOffset checkedAt,DateTimeOffset now,TimeSpan maxAge)
    {
        if(checkedAt.Offset!=TimeSpan.Zero || now.Offset!=TimeSpan.Zero ||
           checkedAt==default || now==default)return false;
        var age=now-checkedAt;
        return age>=TimeSpan.Zero && age<=maxAge;
    }
}
