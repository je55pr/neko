using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Neko;

/// <summary>
/// The only conversation-facing boundary used by the desktop window.
/// Providers deal in public messages, never browser transport details.
/// </summary>
internal sealed class ConversationWorkspaceHost : IDisposable
{
    internal sealed class Panel(string id)
    {
        public string Id { get; } = id;
        public string Title { get; set; } = "Conversation";
        public List<VisibleConversationMessage> Messages { get; set; } = [];
        public int HistoryRevision { get; set; }
        public string Draft { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Authorized { get; set; } = true;
        public bool Sending { get; set; }
        public bool Generating { get; set; }
        public bool Uncertain { get; set; }
        public bool HistoryLoading { get; set; }
        public bool StopPending { get; set; }
        public LiveReply? Live { get; set; }
    }

    internal sealed class LiveReply
    {
        public string MessageId { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Completed { get; set; }
        public string Error { get; set; } = "";
        public int Sequence { get; set; }
        public object[] Activities { get; set; } = [];
    }

    private readonly IConversationProvider _provider;
    private readonly ConversationCommandCoordinator _commands;
    private readonly OfflineBoardStore _boards;
    private readonly object _gate = new();
    private readonly Dictionary<string,Panel> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<string,CancellationTokenSource> _watches = new(StringComparer.Ordinal);
    private List<ConversationSummary> _library = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _libraryLoading;
    private string _libraryError = "";
    private int _libraryRevision;
    private bool _creating;
    private string _newChatError = "";
    private bool _newChatUncertain;
    private string _createdChatId = "";
    public event Action? Changed;

    public ConversationWorkspaceHost(IConversationProvider provider,OfflineBoardStore boards)
    {
        _provider=provider;
        _commands=new ConversationCommandCoordinator(provider);
        _boards=boards;
    }

    private void Notify() => Changed?.Invoke();

    public object Snapshot()
    {
        lock(_gate)
        {
            var assigned=_boards.ConversationsForActiveBoard();
            return new
            {
                activeBoardId=_boards.ActiveBoardId,
                visible=true,
                boards=_boards.Boards.Select(x=>new
                {
                    id=x.Id,name=x.Name,
                    count=(x.ConversationIds?.Count ?? 0),
                    conversationIds=x.ConversationIds ?? []
                }).ToArray(),
                panels=assigned.Where(_panels.ContainsKey).Select(id=>_panels[id]).Select(p=>new
                {
                    id=p.Id,title=p.Title,
                    messages=p.Messages.Select(m=>new
                    {
                        id=m.Id,
                        role=m.Role==VisibleMessageRole.User?"user":
                            m.Role==VisibleMessageRole.Activity?"activity":"assistant",
                        text=m.Text,kind=m.Kind,timestamp=m.Timestamp
                    }).ToArray(),
                    p.HistoryRevision,p.Draft,p.Status,p.Authorized,
                    p.Sending,p.Generating,p.Uncertain,p.HistoryLoading,
                    p.StopPending,p.Live
                }).ToArray(),
                capacity=OfflineBoardStore.MaxPanelsPerBoard,
                creatingChat=_creating,newChatError=_newChatError,
                newChatUncertain=_newChatUncertain,createdChatId=_createdChatId
            };
        }
    }

    public object Library()
    {
        lock(_gate)
            return new
            {
                revision=_libraryRevision,
                loading=_libraryLoading,
                loaded=!_libraryLoading,
                error=_libraryError,
                chats=_library.Select(c=>new {id=c.Id,title=c.Title}).ToArray(),
                total=_library.Count,
                hasMore=false
            };
    }

