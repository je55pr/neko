using System.Text.Json;

namespace Neko.Transport;

/// <summary>
/// Reconstructs assistant text from the first-party SSE delta protocol.
/// Handles full-message snapshots, scalar changes, and batches of JSON
/// patches. No text is written to disk; all state remains in process memory.
/// </summary>
internal sealed class SseTextAssembler
{
    private const int MaxTextChars=1_000_000;
    private readonly string _fallbackMetadataKey;
    internal SseTextAssembler(string fallbackMetadataKey) =>
        _fallbackMetadataKey=fallbackMetadataKey;
    private readonly SortedDictionary<int,string> _parts=[];
    private string _fallbackMarkdown="";
    private bool _ignoreCurrentMessage;
    private string _currentChannel="";
    private string _currentRecipient="";
    private string _currentRole="";
    public bool IsVisibleFinal=>!_ignoreCurrentMessage &&
        _currentRole=="assistant" && _currentChannel=="final" &&
        _currentRecipient is "" or "all" or "none";
    public bool UsingFallbackMarkdown=>_fallbackMarkdown.Length>0;
    public string MessageId {get;private set;}="";
    public string Status {get;private set;}="unknown";
    public string Text => UsingFallbackMarkdown?_fallbackMarkdown:string.Concat(_parts.Values);
    public bool Completed {get;private set;}
    public int DeltaFrames {get;private set;}
    public int TextOperations {get;private set;}
    public int OtherPatches {get;private set;}
    public string Error {get;private set;}="";

    public bool Apply(string eventType,JsonElement frame)
    {
        var before=Text;
        var priorStatus=Status;
        var priorMessage=MessageId;
        var priorComplete=Completed;
        if(eventType=="message_stream_complete")
        {
            // This closes ONE assistant message, not necessarily the entire
            // response. Commentary and tool steps can complete before the
            // final answer is even created.
            Completed=!_ignoreCurrentMessage &&
                _currentChannel=="final" &&
                Status.StartsWith("finished_",StringComparison.Ordinal) &&
                MessageId.Length>0;
        }
        else if(eventType=="DONE")
        {
            // DONE is transport-terminal. Without a final assistant message
            // we must not mislabel earlier commentary as the finished answer.
            Completed=true;
            if(_currentChannel is not ("" or "final") ||
               _ignoreCurrentMessage ||
               !Status.StartsWith("finished_",StringComparison.Ordinal))
                Error="final_answer_not_observed";
        }
        else if(eventType=="delta" && frame.ValueKind==JsonValueKind.Object)
        {
            // A terminal-looking intermediate frame may be followed by more
            // text for the same message. While patches arrive, it isn't done.
            if(Completed)Completed=false;
            DeltaFrames++;
            ApplyPatch(frame,0);
        }
        else if(eventType is "message" or "message_start" or "message_created" &&
                frame.ValueKind==JsonValueKind.Object)
        {
            if(frame.TryGetProperty("message",out var nested) &&
               nested.ValueKind==JsonValueKind.Object)ReadMessage(nested);
            else if(frame.TryGetProperty("author",out _))ReadMessage(frame);
            else ApplyPatch(frame,0);
        }
        else if(eventType=="error")
        {
            Error="stream_error_event";
        }
        return before!=Text || priorStatus!=Status ||
            priorMessage!=MessageId || priorComplete!=Completed ||
            eventType=="error";
    }

