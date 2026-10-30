using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using Neko;

namespace Neko.Transport;

/// <summary>
/// Long-lived, read-only CDP observer of SSE initiated by the website's own
/// submission function. Stream observers outlive the send HTTP receipt.
/// Only final, user-visible text enters the API or append-only archive.
/// </summary>
internal sealed class NativeLiveStreamHub(BrowserSession browser) : IDisposable
{
    internal sealed class Attempt : IDisposable
    {
        public readonly string ParentId;
        public readonly string? ConversationId;
        public readonly string? Scope;
        public readonly Channel<PublicConversationUpdate> Updates=
            Channel.CreateUnbounded<PublicConversationUpdate>(
                new UnboundedChannelOptions { SingleReader=true,SingleWriter=true });
        public readonly VisibleTurnArchive Archive;
        internal readonly SseEventParser Parser=new();
        internal readonly SseTextAssembler Assembler;
        internal readonly Decoder Decoder=Encoding.UTF8.GetDecoder();
        internal string LastPublished="";
        internal string LastMessageId="";
        public int Frames {get; internal set;}
        public bool Capturing {get; internal set;}
        public bool StreamEnded {get; internal set;}
        internal bool Closed;
        private readonly object _publicationGate=new();
        private bool _reconciled;

        public Attempt(string parentId,string? conversationId,string? scope,
            string text,string? archiveDirectory=null,string fallbackMetadataKey="richPayload")
        {
            ParentId=parentId;ConversationId=conversationId;Scope=scope;
            Assembler=new SseTextAssembler(fallbackMetadataKey);
            Archive=new VisibleTurnArchive(text,conversationId,scope,
                archiveDirectory);
        }

        internal void ReceiveBase64(string raw)
        {
            lock(_publicationGate) ReceiveBase64Locked(raw);
        }

        internal void MarkCapturing()
        {
            lock(_publicationGate)
                if(!Closed && !_reconciled)Capturing=true;
        }

        private void ReceiveBase64Locked(string raw)
        {
            if(Closed || _reconciled || raw.Length>2_000_000)return;
            try
            {
                var bytes=Convert.FromBase64String(raw);
                var chars=new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                var len=Decoder.GetChars(bytes,0,bytes.Length,chars,0,flush:false);
                foreach(var item in Parser.Push(new string(chars,0,len)))
                {
                    Frames++;
                    if(item.Data=="[DONE]")
                    {
                        Assembler.Apply("DONE",default);
                        StreamEnded=true;
                        continue;
                    }
                    if(item.Data.Length==0 || item.Data.Length>1_000_000)continue;
                    using var packet=JsonDocument.Parse(item.Data);
                    var root=packet.RootElement;
                    var kind=item.Name.Length>0?item.Name:"message";
                    if(root.ValueKind==JsonValueKind.Object &&
                       root.TryGetProperty("type",out var type) &&
                       type.ValueKind==JsonValueKind.String)
                        kind=type.GetString()??kind;
                    var changed=Assembler.Apply(kind,root);
                    if(!changed || !Assembler.IsVisibleFinal)continue;
                    var current=Assembler.Text;
                    if(current==LastPublished || current.Length==0)continue;
                    var append=LastMessageId==Assembler.MessageId &&
                        current.StartsWith(LastPublished,StringComparison.Ordinal);
                    var operation=append?PublicUpdateOperation.Append:
                        PublicUpdateOperation.Replace;
                    var piece=append?current[LastPublished.Length..]:current;
                    var update=new PublicConversationUpdate(
                        Assembler.MessageId,PublicUpdateKind.Final,operation,piece);
                    LastPublished=current;
                    LastMessageId=Assembler.MessageId;
                    Archive.RecordVisible(update,current);
                    Updates.Writer.TryWrite(update);
                }
            }
            catch(Exception ex) when(ex is FormatException or JsonException or
                InvalidDataException or ArgumentException)
            {
                // Do not leak unvalidated SSE data or terminate live user IO.
            }
        }