    public async Task RefreshLibraryAsync()
    {
        lock(_gate){_libraryLoading=true;_libraryError="";}
        Notify();
        try
        {
            var result=await _provider.BrowseAsync(_lifetime.Token);
            lock(_gate)
            {
                _library=result.Where(x=>!string.IsNullOrWhiteSpace(x.Id))
                    .Take(500).ToList();
                _libraryRevision++;
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception)
        {
            lock(_gate)_libraryError="Conversation list unavailable.";
        }
        finally
        {
            lock(_gate)_libraryLoading=false;
            Notify();
        }
    }

    public async Task RestoreBoardAsync()
    {
        foreach(var id in _boards.ConversationsForActiveBoard())
            await OpenPanelAsync(id,addToBoard:false);
    }

    public async Task OpenPanelAsync(string? id,bool addToBoard=true)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>256)return;
        lock(_gate)
        {
            if(addToBoard&&!_boards.AddConversation(id))return;
            if(!_panels.ContainsKey(id))_panels[id]=new Panel(id);
        }
        Notify();
        await RefreshPanelAsync(id);
        StartWatching(id);
    }

    public void RemovePanel(string? id)
    {
        if(string.IsNullOrWhiteSpace(id))return;
        lock(_gate) _boards.RemoveConversation(id);
        Notify();
    }

    public async Task RefreshPanelAsync(string? id)
    {
        if(id is null)return;
        lock(_gate)
        {
            if(!_panels.TryGetValue(id,out var p))return;
            p.HistoryLoading=true;
        }
        Notify();
        try
        {
            var history=await _provider.ReadAsync(id,_lifetime.Token);
            lock(_gate)
            {
                if(!_panels.TryGetValue(id,out var p))return;
                if(history is not null)
                {
                    p.Title=history.Title;
                    p.Messages=history.Messages.TakeLast(2000).ToList();
                    p.HistoryRevision++;
                    // Saved reply is authoritative over transient live text.
                    if(p.Live is {Completed:true} live &&
                       p.Messages.Any(m=>m.Id==live.MessageId))
                        p.Live=null;
                }
                else p.Status="Conversation history unavailable.";
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){}
        catch(Exception)
        {
            lock(_gate)
                if(_panels.TryGetValue(id,out var p))
                    p.Status="Conversation history unavailable.";
        }
        finally
        {
            lock(_gate)
                if(_panels.TryGetValue(id,out var p))p.HistoryLoading=false;
            Notify();
        }
    }

    public void SetDraft(string? id,string? draft)
    {
        if(id is null)return;
        lock(_gate)
            if(_panels.TryGetValue(id,out var p))p.Draft=(draft??"")[..Math.Min((draft??"").Length,24000)];
    }

    public async Task SendAsync(string? id,string? text)
    {
        if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(text))return;
        lock(_gate)
        {
            if(!_panels.TryGetValue(id,out var p) || p.Sending || p.Uncertain)return;
            p.Sending=true;
            p.Generating=true;
            p.Live=null;
            p.Status="Sending message…";
        }
        Notify();
        SendReceipt receipt;
        try { receipt=await _commands.SendAsync(id,text,_lifetime.Token); }
        catch(OperationCanceledException){return;}
        lock(_gate)
        {
            if(!_panels.TryGetValue(id,out var p))return;
            p.Sending=false;
            p.Uncertain=receipt.Disposition==SendDisposition.Uncertain;
            if(receipt.Disposition!=SendDisposition.Accepted)p.Generating=false;
            p.Status=receipt.Disposition switch
            {
                SendDisposition.Accepted=>"sse_accepted",
                SendDisposition.Uncertain=>"Delivery uncertain. Verify history before retrying.",
                _=>"Not sent: "+SafeReason(receipt.Reason)
            };
            if(receipt.Disposition==SendDisposition.Accepted)p.Draft="";
        }
        Notify();
        if(receipt.Disposition==SendDisposition.Accepted)
            await RefreshPanelAsync(id);
    }

