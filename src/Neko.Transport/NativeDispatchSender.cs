using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Neko.Transport;

/// <summary>
/// Calls the site's authenticated in-memory submission operation directly.
/// No DOM composer or button interaction, no replayed request credentials.
/// The site's normal request pipeline owns integrity and streaming.
/// One explicit invocation per user request, no automatic retry.
/// </summary>
internal sealed class NativeDispatchSender(BrowserSession browser)
{
    internal sealed record Result(
        string State, bool Dispatched, bool SseAccepted,
        int? HttpStatus=null, string? ServerConversationId=null,
        int ObservedPosts=0, int CorrelatedPosts=0,
        string? FailureStage=null, string? ErrorClass=null);

    internal const string Script = """
        (() => {
          const id=__ID__,parent=__PARENT__,text=__TEXT__,token=__TOKEN__;
          const scope=__SCOPE__;
          const bridge=window.__nekoTransport;
          if(typeof bridge?.submit!=='function' ||
             typeof bridge.scope?.get!=='function')
              return {invoked:false,state:'dispatcher_unavailable'};
          const selected=bridge.selectedModel?.(id);
          if(!selected || typeof selected.slug!=='string')
              return {invoked:false,state:'model_unavailable'};
          const report=data=>chrome.webview.postMessage({
              channel:'neko-native-submission',token,data
          });
          try {
              const opts={
                  conversationId:id,
                  parentMessageId:parent,
                  prompt:text,
                  model:selected.slug,
                  ...(selected.thinkingEffort?
                      {thinkingEffort:selected.thinkingEffort}:{}),
                  isTemporaryChat:false,
                  // Only set explicitly scoped values for conversation creation.
                  // The site's submit function destructures projectId, not
                  // a differently named property. The site maps the explicit
                  // scope to its native wire field.
                  ...(scope?{projectId:scope}:{}),
                  onServerThreadIdChange:conversationId=>{
                      if(typeof conversationId==='string' &&
                          /^[0-9a-f-]{36}$/i.test(conversationId))
                          report({kind:'resolved',conversationId});
                  }
              };
              const call=bridge.submit(bridge.scope,opts);
              Promise.resolve(call).then(value=>report({
                  kind:'returned',
                  conversationId:typeof value?.conversationId==='string'?
                      value.conversationId:'',
                  stream:typeof value?.streamRequestId==='string'
              }),error=>report({
                  kind:'failed',
                  errorClass:String(error?.name||'Error').slice(0,40)
              }));
              return {invoked:true,state:'dispatched'};
          } catch(error) {
              return {invoked:false,state:'dispatch_exception'};
          }
        })()
        """;

    /// <summary>Correlates the actual network request to the specific
    /// intended parent and scope, never to prompt text or a guessed ID.</summary>
    internal static bool MatchesWirePayload(JsonElement payload,
        string? conversationId,string parentId,string? scope,TransportSettings settings)
    {
        if(payload.ValueKind!=JsonValueKind.Object ||
           !payload.TryGetProperty(settings.ParentField,out var parent) ||
           parent.ValueKind!=JsonValueKind.String ||
           parent.GetString()!=parentId)
            return false;
        if(conversationId is not null)
            return payload.TryGetProperty(settings.ConversationField,out var id) &&
                id.ValueKind==JsonValueKind.String &&
                id.GetString()==conversationId;
        // The provider may allocate a client thread id before the server
        // resolves the canonical conversation ID. Do not require null here.
        // Creation must explicitly carry the project scope in the payload.
        return scope is not null &&
            payload.TryGetProperty(settings.ScopeField,out var project) &&
            project.ValueKind==JsonValueKind.String &&
            project.GetString()==scope;
    }

