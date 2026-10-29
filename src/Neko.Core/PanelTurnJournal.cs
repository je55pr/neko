using System.IO;
using System.Text.Json;

namespace Neko;

// Keeps previously accepted turns visible while saved history is unavailable.
// The local file is only a display cache. It never authorizes send preflights
// or confirms an uncertain submission; server history remains authoritative.
internal sealed class PanelTurnJournal
{
    internal sealed record ConfirmedReply(string ConversationId,string MessageId,
        string Text,DateTimeOffset ObservedAt,bool Completed,bool HasError=false);

    private sealed class Turn
    {
        public required string LocalId;
        public required string Prompt;
        public required double Created;
        public required string BeforeReply;
        public string ReplyId="";
        public string ReplyText="";
        public double ReplyCreated;
    }

    private sealed class Chat
    {
        public readonly List<Turn> Turns=[];
        public int Version;
    }

    private sealed class PersistedTurn
    {
        public string LocalId {get;set;}="";
        public string Prompt {get;set;}="";
        public double Created {get;set;}
        public string BeforeReply {get;set;}="";
        public string ReplyId {get;set;}="";
        public string ReplyText {get;set;}="";
        public double ReplyCreated {get;set;}
    }

    private sealed class PersistedState
    {
        public int Schema {get;set;}=1;
        public Dictionary<string,List<PersistedTurn>> Turns {get;set;}=[];
        public Dictionary<string,List<ConversationMessage>> History {get;set;}=[];
    }

    private readonly Dictionary<string,Chat> _chats=new(StringComparer.Ordinal);
    private readonly Dictionary<string,ConversationMessage[]> _history=
        new(StringComparer.Ordinal);
    private readonly object _gate=new();
    private string? _storageFile;
    public bool StorageHealthy {get;private set;}=true;

    public void OpenLocalCache(string path)
    {
        lock(_gate)
        {
            _storageFile=path;
            _chats.Clear();
            _history.Clear();
            StorageHealthy=true;
            try
            {
                if(!File.Exists(path))return;
                var file=new FileInfo(path);
                if(file.Length>16_000_000)throw new InvalidDataException(
                    "Local conversation cache exceeded its size limit.");
                var saved=JsonSerializer.Deserialize<PersistedState>(
                    File.ReadAllText(path));
                if(saved?.Schema!=1)throw new InvalidDataException(
                    "Unknown local conversation cache schema.");
                foreach(var (id,turns) in (saved.Turns??[]).Take(100))
                {
                    if(!Guid.TryParse(id,out _))continue;
                    var chat=new Chat();
                    foreach(var turn in (turns??[]).TakeLast(80))
                    {
                        if(turn is null || turn.LocalId is null ||
                           !turn.LocalId.StartsWith(
                             "neko-local:",StringComparison.Ordinal) ||
                           turn.Prompt is null || turn.Prompt.Length>24000 ||
                           turn.ReplyText is null ||
                           turn.ReplyText.Length>250000 ||
                           !double.IsFinite(turn.Created) ||
                           !double.IsFinite(turn.ReplyCreated) ||
                           turn.Created<=0)continue;
                        chat.Turns.Add(new Turn {
                            LocalId=turn.LocalId,Prompt=turn.Prompt,
                            Created=turn.Created,
                            BeforeReply=turn.BeforeReply??"",
                            ReplyId=turn.ReplyId??"",
                            ReplyText=turn.ReplyText,
                            ReplyCreated=turn.ReplyCreated
                        });
                    }
                    if(chat.Turns.Count>0)
                    {
                        chat.Version=1;
                        _chats[id]=chat;
                    }
                }
                foreach(var (id,messages) in (saved.History??[]).Take(100))
                {
                    if(!Guid.TryParse(id,out _))continue;
                    var safe=SafeHistory(messages??[]).ToArray();
                    if(safe.Length>0)_history[id]=safe;
                }
            }
            catch(Exception ex) when(ex is IOException or JsonException or
                UnauthorizedAccessException or InvalidDataException)
            {
                StorageHealthy=false;
            }
        }
    }

    // Display-only check. Never use this to authorize a POST or to infer a
    // current server parent after restart.
    public bool HasLocalDisplay(string id)
    {
        lock(_gate)
            return _history.TryGetValue(id,out var history) &&
                   history.Length>0 ||
                   _chats.TryGetValue(id,out var chat) &&
                   chat.Turns.Any(t=>t.Prompt.Length>0 ||
                                      t.ReplyText.Length>0);
    }

