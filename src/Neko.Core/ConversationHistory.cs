namespace Neko;

/// <summary>In-memory view of a conversation from an authenticated provider.
/// Rich tool/media content remains owned by the provider until typed renderers exist.</summary>
internal sealed class ConversationHistory
{
    public int Status {get;set;}
    public string ConversationId {get;set;}="";
    public string CurrentNode {get;set;}="";
    public string Title {get;set;}="";
    public List<ConversationMessage> Messages {get;set;}=[];
}
internal sealed class ConversationMessage
{
    public string Id {get;set;}="";
    public string ParentId {get;set;}="";
    public string Role {get;set;}="";
    public string Status {get;set;}="";
    public string Text {get;set;}="";
    // Typed, user-visible history blocks. Never store private analysis text.
    public string Kind {get;set;}="message";
    public string Title {get;set;}="";
    public string Channel {get;set;}="";
    public string Recipient {get;set;}="";
    public double Created {get;set;}
}

// Conversation providers can return tree/traversal order and message
// timestamps are NOT always causal. In particular, an assistant may have a
// creation time earlier than the user prompt it answers. Respect a coherent
// source sequence when its assistant activity follows its user, rather than
// reversing a turn by sorting every message independently.
internal static class ConversationMessageOrder
{
    private sealed class Turn(ConversationMessage user)
    {
        public ConversationMessage User {get;}=user;
        public List<ConversationMessage> Messages {get;}=[user];
    }

    private static bool Dated(ConversationMessage message)=>
        message.Created>0 && double.IsFinite(message.Created);

    public static ConversationMessage[] Chronological(
        IReadOnlyList<ConversationMessage> messages)
    {
        if(messages.Count<2)return messages.ToArray();

        var turns=new List<Turn>();
        var byId=new Dictionary<string,ConversationMessage>(StringComparer.Ordinal);
        var byUserId=new Dictionary<string,Turn>(StringComparer.Ordinal);
        foreach(var message in messages)
        {
            if(!string.IsNullOrEmpty(message.Id))
                byId.TryAdd(message.Id,message);
            if(message.Role!="user")continue;
            var turn=new Turn(message);
            turns.Add(turn);
            if(!string.IsNullOrEmpty(message.Id))
                byUserId.TryAdd(message.Id,turn);
        }
        if(turns.Count==0)return SortDatedSlots(messages);

        // Compare all dated user turns, ignoring intervening undated turns.
        var reordered=false;
        var previous=double.NegativeInfinity;
        foreach(var turn in turns)
        {
            if(!Dated(turn.User))continue;
            reordered|=turn.User.Created<previous;
            previous=turn.User.Created;
        }

        var ownerCache=new Dictionary<string,Turn?>(StringComparer.Ordinal);
        var leading=new List<ConversationMessage>();
        Turn? active=null;
        var nextUser=0;
        foreach(var message in messages)
        {
            if(message.Role=="user")
            {
                active=turns[nextUser++];
                continue;
            }
            var owner=ResolveOwner(message.ParentId??"",byId,byUserId,ownerCache);
            if(owner is not null)owner.Messages.Add(message);
            else if(active is not null)active.Messages.Add(message);
            else leading.Add(message);
        }

        // Sort dated user turns into dated slots. Undated turns stay put.
        var indexed=turns.Select((turn,index)=>new {turn,index})
            .Where(x=>Dated(x.turn.User)).ToArray();
        var dated=indexed.OrderBy(x=>x.turn.User.Created)
            .ThenBy(x=>x.index).Select(x=>x.turn).ToArray();
        var ordered=turns.ToArray();
        for(var i=0;i<dated.Length;i++)
            ordered[indexed[i].index]=dated[i];

        return leading.Concat(ordered.SelectMany(turn=>OrderTurn(turn,reordered)))
            .ToArray();
    }

    private static Turn? ResolveOwner(
        string parentId,
        IReadOnlyDictionary<string,ConversationMessage> byId,
        IReadOnlyDictionary<string,Turn> byUserId,
        Dictionary<string,Turn?> cache)
    {
        var visited=new HashSet<string>(StringComparer.Ordinal);
        var path=new List<string>();
        Turn? owner=null;
        while(parentId.Length>0 && visited.Add(parentId))
        {
            if(byUserId.TryGetValue(parentId,out owner))break;
            if(cache.TryGetValue(parentId,out owner))break;
            path.Add(parentId);
            if(!byId.TryGetValue(parentId,out var parent))break;
            parentId=parent.ParentId??"";
        }
        foreach(var id in path)cache[id]=owner;
        return owner;
    }

    private static ConversationMessage[] OrderTurn(Turn turn,bool reordered)
    {
        var tail=turn.Messages.Skip(1).ToArray();
        if(tail.Length<2)return turn.Messages.ToArray();

        // Only trust clocks within a turn when the provider traversal was
        // reversed or all activity clocks follow the prompt's clock.
        var hasPromptClockSkew=Dated(turn.User) &&
            tail.Any(x=>Dated(x) && x.Created<turn.User.Created);
        var previous=double.NegativeInfinity;
        var hasActivityInversion=false;
        foreach(var item in tail)
        {
            if(!Dated(item))continue;
            hasActivityInversion|=item.Created<previous;
            previous=item.Created;
        }
        var candidate=(reordered || (!hasPromptClockSkew && hasActivityInversion))
            ?SortDatedSlots(tail):tail;

        // A parent edge takes precedence over clocks and traversal position.
        // Stable topological ordering moves the child after its parent without
        // unnecessarily reordering unrelated activity.
        var positions=new Dictionary<string,int>(StringComparer.Ordinal);
        for(var i=0;i<candidate.Length;i++)
            if(!string.IsNullOrEmpty(candidate[i].Id))
                positions.TryAdd(candidate[i].Id,i);

        var pending=new int[candidate.Length];
        var children=new List<int>[candidate.Length];
        for(var i=0;i<candidate.Length;i++)children[i]=[];
        for(var i=0;i<candidate.Length;i++)
        {
            var parentId=candidate[i].ParentId??"";
            if(parentId.Length==0 || !positions.TryGetValue(parentId,out var parent) ||
               parent==i)continue;
            pending[i]++;
            children[parent].Add(i);
        }

        var ready=new SortedSet<int>();
        for(var i=0;i<candidate.Length;i++)
            if(pending[i]==0)ready.Add(i);
        var output=new List<ConversationMessage>(candidate.Length+1){turn.User};
        while(ready.Count>0)
        {
            var index=ready.Min;
            ready.Remove(index);
            output.Add(candidate[index]);
            foreach(var child in children[index])
                if(--pending[child]==0)ready.Add(child);
        }
        // Malformed cyclic parent references cannot lose history entries.
        if(output.Count<candidate.Length+1)
            for(var i=0;i<candidate.Length;i++)
                if(pending[i]>0)output.Add(candidate[i]);
        return output.ToArray();
    }

    private static ConversationMessage[] SortDatedSlots(
        IReadOnlyList<ConversationMessage> messages)
    {
        var dated=messages.Select((message,index)=>new {message,index})
            .Where(x=>Dated(x.message))
            .OrderBy(x=>x.message.Created).ThenBy(x=>x.index)
            .Select(x=>x.message).ToArray();
        if(dated.Length<2)return messages.ToArray();
        var next=0;
        return messages.Select(message=>Dated(message)
            ?dated[next++]:message).ToArray();
    }
}
