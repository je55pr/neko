using System.Text.RegularExpressions;

namespace Neko;

/// <summary>
/// Provider-neutral preparation of validated conversation dispatch inputs.
/// No network access, web runtime, credentials or provider serialization.
/// An adapter separately maps these values to its own request protocol.
/// </summary>
internal static class NativeConversationRequestPreparation
{
    private static readonly TimeSpan MaxParentAge=TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxObservedReplyAge=TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxModelAge=TimeSpan.FromMinutes(5);
    private static readonly Regex ModelPattern=new(
        "^[a-zA-Z0-9_.-]{1,90}$",RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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
    /// The adapter supplies a current, verified model selection.
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
            "provider request preview (content, identifiers and credentials omitted)";
    }

    /// <summary>
    /// Typed, protocol-independent input. This is not an HTTP body or an
    /// instruction to transmit. Adapters decide how to serialize it.
    /// </summary>
    internal sealed record AdapterArguments(
        string ClientMessageId,
        string? ConversationId,
        string ParentReference,
        string Text,
        string ModelId,
        string? QualitySetting)
    {
        public override string ToString()=>
            "Adapter arguments (content and identifiers redacted)";
    }

    internal sealed class PreparedDispatch
    {
        private readonly AdapterArguments _arguments;
        internal AdapterArguments GetArgumentsForAdapter()=>_arguments;
        internal SafePreview Preview {get;}
        internal string ClientMessageId=>_arguments.ClientMessageId;

        internal PreparedDispatch(
            AdapterArguments arguments,SafePreview preview)
        {
            _arguments=arguments;
            Preview=preview;
        }

        public override string ToString()=>
            "Prepared dispatch (sensitive fields redacted)";
    }

    internal sealed record Result(PreparedDispatch? Dispatch,string ErrorCode="")
    {
        internal bool Ready=>Dispatch is not null;
        public override string ToString()=>Ready?
            "provider dispatch ready (redacted)":"provider dispatch blocked: "+ErrorCode;
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
        var arguments=new AdapterArguments(
            submission.ClientMessageId,submission.ConversationId,
            parentMessageId,submission.Text,model.Slug,
            model.ThinkingEffort);
        var preview=new SafePreview(
            submission.CreatesConversation,
            submission.Text.Length,
            true,
            model.ThinkingEffort is {Length:>0},
            !submission.CreatesConversation,
            parentSource);
        return new(new PreparedDispatch(arguments,preview));
    }

    private static bool ValidModel(ModelChoice? model,DateTimeOffset nowUtc)
    {
        if(model is null || string.IsNullOrWhiteSpace(model.Slug) ||
           !ModelPattern.IsMatch(model.Slug) ||
           !Fresh(model.ObservedAtUtc,nowUtc,MaxModelAge))
            return false;
        return model.ThinkingEffort is null ||
            (model.ThinkingEffort.Length<=48 &&
             Regex.IsMatch(model.ThinkingEffort,
                "^[a-zA-Z0-9_.-]+$",RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)));
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