    public int Version(string id)
    {
        lock(_gate)return _chats.TryGetValue(id,out var chat)
            ?chat.Version:(_history.ContainsKey(id)?1:0);
    }

    private static IEnumerable<ConversationMessage> SafeHistory(
        IEnumerable<ConversationMessage> items)=>
        items.Where(m=>m is not null &&
            (m.Role=="user" ||
             m.Role=="assistant" &&
                ((m.Kind=="message" && (m.Channel is "" or "final")) ||
                 // Public progress is real user-visible conversation text.
                 // Never persist analysis/reasoning or tool arguments here.
                 (m.Kind=="progress" && m.Channel=="commentary"))) &&
            m.Text is not null && m.Text.Length<=250000)
            .TakeLast(120).Select(m=>new ConversationMessage {
                Id=m.Id,ParentId=m.ParentId,Role=m.Role,
                Status=m.Status,Text=m.Text,Kind=m.Kind,Title=m.Title,
                Channel=m.Channel,Recipient=m.Recipient,Created=m.Created
            });

    // Store only server-confirmed, public-facing history. This snapshot is
    // for display when a future startup's GET returns HTTP 429; it is not
    // used for sending, authorization or uncertain-message reconciliation.
    public void RememberHistory(string id,
        IReadOnlyList<ConversationMessage> messages)
    {
        if(!Guid.TryParse(id,out _) || messages.Count==0)return;
        lock(_gate)
        {
            var safe=SafeHistory(messages).ToArray();
            if(safe.Length==0)return;
            _history[id]=safe;
            SaveLocked();
        }
    }

    // Deleting a conversation from the service must also erase its local
    // display backup. Merely removing a workspace panel does not delete it.
    public void ForgetChat(string id)
    {
        lock(_gate)
        {
            var removed=_chats.Remove(id);
            removed=_history.Remove(id)||removed;
            if(removed)SaveLocked();
        }
    }