        // Reconcile the authoritative history while native events are excluded.
        // An in-flight CDP callback may finish first, but can never write after
        // the final verified text is published.
        internal bool PublishPolling(string id,string text,bool verifiedFinal)
        {
            lock(_publicationGate)
            {
                if(Closed || _reconciled || string.IsNullOrWhiteSpace(text) ||
                   (!verifiedFinal && Capturing && !StreamEnded))return false;
                if(verifiedFinal)_reconciled=true;
                var baseline=LastMessageId==id?LastPublished:"";
                var update=ProjectProvider.PollUpdate(id,text,baseline);
                if(update is null)return false;
                Archive.RecordVisible(update,text);
                LastPublished=text;
                LastMessageId=id;
                Updates.Writer.TryWrite(update);
                return true;
            }
        }

        public void Finish(string outcome,string? id,string? serverText=null)
        {
            lock(_publicationGate) FinishLocked(outcome,id,serverText);
        }

        private void FinishLocked(string outcome,string? id,string? serverText)
        {
            if(Closed)return;
            Closed=true;
            try
            {
                Archive.Finish(outcome,id??ConversationId,serverText);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                or ObjectDisposedException)
            {
                // Archive I/O failure must not keep the provider send gate
                // locked or keep subscribed clients waiting forever.
                System.Diagnostics.Trace.TraceWarning(
                    "Conversation archive could not be finalized.");
            }
        }

        public void Dispose()=>Finish("observer_disposed",ConversationId);
    }

    private readonly ConcurrentDictionary<string,Attempt> _byParent=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,Attempt> _byRequest=new(StringComparer.Ordinal);
    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _requests,_responses,_data;
    private bool _disposed;

    public async Task<Attempt> BeginAsync(
        string parentId,string? conversationId,string? scope,string userText)
    {
        if(!Guid.TryParse(parentId,out _))throw new ArgumentException("Invalid parent");
        var result=new Attempt(parentId,conversationId,scope,userText,
            fallbackMetadataKey:browser.Settings.FallbackMetadataKey);
        try
        {
            await browser.RunAsync(async core=>
            {
                if(_disposed)throw new ObjectDisposedException(nameof(NativeLiveStreamHub));
                if(_core is null)
                {
                    await core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                    _core=core;
                    _requests=core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
                    _responses=core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
                    _data=core.GetDevToolsProtocolEventReceiver("Network.dataReceived");
                    _requests.DevToolsProtocolEventReceived+=OnRequest;
                    _responses.DevToolsProtocolEventReceived+=OnResponse;
                    _data.DevToolsProtocolEventReceived+=OnData;
                }
                if(!_byParent.TryAdd(parentId,result))
                    throw new InvalidOperationException("Already observing this parent.");
                return true;
            });
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static string? ExtractParent(JsonElement payload,TransportSettings settings) =>
        payload.ValueKind==JsonValueKind.Object &&
        payload.TryGetProperty(settings.ParentField,out var parent) &&
        parent.ValueKind==JsonValueKind.String
            ?parent.GetString():null;

    private void OnRequest(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            var root=doc.RootElement;
            var request=root.GetProperty("request");
            if(request.GetProperty("method").GetString()!="POST")return;
            var uri=new Uri(request.GetProperty("url").GetString()??"");
            if(uri.Scheme!="https" || uri.Host!=browser.Settings.SiteHost ||
               uri.AbsolutePath!=browser.Settings.SubmitPath)return;
            if(!request.TryGetProperty("postData",out var body) ||
               body.ValueKind!=JsonValueKind.String)return;
            using var json=JsonDocument.Parse(body.GetString()??"");
            var payload=json.RootElement;
            var key=ExtractParent(payload,browser.Settings);
            if(string.IsNullOrEmpty(key))return;
            if(!_byParent.TryGetValue(key,out var attempt))return;
            if(!NativeDispatchSender.MatchesWirePayload(payload,
                attempt.ConversationId,attempt.ParentId,attempt.Scope,browser.Settings))return;
            var reqId=root.GetProperty("requestId").GetString()??"";
            if(reqId.Length>0)_byRequest[reqId]=attempt;
        }
        catch(Exception){}
    }

    private void OnResponse(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            var root=doc.RootElement;
            var id=root.GetProperty("requestId").GetString()??"";
            if(!_byRequest.TryGetValue(id,out var attempt) || attempt.Closed)return;
            var response=root.GetProperty("response");
            var status=(int)response.GetProperty("status").GetDouble();
            var mime=response.GetProperty("mimeType").GetString()??"";
            if(status is <200 or >=300 || !mime.Contains("event-stream",
                StringComparison.OrdinalIgnoreCase))return;
            _=EnableCaptureAsync(id,attempt);
        }
        catch(Exception){}
    }