    /// <summary>
    /// Rehearse the exact JS envelope and CDP setup, replacing ONLY the
    /// submission call with a local dry-run return. Zero network writes.
    /// </summary>
    internal async Task<string> DiagnosePreparationAsync(string scope)
    {
        var stage="bootstrap";
        return await browser.RunAsync(async core =>
        {
            try
            {
                var state=await NativeDispatcherBootstrap.InitializeAsync(core,browser.Settings);
                if(!state.Ready)
                    return JsonSerializer.Serialize(new {ready=false,stage,reason=state.Reason});
                stage="network_enable";
                await core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                stage="subscribe";
                var requests=core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
                var responses=core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
                void OnRequest(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs __){}
                void OnResponse(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs __){}
                requests.DevToolsProtocolEventReceived+=OnRequest;
                responses.DevToolsProtocolEventReceived+=OnResponse;
                try
                {
                    stage="js_prepare";
                    var dry=Script.Replace(
                        "const call=bridge.submit(bridge.scope,opts);",
                        "return {invoked:false,state:'dry_run',projectBound:opts.projectId===scope,"+
                        "parentValid:/^[a-fA-F0-9-]{36}$/.test(opts.parentMessageId)};");
                    if(dry==Script)throw new InvalidOperationException("Dry-run hook not installed");
                    var rendered=dry.Replace("__ID__","null")
                        .Replace("__PARENT__",JsonSerializer.Serialize(Guid.NewGuid().ToString("D")))
                        .Replace("__TEXT__",JsonSerializer.Serialize("read-only dry run"))
                        .Replace("__TOKEN__",JsonSerializer.Serialize(Guid.NewGuid().ToString("N")))
                        .Replace("__SCOPE__",JsonSerializer.Serialize(scope));
                    using var output=JsonDocument.Parse(await core.ExecuteScriptAsync(rendered));
                    var root=output.RootElement;
                    return JsonSerializer.Serialize(new {
                        ready=true,stage,
                        dryRun=root.TryGetProperty("state",out var s)&&
                            s.GetString()=="dry_run",
                        projectBound=root.TryGetProperty("projectBound",out var pb)&&
                            pb.ValueKind==JsonValueKind.True,
                        parentValid=root.TryGetProperty("parentValid",out var pv)&&
                            pv.ValueKind==JsonValueKind.True
                    });
                }
                finally
                {
                    requests.DevToolsProtocolEventReceived-=OnRequest;
                    responses.DevToolsProtocolEventReceived-=OnResponse;
                }
            }
            catch(Exception ex)
            {
                return JsonSerializer.Serialize(new{
                    ready=false,stage,errorClass=ex.GetType().Name
                });
            }
        });
    }