    public void Accepted(string id,string prompt,string previousReply="")
    {
        if(!Guid.TryParse(id,out _) || prompt.Length>24000)return;
        lock(_gate)
        {
            if(!_chats.TryGetValue(id,out var chat))
                _chats[id]=chat=new Chat();
            var clock=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
            var preceding=chat.Turns.LastOrDefault();
            var next=preceding is null?clock:Math.Max(clock,
                Math.Max(preceding.Created,preceding.ReplyCreated)+0.002);
            chat.Turns.Add(new Turn {
                LocalId="neko-local:"+Guid.NewGuid().ToString("N"),
                Prompt=prompt,
                Created=next,
                BeforeReply=previousReply
            });
            if(chat.Turns.Count>80)chat.Turns.RemoveAt(0);
            chat.Version++;
            SaveLocked();
        }
    }
    public bool Observe(ConfirmedReply output)
    {
        if(!output.Completed || output.HasError || output.Text.Length>250000 ||
           string.IsNullOrWhiteSpace(output.Text) ||
           string.IsNullOrWhiteSpace(output.MessageId))return false;
        lock(_gate)
        {
            if(!_chats.TryGetValue(output.ConversationId,out var chat))
                return false;
            var turn=chat.Turns.LastOrDefault(x=>x.ReplyId.Length==0);
            if(turn is null || turn.BeforeReply==output.MessageId ||
               chat.Turns.Any(x=>x.ReplyId==output.MessageId) ||
               output.ObservedAt.ToUnixTimeMilliseconds()/1000.0 <
                   turn.Created-45)return false;
            turn.ReplyId=output.MessageId;
            turn.ReplyText=output.Text;
            turn.ReplyCreated=Math.Max(turn.Created+0.001,
                output.ObservedAt.ToUnixTimeMilliseconds()/1000.0);
            chat.Version++;
            SaveLocked();
            return true;
        }
    }
    private void SaveLocked()
    {
        if(_storageFile is not {Length:>0} path)return;
        try
        {
            var stored=new PersistedState {
                Turns=_chats.Take(100).ToDictionary(
                    pair=>pair.Key,
                    pair=>pair.Value.Turns.TakeLast(80)
                        .Select(turn=>new PersistedTurn {
                            LocalId=turn.LocalId,Prompt=turn.Prompt,
                            Created=turn.Created,BeforeReply=turn.BeforeReply,
                            ReplyId=turn.ReplyId,ReplyText=turn.ReplyText,
                            ReplyCreated=turn.ReplyCreated
                        }).ToList(),StringComparer.Ordinal),
                History=_history.Take(100).ToDictionary(
                    pair=>pair.Key,
                    pair=>SafeHistory(pair.Value).ToList(),
                    StringComparer.Ordinal)
            };
            var json=JsonSerializer.Serialize(stored);
            if(json.Length>16_000_000)throw new IOException(
                "Local transcript cache exceeded its size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp=path+".tmp";
            File.WriteAllText(temp,json);
            File.Move(temp,path,true);
            StorageHealthy=true;
        }
        catch(Exception ex) when(ex is IOException or
            UnauthorizedAccessException or JsonException)
        {
            StorageHealthy=false;
        }
    }

    public ConversationMessage[] Merge(string id,
        IReadOnlyList<ConversationMessage> server)
    {
        lock(_gate)
        {
            // A successful server read is authoritative. Only substitute
            // locally cached public history when this startup has NO saved
            // history available; the local entries never enter preflight.
            var baseMessages=server.Count>0?server:
                _history.TryGetValue(id,out var cached)?cached:[];
            if(!_chats.TryGetValue(id,out var chat) ||
               chat.Turns.Count==0)return baseMessages.ToArray();
            var projected=new List<ConversationMessage>(baseMessages);
            var used=new HashSet<string>(StringComparer.Ordinal);
            var serverIds=new HashSet<string>(baseMessages.Select(x=>x.Id),
                StringComparer.Ordinal);
            var users=baseMessages.Where(x=>x.Role=="user").ToArray();
            var causalPairs=new List<(string UserId,string ReplyId)>();
            foreach(var turn in chat.Turns)
            {
                var match=users.Where(x=>
                        !used.Contains(x.Id) &&
                        (x.Text==turn.Prompt ||
                         (turn.Prompt.Length>0 &&
                          x.Text.StartsWith(turn.Prompt+"\n",
                              StringComparison.Ordinal)) ||
                         (turn.Prompt.Length==0 &&
                          x.Text.Contains("[Image attachment]",
                              StringComparison.Ordinal))))
                    .OrderBy(x=>x.Created>0
                        ?Math.Abs(x.Created-turn.Created)
                        :double.MaxValue)
                    .FirstOrDefault();
                var matched=match is not null &&
                    (match.Created<=0 ||
                     Math.Abs(match.Created-turn.Created)<180);
                if(matched)used.Add(match!.Id);
                else projected.Add(new ConversationMessage {
                    Id=turn.LocalId,Role="user",Kind="local_user",
                    Status="finished_successfully",
                    Text=turn.Prompt.Length>0?turn.Prompt:"[Attachment sent]",
                    Created=turn.Created
                });
                if(turn.ReplyId.Length>0)
                    causalPairs.Add((matched?match!.Id:turn.LocalId,
                        turn.ReplyId));
                if(turn.ReplyId.Length>0 &&
                   !serverIds.Contains(turn.ReplyId))
                    projected.Add(new ConversationMessage {
                        Id=turn.ReplyId,Role="assistant",Kind="message",
                        Channel="final",Status="finished_successfully",
                        Text=turn.ReplyText,Created=turn.ReplyCreated
                    });
            }
            var ordered=ConversationMessageOrder.Chronological(projected).ToList();
            // Old caches may already have been sorted by skewed server
            // timestamps. The journal knows the exact SSE reply ID for each
            // locally accepted prompt, so repair only those proven pairs.
            // This also handles a saved reply arriving before its user record.
            foreach(var (userId,replyId) in causalPairs)
            {
                var userIndex=ordered.FindIndex(m=>m.Id==userId);
                var replyIndex=ordered.FindIndex(m=>m.Id==replyId);
                if(userIndex<0 || replyIndex<0 || userIndex<replyIndex)
                    continue;
                var user=ordered[userIndex];
                ordered.RemoveAt(userIndex);
                ordered.Insert(replyIndex,user);
            }
            return ordered.ToArray();
        }
    }
}
