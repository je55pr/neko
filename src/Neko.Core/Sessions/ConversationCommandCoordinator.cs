namespace Neko;

/// <summary>
/// Serialises potentially consequential commands without coupling the public
/// application to any provider's request or authentication protocol.
/// </summary>
public sealed class ConversationCommandCoordinator(IConversationProvider provider)
{
    private sealed class Session
    {
        public bool Sending;
        public bool Stopping;
        public bool DeliveryUncertain;
        public SendReceipt? LastSend;
    }

    public sealed record State(
        bool Sending,bool Stopping,bool DeliveryUncertain,SendReceipt? LastSend);

    private readonly object _gate=new();
    private readonly Dictionary<string,Session> _sessions=new(StringComparer.Ordinal);
    private bool _creating;
    private bool _creationUncertain;

    public State GetState(string conversationId)
    {
        lock(_gate)
        {
            var session=GetOrCreate(conversationId);
            return new State(session.Sending,session.Stopping,
                session.DeliveryUncertain,session.LastSend);
        }
    }

    public Task<SendReceipt> SendAsync(
        string conversationId,string text,CancellationToken cancellation=default)
    {
        var prepared=OutboundMessagePreparation.ForExisting(conversationId,text);
        return prepared.Message is null
            ?Task.FromResult(Rejected("invalid_submission"))
            :SendAsync(prepared.Message,cancellation);
    }

    public async Task<SendReceipt> SendAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation=default)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           submission.ConversationId is null)
            return Rejected("invalid_submission");
        cancellation.ThrowIfCancellationRequested();
        var conversationId=submission.ConversationId;
        Session session;
        lock(_gate)
        {
            session=GetOrCreate(conversationId);
            if(session.Sending)return Rejected("send_in_progress");
            if(session.Stopping)return Rejected("stop_in_progress");
            if(session.DeliveryUncertain)
                return Rejected("previous_delivery_uncertain");
            session.Sending=true;
        }
        SendReceipt receipt;
        try
        {
            receipt=provider is IPreparedConversationProvider prepared
                ?await prepared.SendPreparedAsync(submission,cancellation)
                :await provider.SendAsync(conversationId,submission.Text,cancellation);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {
            // A cancelled local task cannot prove the remote send did not happen.
            receipt=new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(Exception)
        {
            // Never propagate provider exception strings into public diagnostics.
            receipt=new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        lock(_gate)
        {
            session.Sending=false;
            session.LastSend=receipt;
            if(receipt.Disposition==SendDisposition.Uncertain)
                session.DeliveryUncertain=true;
        }
        return receipt;
    }

    public async Task<StopReceipt> StopAsync(
        string conversationId,CancellationToken cancellation=default)
    {
        if(string.IsNullOrWhiteSpace(conversationId))
            return new StopReceipt(StopDisposition.NotActive,"invalid_conversation");
        cancellation.ThrowIfCancellationRequested();
        Session session;
        lock(_gate)
        {
            session=GetOrCreate(conversationId);
            if(session.Sending)
                return new StopReceipt(StopDisposition.NotActive,"send_in_progress");
            if(session.Stopping)
                return new StopReceipt(StopDisposition.NotActive,"stop_in_progress");
            session.Stopping=true;
        }
        StopReceipt receipt;
        try
        {
            receipt=await provider.StopAsync(conversationId,cancellation);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {
            receipt=new StopReceipt(StopDisposition.Uncertain,"stop_unknown");
        }
        catch(Exception)
        {
            receipt=new StopReceipt(StopDisposition.Uncertain,"stop_unknown");
        }
        lock(_gate)session.Stopping=false;
        return receipt;
    }

    public Task<SendReceipt> CreateAsync(
        string text,CancellationToken cancellation=default)
    {
        var prepared=OutboundMessagePreparation.ForNew(text);
        return prepared.Message is null
            ?Task.FromResult(Rejected("invalid_submission"))
            :CreateAsync(prepared.Message,cancellation);
    }

    public async Task<SendReceipt> CreateAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation=default)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           !submission.CreatesConversation)
            return Rejected("invalid_submission");
        cancellation.ThrowIfCancellationRequested();
        lock(_gate)
        {
            if(_creating)return Rejected("create_in_progress");
            if(_creationUncertain)return Rejected("previous_creation_uncertain");
            _creating=true;
        }
        SendReceipt receipt;
        try
        {
            receipt=provider is IPreparedConversationProvider prepared
                ?await prepared.CreatePreparedAsync(submission,cancellation)
                :await provider.CreateAsync(submission.Text,cancellation);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {
            receipt=new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(Exception)
        {
            receipt=new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        lock(_gate)
        {
            _creating=false;
            if(receipt.Disposition==SendDisposition.Uncertain)
                _creationUncertain=true;
        }
        return receipt;
    }

    // The caller must reconcile with authoritative provider history before
    // releasing a delivery-uncertain latch.
    public bool ConfirmReconciled(string conversationId)
    {
        if(string.IsNullOrWhiteSpace(conversationId))return false;
        lock(_gate)
        {
            var session=GetOrCreate(conversationId);
            if(session.Sending||session.Stopping)return false;
            if(!session.DeliveryUncertain)return false;
            session.DeliveryUncertain=false;
            return true;
        }
    }

    public bool ConfirmCreationReconciled()
    {
        lock(_gate)
        {
            if(_creating||!_creationUncertain)return false;
            _creationUncertain=false;
            return true;
        }
    }

    private static SendReceipt Rejected(string reason)=>
        new(SendDisposition.Rejected,reason);

    private Session GetOrCreate(string id)
    {
        if(!_sessions.TryGetValue(id,out var session))
        {
            session=new Session();
            _sessions[id]=session;
        }
        return session;
    }
}