    private void ApplyPatch(JsonElement node,int depth)
    {
        if(depth>7 || node.ValueKind!=JsonValueKind.Object)return;
        var path=TextProp(node,"p");
        var operation=TextProp(node,"o");
        var hasValue=node.TryGetProperty("v",out var value);
        if(hasValue && value.ValueKind==JsonValueKind.Array &&
           (operation=="patch" || operation=="" || path=="" || path is null))
        {
            foreach(var part in value.EnumerateArray().Take(200))ApplyPatch(part,depth+1);
            return;
        }
        if(hasValue && value.ValueKind==JsonValueKind.Object &&
           value.TryGetProperty("message",out var snapshot) &&
           snapshot.ValueKind==JsonValueKind.Object &&
           (path is null || path=="" || path=="/" || operation is null || operation=="" || operation=="add"))
        {
            ReadMessage(snapshot);
            return;
        }
        if(hasValue && path=="/message" && value.ValueKind==JsonValueKind.Object)
        {
            ReadMessage(value);
            return;
        }
        if(!hasValue)return;
        if(path=="/message/id" && value.ValueKind==JsonValueKind.String)
        {
            // Modern first-party SSE often changes message IDs with scalar
            // patches instead of a full /message snapshot. Without this,
            // ignoreCurrentMessage can stay true after tool/analysis blocks
            // and silently discard the final answer.
            BeginMessage(value.GetString()??"");
            return;
        }
        if(path=="/message/recipient" && value.ValueKind==JsonValueKind.String)
        {
            _currentRecipient=value.GetString()??"";
            _ignoreCurrentMessage=_currentRecipient.Length>0 &&
                _currentRecipient is not ("all" or "none") ||
                _currentChannel is "analysis" or "reasoning" or "commentary";
            return;
        }
        if(path is "/message/channel" or "/message/metadata/channel" &&
           value.ValueKind==JsonValueKind.String)
        {
            _currentChannel=value.GetString()??"";
            _ignoreCurrentMessage=_currentChannel is "analysis" or "reasoning" or "commentary" ||
                _currentRecipient.Length>0 &&
                _currentRecipient is not ("all" or "none");
            return;
        }
        if(path=="/message/author" && value.ValueKind==JsonValueKind.Object)
        {
            _currentRole=TextProp(value,"role")??"";
            if(_currentRole!="assistant")_ignoreCurrentMessage=true;
            return;
        }
        if(_ignoreCurrentMessage)return;
        if(path=="/message/status" && value.ValueKind==JsonValueKind.String)
        {
            Status=SafeStatus(value.GetString());
            return;
        }
        if(path=="/message/metadata/"+_fallbackMetadataKey+"/fallbackMarkdown" &&
           value.ValueKind==JsonValueKind.String)
        {
            var next=value.GetString()??"";
            _fallbackMarkdown=operation=="append"?_fallbackMarkdown+next:next;
            if(_fallbackMarkdown.Length>MaxTextChars)
            {
                _fallbackMarkdown=_fallbackMarkdown[..MaxTextChars];
                Error="text_limit_reached";
            }
            TextOperations++;
            return;
        }
        if(path=="/message/metadata" && value.ValueKind==JsonValueKind.Object)
        {
            ReadMetadata(value);
            return;
        }
        if(path=="/message/metadata/"+_fallbackMetadataKey && value.ValueKind==JsonValueKind.Object)
        {
            ReadDilMetadata(value);
            return;
        }
        if(path=="/message/content" && value.ValueKind==JsonValueKind.Object)
        {
            ReadContent(value);
            return;
        }
        if(path=="/message/content/parts" && value.ValueKind==JsonValueKind.Array)
        {
            ReadParts(value);
            return;
        }
        const string prefix="/message/content/parts/";
        if(path is not null && path.StartsWith(prefix,StringComparison.Ordinal))
        {
            var suffix=path[prefix.Length..];
            if(suffix=="-" && value.ValueKind==JsonValueKind.String)
            {
                SetPart(_parts.Count,value.GetString()??"",false);
                return;
            }
            if(int.TryParse(suffix,out var index) && index>=0 && index<300 &&
               value.ValueKind==JsonValueKind.String)
            {
                SetPart(index,value.GetString()??"",operation=="append");
                return;
            }
        }
        OtherPatches++;
    }