    public async Task CreateAsync(string? text)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        lock(_gate)
        {
            if(_creating||_newChatUncertain)return;
            _creating=true;_createdChatId="";_newChatError="";
        }
        Notify();
        SendReceipt receipt;
        try {receipt=await _commands.CreateAsync(text,_lifetime.Token);}
        catch(OperationCanceledException){return;}
        lock(_gate)
        {
            _creating=false;
            _newChatUncertain=receipt.Disposition==SendDisposition.Uncertain;
            if(receipt.Disposition==SendDisposition.Accepted &&
               !string.IsNullOrWhiteSpace(receipt.ConversationId))
                _createdChatId=receipt.ConversationId;
            else _newChatError=receipt.Disposition==SendDisposition.Uncertain?
                "Creation uncertain. Check provider history before retrying.":
                "New conversation unavailable: "+SafeReason(receipt.Reason);
        }
        Notify();
        if(!string.IsNullOrWhiteSpace(_createdChatId))
        {
            var id=_createdChatId;
            lock(_gate)
            {
                if(!_panels.TryGetValue(id,out var p))
                    _panels[id]=new Panel(id) {Generating=true};
                else p.Generating=true;
            }
            await RefreshLibraryAsync();
            await OpenPanelAsync(id);
        }
    }

    public async Task StopAsync(string? id)
    {
        if(string.IsNullOrWhiteSpace(id))return;
        lock(_gate)
            if(_panels.TryGetValue(id,out var p))p.StopPending=true;
        Notify();
        try
        {
            var receipt=await _commands.StopAsync(id,_lifetime.Token);
            lock(_gate)
                if(_panels.TryGetValue(id,out var p))
                {
                    p.StopPending=false;
                    p.Status=receipt.Disposition switch
                    {
                        StopDisposition.Uncertain=>"Stop uncertain. Refresh history to verify.",
                        StopDisposition.Requested=>"Stop requested.",
                        _=>"No active generation to stop."
                    };
                }
        }
        catch(OperationCanceledException){}
        Notify();
    }

    public async Task VerifyAsync(string? id)
    {
        if(id is null)return;
        await RefreshPanelAsync(id);
        // A read alone cannot establish whether a lost send was delivered.
        // Reconciliation must use authoritative, correlated provider evidence.
        Notify();
    }

    private void StartWatching(string id)
    {
        lock(_gate)
        {
            if(_watches.ContainsKey(id))return;
            var cts=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _watches.Add(id,cts);
            _=WatchPanelAsync(id,cts.Token);
        }
    }

    private async Task WatchPanelAsync(string id,CancellationToken cancellation)
    {
        try
        {
            await foreach(var update in _provider.WatchAsync(id,cancellation))
            {
                var refresh=false;
                lock(_gate)
                {
                    if(!_panels.TryGetValue(id,out var p))continue;
                    if(update.Kind!=PublicUpdateKind.Final)continue;
                    var live=p.Live;
                    if(live is null || live.MessageId!=update.MessageId)
                        live=p.Live=new LiveReply { MessageId=update.MessageId };
                    switch(update.Operation)
                    {
                        case PublicUpdateOperation.Start: live.Text="";live.Completed=false;break;
                        case PublicUpdateOperation.Replace: live.Text=update.Text;break;
                        case PublicUpdateOperation.Append: live.Text+=update.Text;break;
                        case PublicUpdateOperation.Complete:
                            live.Completed=true;p.Generating=false;refresh=true;break;
                        case PublicUpdateOperation.Fail:
                            live.Error="Generation failed.";p.Generating=false;
                            p.Status="Generation failed.";break;
                    }
                    if(live.Text.Length>1_000_000)live.Text=live.Text[..1_000_000];
                    live.Sequence++;
                }
                Notify();
                if(refresh)await RefreshPanelAsync(id);
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        catch(Exception)
        {
            lock(_gate)
                if(_panels.TryGetValue(id,out var p))
                {
                    p.Generating=false;
                    p.Status="Live updates unavailable. Refresh history.";
                }
            Notify();
        }
    }

    private static string SafeReason(string? value) =>
        value=="No conversation provider is connected."?"No provider is connected.":
        value is "send_in_progress" or "stop_in_progress" or
            "previous_delivery_uncertain"?"Operation blocked until resolved.":
        "Provider rejected the operation.";

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach(var watch in _watches.Values)watch.Dispose();
        _lifetime.Dispose();
    }
}