    private async Task EnableCaptureAsync(string id,Attempt attempt)
    {
        try
        {
            var json=await _core!.CallDevToolsProtocolMethodAsync(
                "Network.streamResourceContent",JsonSerializer.Serialize(new {requestId=id}));
            attempt.MarkCapturing();
            using var data=JsonDocument.Parse(json);
            if(data.RootElement.TryGetProperty("bufferedData",out var value) &&
               value.ValueKind==JsonValueKind.String)
                attempt.ReceiveBase64(value.GetString()??"");
        }
        catch(Exception)
        {
            // The provider may lack live CDP capture. History polling still
            // supplies the final reply and the archive's final snapshot.
        }
    }

    private void OnData(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc=JsonDocument.Parse(e.ParameterObjectAsJson);
            var root=doc.RootElement;
            var id=root.GetProperty("requestId").GetString()??"";
            if(!_byRequest.TryGetValue(id,out var attempt) || attempt.Closed)return;
            if(root.TryGetProperty("data",out var chunk) &&
               chunk.ValueKind==JsonValueKind.String)
                attempt.ReceiveBase64(chunk.GetString()??"");
        }
        catch(Exception){}
    }

    public async Task FinishAsync(Attempt attempt,string outcome,
        string? conversationId,string? authoritativeText=null)
    {
        try
        {
            await browser.RunAsync(core=>
            {
                _byParent.TryRemove(attempt.ParentId,out _);
                foreach(var key in _byRequest.Where(p=>ReferenceEquals(p.Value,attempt))
                    .Select(p=>p.Key).ToArray())
                    _byRequest.TryRemove(key,out _);
                attempt.Finish(outcome,conversationId,authoritativeText);
                return Task.FromResult(true);
            });
        }
        catch
        {
            // The UI scheduler may be gone. Concurrent dictionaries allow
            // deterministic cleanup even when no browser callback can run.
            _byParent.TryRemove(attempt.ParentId,out _);
            foreach(var key in _byRequest.Where(x=>ReferenceEquals(x.Value,attempt))
                .Select(x=>x.Key).ToArray())
                _byRequest.TryRemove(key,out _);
            attempt.Finish("browser_unavailable",conversationId,authoritativeText);
        }
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        try
        {
            browser.RunAsync(core=>
            {
                if(_requests is not null)
                    _requests.DevToolsProtocolEventReceived-=OnRequest;
                if(_responses is not null)
                    _responses.DevToolsProtocolEventReceived-=OnResponse;
                if(_data is not null)
                    _data.DevToolsProtocolEventReceived-=OnData;
                foreach(var attempt in _byParent.Values)attempt.Dispose();
                _byParent.Clear();
                _byRequest.Clear();
                return Task.FromResult(true);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            foreach(var attempt in _byParent.Values)attempt.Dispose();
            _byParent.Clear();
            _byRequest.Clear();
        }
    }
}