    private void ReadMessage(JsonElement message)
    {
        // A tool call, tool result, or private reasoning message is an
        // activity block, not prose to append to the final reply.
        var role=message.TryGetProperty("author",out var author) &&
            author.ValueKind==JsonValueKind.Object?TextProp(author,"role"):"";
        var recipient=TextProp(message,"recipient")??"";
        var channel=TextProp(message,"channel")??"";
        if(channel.Length==0 && message.TryGetProperty("metadata",out var channelMetadata) &&
           channelMetadata.ValueKind==JsonValueKind.Object)
            channel=TextProp(channelMetadata,"channel")??"";
        var id=TextProp(message,"id")??"";
        if(id.Length>0)BeginMessage(id);
        _currentChannel=channel;
        _currentRecipient=recipient;
        _currentRole=role??"";
        _ignoreCurrentMessage=role!="" && role!="assistant" ||
            (recipient.Length>0 && recipient is not ("all" or "none")) ||
            channel is "analysis" or "reasoning" or "commentary";
        if(_ignoreCurrentMessage)return;
        if(message.TryGetProperty("status",out var status) &&
           status.ValueKind==JsonValueKind.String)
            Status=SafeStatus(status.GetString());
        if(message.TryGetProperty("content",out var content) &&
           content.ValueKind==JsonValueKind.Object)ReadContent(content);
        if(message.TryGetProperty("metadata",out var metadata) &&
           metadata.ValueKind==JsonValueKind.Object)ReadMetadata(metadata);
    }

    private void BeginMessage(string id)
    {
        if(id.Length==0 || id==MessageId)return;
        MessageId=id;
        _parts.Clear();
        _fallbackMarkdown="";
        Completed=false;
        Status="unknown";
        _ignoreCurrentMessage=false;
        _currentChannel="";
        _currentRecipient="";
        _currentRole="";
    }

    private void ReadMetadata(JsonElement metadata)
    {
        if(metadata.TryGetProperty("channel",out var channel) &&
           channel.ValueKind==JsonValueKind.String)
        {
            _currentChannel=channel.GetString()??"";
            _ignoreCurrentMessage=_currentChannel is "analysis" or "reasoning" or "commentary" ||
                _currentRecipient.Length>0 &&
                _currentRecipient is not ("all" or "none");
        }
        if(metadata.TryGetProperty(_fallbackMetadataKey,out var dil) &&
           dil.ValueKind==JsonValueKind.Object)ReadDilMetadata(dil);
    }

    private void ReadDilMetadata(JsonElement dil)
    {
        if(dil.TryGetProperty("fallbackMarkdown",out var markdown) &&
           markdown.ValueKind==JsonValueKind.String)
            _fallbackMarkdown=markdown.GetString()??"";
    }

    private void ReadContent(JsonElement content)
    {
        if(content.TryGetProperty("parts",out var parts) &&
           parts.ValueKind==JsonValueKind.Array)ReadParts(parts);
    }

    private void ReadParts(JsonElement parts)
    {
        _parts.Clear();
        var i=0;
        foreach(var part in parts.EnumerateArray())
        {
            if(i>=300)break;
            if(part.ValueKind==JsonValueKind.String)
                SetPart(i,part.GetString()??"",false);
            i++;
        }
    }

    private void SetPart(int index,string next,bool append)
    {
        if(next.Length==0)return;
        _parts.TryGetValue(index,out var prior);
        var value=append?(prior??"")+next:next;
        if(value.Length>MaxTextChars)
        {
            Error="text_limit_reached";
            return;
        }
        _parts[index]=value;
        TextOperations++;
    }

    private static string? TextProp(JsonElement node,string name) =>
        node.TryGetProperty(name,out var prop) &&
        prop.ValueKind==JsonValueKind.String?prop.GetString():null;
    private static string SafeStatus(string? value)=>
        value is "in_progress" or "finished_successfully" or
        "finished_partial_completion" or "finished_with_error" or
        "pending" or "unknown"?value:"other";
}