    /// <summary>
    /// The account model catalogue can hydrate after the dispatcher loads.
    /// Read-only polling; no navigation, submission or HTTP request is made.
    /// </summary>
    private static async Task<bool> WaitForModelAsync(
        CoreWebView2 core,string? id,CancellationToken cancellation)
    {
        var script="(() => {try {const model=window.__nekoTransport"+
            "?.selectedModel?.("+JsonSerializer.Serialize(id)+");"+
            "return typeof model?.slug==='string' && model.slug.length>0;"+
            "}catch{return false}})()";
        for(var i=0;i<20;i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if(await core.ExecuteScriptAsync(script)=="true")return true;
            }
            catch(Exception ex) when(ex is InvalidOperationException or
                System.Runtime.InteropServices.COMException){}
            if(i<19)await Task.Delay(500,cancellation);
        }
        return false;
    }

    public async Task<Result> SubmitOnceAsync(
        string? conversationId, string parentId, string text, string? scope,
        CancellationToken cancellation,bool externalStreamObserver=false)
    {
        if((conversationId is not null && !Guid.TryParse(conversationId,out _)) ||
           !Guid.TryParse(parentId,out _) ||
           string.IsNullOrWhiteSpace(text) || text.Length>24000 ||
           (scope is not null && !System.Text.RegularExpressions.Regex.IsMatch(
                scope,browser.Settings.ScopePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            return new("invalid_request",false,false);

        cancellation.ThrowIfCancellationRequested();
        var stage="schedule_browser";
        var mightHaveDispatched=false;
        var recordedPosts=0;
        var matchedPosts=0;
        try
        {
        return await browser.RunAsync(async core =>
        {
            stage="bootstrap";
            var initialized=await NativeDispatcherBootstrap.InitializeAsync(core,browser.Settings,cancellation);
            if(!initialized.Ready)
                return new Result("dispatcher_"+initialized.Reason,false,false);
            stage="model_catalogue";
            if(!await WaitForModelAsync(core,conversationId,cancellation))
                return new Result("model_unavailable",false,false);

            var token=Guid.NewGuid().ToString("N");
            var requests=new HashSet<string>(StringComparer.Ordinal);
            var streamTrackers=new Dictionary<string,SseConversationIdTracker>(
                StringComparer.Ordinal);
            int observedPosts=0,correlatedPosts=0;
            var accepted=new TaskCompletionSource<(int Code,string Mime)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resolved=new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var returned=new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void Requested(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs evt)
            {
                try
                {
                    using var outer=JsonDocument.Parse(evt.ParameterObjectAsJson);
                    var root=outer.RootElement;
                    var req=root.GetProperty("request");
                    if(req.GetProperty("method").GetString()!="POST")return;
                    var url=new Uri(req.GetProperty("url").GetString()??"");
                    if(url.Scheme!="https"||url.Host!=browser.Settings.SiteHost ||
                       url.AbsolutePath!=browser.Settings.SubmitPath)return;
                    observedPosts++;
                    recordedPosts++;
                    if(!req.TryGetProperty("postData",out var post))return;
                    using var body=JsonDocument.Parse(post.GetString()??"");
                    if(!MatchesWirePayload(body.RootElement,
                        conversationId,parentId,scope,browser.Settings))return;
                    correlatedPosts++;
                    matchedPosts++;
                    var requestId=root.GetProperty("requestId").GetString()??"";
                    requests.Add(requestId);
                    if(conversationId is null && requestId.Length>0)
                        streamTrackers[requestId]=new SseConversationIdTracker();
                }
                catch(Exception){}
            }

            void ReceiveStreamData(string requestId,string base64)
            {
                if(!streamTrackers.TryGetValue(requestId,out var tracker))return;
                var id=tracker.PushBase64(base64);
                if(id is not null)resolved.TrySetResult(id);
            }

            async Task EnableStreamCaptureAsync(string requestId)
            {
                try
                {
                    var raw=await core.CallDevToolsProtocolMethodAsync(
                        "Network.streamResourceContent",
                        JsonSerializer.Serialize(new {requestId}));
                    using var response=JsonDocument.Parse(raw);
                    if(response.RootElement.TryGetProperty("bufferedData",out var bytes)
                       && bytes.ValueKind==JsonValueKind.String)
                        ReceiveStreamData(requestId,bytes.GetString()??"");
                }
                catch(Exception){} // CDP streaming optional; callback still works.
            }

            void StreamData(object? sender,
                CoreWebView2DevToolsProtocolEventReceivedEventArgs evt)
            {
                try
                {
                    using var data=JsonDocument.Parse(evt.ParameterObjectAsJson);
                    var root=data.RootElement;
                    var requestId=root.GetProperty("requestId").GetString()??"";
                    if(!streamTrackers.ContainsKey(requestId))return;
                    if(root.TryGetProperty("data",out var encoded)
                       && encoded.ValueKind==JsonValueKind.String)
                        ReceiveStreamData(requestId,encoded.GetString()??"");
                }
                catch(Exception){}
            }

            void Responded(object? sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs evt)
            {
                try
                {
                    using var outer=JsonDocument.Parse(evt.ParameterObjectAsJson);
                    var root=outer.RootElement;
                    var requestId=root.GetProperty("requestId").GetString()??"";
                    if(!requests.Contains(requestId))return;
                    var resp=root.GetProperty("response");
                    var status=(int)resp.GetProperty("status").GetDouble();
                    var mime=resp.GetProperty("mimeType").GetString()??"";
                    accepted.TrySetResult((status,mime));
                    if(!externalStreamObserver && conversationId is null &&
                       status is >=200 and <300 &&
                       mime.Contains("event-stream",
                           StringComparison.OrdinalIgnoreCase))
                        _=EnableStreamCaptureAsync(requestId);
                }
                catch(Exception){}
            }

            void Message(object? sender,CoreWebView2WebMessageReceivedEventArgs evt)
            {
                if(!Uri.TryCreate(evt.Source,UriKind.Absolute,out var origin) ||
                   origin.Scheme!="https" || origin.Host!=browser.Settings.SiteHost ||
                   evt.WebMessageAsJson.Length>2000)return;
                try
                {
                    using var envelope=JsonDocument.Parse(evt.WebMessageAsJson);
                    var root=envelope.RootElement;
                    if(root.GetProperty("channel").GetString()!="neko-native-submission" ||
                       root.GetProperty("token").GetString()!=token)return;
                    var data=root.GetProperty("data");
                    var kind=data.GetProperty("kind").GetString();
                    if(kind=="resolved" &&
                       data.TryGetProperty("conversationId",out var server) &&
                       Guid.TryParse(server.GetString(),out _))
                        resolved.TrySetResult(server.GetString()!);
                    if(kind=="returned")
                    {
                        if(data.TryGetProperty("conversationId",out var resultId) &&
                           Guid.TryParse(resultId.GetString(),out _))
                            resolved.TrySetResult(resultId.GetString()!);
                        returned.TrySetResult("returned");
                    }
                    if(kind=="failed")returned.TrySetResult("failed");
                }
                catch(Exception){}
            }

            stage="network_enable";
            await core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
            stage="event_receivers";
            var requestReceiver=core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            var responseReceiver=core.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            var dataReceiver=core.GetDevToolsProtocolEventReceiver("Network.dataReceived");
            requestReceiver.DevToolsProtocolEventReceived+=Requested;
            responseReceiver.DevToolsProtocolEventReceived+=Responded;
            dataReceiver.DevToolsProtocolEventReceived+=StreamData;
            core.WebMessageReceived+=Message;
            var invoked=false;
            try
            {
                var script=Script.Replace("__ID__",JsonSerializer.Serialize(conversationId))
                    .Replace("__PARENT__",JsonSerializer.Serialize(parentId))
                    .Replace("__TEXT__",JsonSerializer.Serialize(text))
                    .Replace("__TOKEN__",JsonSerializer.Serialize(token))
                    .Replace("__SCOPE__",JsonSerializer.Serialize(scope));
                stage="submission_script";
                mightHaveDispatched=true;
                string? scriptError=null;
                try
                {
                    using var initial=JsonDocument.Parse(await core.ExecuteScriptAsync(script));
                    var data=initial.RootElement;
                    if(!data.TryGetProperty("invoked",out var started)||!started.GetBoolean())
                        return new Result(
                            data.TryGetProperty("state",out var reason)
                                ?reason.GetString()??"dispatcher_unavailable"
                                :"dispatcher_unavailable",false,false);
                }
                catch(Exception ex) when(ex is InvalidOperationException or
                    System.Runtime.InteropServices.COMException or JsonException)
                {
                    // A native project creation may navigate away before
                    // ExecuteScriptAsync returns. Keep CDP/SSE observation
                    // alive; the network may already contain the actual send.
                    scriptError=ex.GetType().Name;
                }
                invoked=true;
                stage="http_admission";
                var awaited=await Task.WhenAny(accepted.Task,Task.Delay(18000,cancellation));
                if(awaited!=accepted.Task)
                    return new Result("submission_uncertain",true,false,
                        ServerConversationId:resolved.Task.IsCompletedSuccessfully?
                            resolved.Task.Result:null,
                        ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts,
                        FailureStage:scriptError is null?null:"submission_script",
                        ErrorClass:scriptError);
                var (status,mime)=await accepted.Task;
                if(status is <200 or >=300)
                    return new Result("http_rejected",true,false,status,
                        ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts);
                if(!mime.Contains("event-stream",StringComparison.OrdinalIgnoreCase))
                    return new Result("unexpected_response",true,false,status,
                        ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts);
                if(conversationId is not null)
                    return new Result("sse_accepted",true,true,status,
                        conversationId,observedPosts,correlatedPosts);

                stage="conversation_id_resolution";
                awaited=await Task.WhenAny(resolved.Task,Task.Delay(20000,cancellation));
                if(awaited!=resolved.Task)
                    return new Result("conversation_id_uncertain",true,true,status,
                        ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts,
                        FailureStage:scriptError is null?null:"submission_script",
                        ErrorClass:scriptError);
                return new Result("sse_accepted",true,true,status,await resolved.Task,
                    observedPosts,correlatedPosts);
            }
            catch(OperationCanceledException) when(invoked)
            {
                return new Result("cancelled_after_dispatch",true,false,
                    ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts,
                    FailureStage:stage,ErrorClass:nameof(OperationCanceledException));
            }
            catch(Exception ex) when(invoked)
            {
                return new Result("exception_after_dispatch",true,false,
                    ObservedPosts:observedPosts,CorrelatedPosts:correlatedPosts,
                    FailureStage:stage,ErrorClass:ex.GetType().Name);
            }
            finally
            {
                requestReceiver.DevToolsProtocolEventReceived-=Requested;
                responseReceiver.DevToolsProtocolEventReceived-=Responded;
                dataReceiver.DevToolsProtocolEventReceived-=StreamData;
                core.WebMessageReceived-=Message;
            }
        });
        }
        catch(Exception ex)
        {
            // A page navigation or process failure may interrupt an invoked
            // JS script before its receipt. Never label that as a rejection.
            return new Result("browser_operation_exception",
                mightHaveDispatched,false,
                ObservedPosts:recordedPosts,CorrelatedPosts:matchedPosts,
                FailureStage:stage,ErrorClass:ex.GetType().Name);
        }
    }
}
