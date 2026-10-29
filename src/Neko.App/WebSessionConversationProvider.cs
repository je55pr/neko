using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Neko;

/// <summary>
/// Opt-in browser-session adapter. A separate, visible WebView owns the site's
/// existing sign-in; Neko never obtains cookies, credentials, or private APIs.
/// All site URLs and selectors are supplied via untracked local configuration.
/// This early adapter supports one text submission at a time; unknown results
/// must never trigger automatic retries.
/// </summary>
internal sealed class WebSessionConversationProvider :
    IConversationProvider, IPreparedConversationProvider, IDisposable
{
    internal sealed record Settings(
        Uri HomeUrl,string ProfileDirectory,string RoutePrefix,
        string ComposerSelector,string SubmitSelector,
        string UserMessageSelector,string AssistantMessageSelector,
        bool ExperimentalSendEnabled=false,
        string? ActiveGenerationSelector=null)
    {
        internal bool Valid =>
            HomeUrl.Scheme==Uri.UriSchemeHttps &&
            HomeUrl.IsDefaultPort && HomeUrl.AbsolutePath=="/" &&
            string.IsNullOrEmpty(HomeUrl.UserInfo) &&
            string.IsNullOrEmpty(HomeUrl.Query) &&
            string.IsNullOrEmpty(HomeUrl.Fragment) &&
            Path.IsPathFullyQualified(ProfileDirectory) &&
            RoutePrefix.StartsWith("/",StringComparison.Ordinal) &&
            !RoutePrefix.StartsWith("//",StringComparison.Ordinal) &&
            !RoutePrefix.Contains("..",StringComparison.Ordinal) &&
            new[]{ComposerSelector,SubmitSelector,UserMessageSelector,
                AssistantMessageSelector}.All(x=>
                !string.IsNullOrWhiteSpace(x) && x.Length<=200 &&
                !x.Contains('\0')) &&
            (ActiveGenerationSelector is null ||
                (ActiveGenerationSelector.Length>0 &&
                 ActiveGenerationSelector.Length<=200 &&
                 !ActiveGenerationSelector.Contains('\0')));
    }

    private sealed class Conversation(string id,string title)
    {
        public string Id=id;
        public string Title=title;
        public readonly List<VisibleConversationMessage> Messages=[];
        public readonly Channel<PublicConversationUpdate> Updates=
            Channel.CreateUnbounded<PublicConversationUpdate>();
    }

    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string,Conversation> _chats=new();
    private readonly SemaphoreSlim _sendGate=new(1,1);
    private WebView2? _view;
    private Window? _window;
    private CoreWebView2? _core;
    private bool _disposed;

    internal WebSessionConversationProvider(Settings settings)
    {
        if(!settings.Valid)throw new ArgumentException("Invalid browser session settings.");
        _settings=settings;
    }

    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>(
            _chats.Values.Select(c=>new ConversationSummary(c.Id,c.Title)).ToArray());
    }

    public Task<VisibleConversation?> ReadAsync(string id,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!_chats.TryGetValue(id,out var chat))
            return Task.FromResult<VisibleConversation?>(null);
        return Task.FromResult<VisibleConversation?>(
            new VisibleConversation(id,chat.Title,chat.Messages.ToArray()));
    }

    public Task<SendReceipt> CreateAsync(string text,CancellationToken ct)
    {
        var prepared=OutboundMessagePreparation.ForNew(text);
        return prepared.Message is null?
            Task.FromResult(new SendReceipt(SendDisposition.Rejected,"invalid_submission")):
            CreatePreparedAsync(prepared.Message,ct);
    }

    public Task<SendReceipt> SendAsync(string id,string text,CancellationToken ct)
    {
        var prepared=OutboundMessagePreparation.ForExisting(id,text);
        return prepared.Message is null?
            Task.FromResult(new SendReceipt(SendDisposition.Rejected,"invalid_submission")):
            SendPreparedAsync(prepared.Message,ct);
    }

    public Task<SendReceipt> CreatePreparedAsync(
        PreparedOutboundMessage message,CancellationToken ct)=>
        DispatchAsync(message,true,ct);

    public Task<SendReceipt> SendPreparedAsync(
        PreparedOutboundMessage message,CancellationToken ct)=>
        DispatchAsync(message,false,ct);

    private async Task<SendReceipt> DispatchAsync(
        PreparedOutboundMessage message,bool creating,CancellationToken ct)
    {
        if(!OutboundMessagePreparation.IsValid(message) ||
           message.CreatesConversation!=creating)
            return new(SendDisposition.Rejected,"invalid_submission");
        if(!_settings.ExperimentalSendEnabled)
            return new(SendDisposition.Rejected,"send_not_enabled");
        ct.ThrowIfCancellationRequested();
        if(!await _sendGate.WaitAsync(0,ct))
            return new(SendDisposition.Rejected,"send_in_progress");
        var clicked=false;
        try
        {
            _expectedText=message.Text;
            var core=await EnsureBrowserAsync(ct);
            var target=message.CreatesConversation
                ?_settings.HomeUrl
                :new Uri(_settings.HomeUrl,_settings.RoutePrefix+
                    Uri.EscapeDataString(message.ConversationId!));
            if(!message.CreatesConversation &&
               !_chats.ContainsKey(message.ConversationId!))
                return new(SendDisposition.Rejected,"unknown_conversation");
            core.Navigate(target.AbsoluteUri);
            if(!await WaitForComposerAsync(core,target,ct))
                return new(SendDisposition.Rejected,"composer_unavailable");
            var baseline=await InspectAsync(core,ct);
            if(baseline is null || !baseline.Ready ||
               !IsExpectedPath(baseline.Path,target.AbsolutePath))
                return new(SendDisposition.Rejected,"composer_unavailable");
            // The page is already authenticated. We invoke its normal text
            // composer and submit button, not a raw HTTP endpoint.
            var input=new {editor=_settings.ComposerSelector,
                submit=_settings.SubmitSelector,text=message.Text,
                path=target.AbsolutePath};
            var staged=await RunAsync(core,@"(function(input){
              if(location.pathname!==input.path)return {ok:false,reason:'route_changed'};
              const ed=document.querySelector(input.editor);
              if(!ed || !ed.isContentEditable)
                return {ok:false,reason:'editor_unavailable'};
              if(ed.innerText.trim())
                return {ok:ed.innerText.trim()===input.text.trim(),
                  reason:'existing_draft'};
              ed.focus();
              const selection=window.getSelection();
              if(!selection)return {ok:false,reason:'selection_unavailable'};
              const range=document.createRange();
              range.selectNodeContents(ed);
              range.collapse(false);
              selection.removeAllRanges();
              selection.addRange(range);
              if(!document.execCommand('insertText',false,input.text))
                return {ok:false,reason:'insert_unavailable'};
              if(ed.innerText.trim()!==input.text.trim())
                return {ok:false,reason:'composer_text_mismatch'};
              return {ok:true};
            })",input,ct);
            if(staged is null ||
               !staged.RootElement.TryGetProperty("ok",out var stageOk) ||
               stageOk.ValueKind!=JsonValueKind.True)
                return new(SendDisposition.Rejected,"composer_stage_failed");

            // React may enable the button on a later render. Never repeat
            // the click, even if delivery verification subsequently times out.
            for(var check=0;check<18 && !clicked;check++)
            {
                await Task.Delay(200,ct);
                var send=await RunAsync(core,@"(function(input){
                  if(location.pathname!==input.path)return {ok:false,reason:'route_changed'};
                  const ed=document.querySelector(input.editor);
                  const button=document.querySelector(input.submit);
                  if(!ed || ed.innerText.trim()!==input.text.trim())
                    return {ok:false,reason:'draft_changed'};
                  if(!button || button.disabled ||
                     button.getAttribute('aria-disabled')==='true')
                    return {ok:false,reason:'not_ready'};
                  const bounds=button.getBoundingClientRect();
                  if(bounds.width<2 || bounds.height<2)
                    return {ok:false,reason:'button_hidden'};
                  return {ok:true,x:bounds.left+bounds.width/2,
                    y:bounds.top+bounds.height/2};
                })",input,ct);
                if(send?.RootElement.TryGetProperty("ok",out var ok)==true &&
                   ok.ValueKind==JsonValueKind.True)
                {
                    var x=send.RootElement.GetProperty("x").GetDouble();
                    var y=send.RootElement.GetProperty("y").GetDouble();
                    // DevTools input dispatch is a browser-level pointer event,
                    // not an untrusted JavaScript element.click().
                    await core.CallDevToolsProtocolMethodAsync(
                        "Input.dispatchMouseEvent",
                        JsonSerializer.Serialize(new
                        {type="mouseMoved",x,y,button="none"}));
                    clicked=true; // partial input is also an uncertain delivery
                    await core.CallDevToolsProtocolMethodAsync(
                        "Input.dispatchMouseEvent",
                        JsonSerializer.Serialize(new
                        {type="mousePressed",x,y,button="left",clickCount=1}));
                    await core.CallDevToolsProtocolMethodAsync(
                        "Input.dispatchMouseEvent",
                        JsonSerializer.Serialize(new
                        {type="mouseReleased",x,y,button="left",clickCount=1}));
                }
                else if(send?.RootElement.TryGetProperty("reason",
                    out var reason)==true &&
                    reason.GetString()!="not_ready")
                    break;
            }
            if(!clicked)return new(SendDisposition.Rejected,"composer_not_ready");

            // A clicked button is NOT proof of delivery. Require a new visible
            // user message plus an authoritative conversation route.
            for(var i=0;i<55;i++)
            {
                await Task.Delay(400,ct);
                var current=await InspectAsync(core,ct);
                if(current is null)continue;
                var now=current;
                if(now.UserCount<=baseline.UserCount ||
                   !now.LastUserMatches)continue;
                var id=now.ConversationId;
                if(id is null)continue;
                if(message.ConversationId is not null &&
                   message.ConversationId!=id)continue;
                var chat=_chats.GetOrAdd(id,other=>new Conversation(other,
                    message.Text.Trim()[..Math.Min(message.Text.Trim().Length,70)]));
                chat.Messages.Add(new VisibleConversationMessage(message.ClientMessageId,
                    VisibleMessageRole.User,message.Text));
                _=ObserveReplyAsync(core,chat,ct);
                return new(SendDisposition.Accepted,ConversationId:id,
                    MessageId:message.ClientMessageId);
            }
            return new(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(OperationCanceledException) when(clicked)
        {
            return new(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception)
        {
            return new(clicked?SendDisposition.Uncertain:SendDisposition.Rejected,
                clicked?"delivery_unknown":"session_unavailable");
        }
        finally{_sendGate.Release();}
    }

    private async Task<CoreWebView2> EnsureBrowserAsync(CancellationToken ct)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(WebSessionConversationProvider));
        if(_core is not null)return _core;
        _view=new WebView2();
        _window=new Window
        {
            Title="Neko session window",
            Width=1000,Height=820,
            Content=_view,ShowInTaskbar=true
        };
        _window.Closed+=(_,_)=>{_core=null;_view=null;_window=null;};
        _window.Show();
        var env=await CoreWebView2Environment.CreateAsync(
            userDataFolder:_settings.ProfileDirectory);
        await _view.EnsureCoreWebView2Async(env);
        _core=_view.CoreWebView2;
        _core.Settings.AreDevToolsEnabled=false;
        _core.Settings.IsWebMessageEnabled=false;
        _core.NavigationStarting+=(_,args)=>
        {
            if(!Uri.TryCreate(args.Uri,UriKind.Absolute,out var uri) ||
               uri.Scheme!=Uri.UriSchemeHttps ||
               uri.Host!=_settings.HomeUrl.Host)args.Cancel=true;
        };
        _core.NewWindowRequested+=(_,args)=>args.Handled=true;
        ct.ThrowIfCancellationRequested();
        return _core;
    }

    internal static bool IsExpectedPath(string? actual,string expected)=>
        string.Equals(actual,expected,StringComparison.Ordinal);

    private async Task<bool> WaitForComposerAsync(
        CoreWebView2 core,Uri target,CancellationToken ct)
    {
        for(var i=0;i<90;i++)
        {
            ct.ThrowIfCancellationRequested();
            var data=await InspectAsync(core,ct);
            if(data is {Ready:true} &&
               IsExpectedPath(data.Path,target.AbsolutePath))
                return true;
            await Task.Delay(350,ct);
        }
        return false;
    }

    private sealed record Inspection(
        bool Ready,string? Path,int UserCount,bool LastUserMatches,
        string? ConversationId,string? AssistantText,bool IsGenerating);

    private async Task<Inspection?> InspectAsync(CoreWebView2 core,CancellationToken ct)
    {
        var data=await RunAsync(core,@"(function(input){
          if(location.origin!==input.origin)return {ready:false};
          const path=location.pathname;
          const id=path.startsWith(input.prefix)?
            path.slice(input.prefix.length).split('/')[0]:null;
          const users=[...document.querySelectorAll(input.user)];
          const replies=[...document.querySelectorAll(input.assistant)];
          return {ready:!!document.querySelector(input.editor),
            path,isGenerating:!!(input.active &&
              document.querySelector(input.active)),
            userCount:users.length,
            lastUserMatches:users.some(x=>x.textContent?.includes(input.text)),
            conversationId:id,assistantText:replies.at(-1)?.innerText||null};
        })",new {origin=_settings.HomeUrl.GetLeftPart(UriPartial.Authority),
            prefix=_settings.RoutePrefix,user=_settings.UserMessageSelector,
            assistant=_settings.AssistantMessageSelector,
            editor=_settings.ComposerSelector,text=_expectedText,
            active=_settings.ActiveGenerationSelector},ct);
        if(data is null)return null;
        var x=data.RootElement;
        return new(
            x.TryGetProperty("ready",out var ready)&&ready.ValueKind==JsonValueKind.True,
            x.TryGetProperty("path",out var path)&&path.ValueKind==JsonValueKind.String?
                path.GetString():null,
            x.TryGetProperty("userCount",out var count)&&count.ValueKind==JsonValueKind.Number?
                count.GetInt32():0,
            x.TryGetProperty("lastUserMatches",out var matches)&&matches.ValueKind==JsonValueKind.True,
            x.TryGetProperty("conversationId",out var id)&&id.ValueKind==JsonValueKind.String?
                id.GetString():null,
            x.TryGetProperty("assistantText",out var assistant)&&assistant.ValueKind==JsonValueKind.String?
                assistant.GetString():null,
            x.TryGetProperty("isGenerating",out var generating)&&generating.ValueKind==JsonValueKind.True);
    }

    private string _expectedText="";
    private static async Task<JsonDocument?> RunAsync(
        CoreWebView2 core,string fn,object args,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var js="("+fn+")("+JsonSerializer.Serialize(args)+")";
        var value=await core.ExecuteScriptAsync(js);
        if(value is "null" or "undefined" or "")return null;
        return JsonDocument.Parse(value);
    }

    private async Task ObserveReplyAsync(CoreWebView2 core,Conversation chat,
        CancellationToken ct)
    {
        var msgId=Guid.NewGuid().ToString("D");
        chat.Updates.Writer.TryWrite(new(msgId,PublicUpdateKind.Final,
            PublicUpdateOperation.Start));
        var last="";
        var unchanged=0;
        try
        {
            for(var i=0;i<110;i++)
            {
                await Task.Delay(450,ct);
                var snapshot=await InspectAsync(core,ct);
                if(snapshot?.ConversationId!=chat.Id)break;
                var text=snapshot.AssistantText??"";
                if(text.Length>0 && text!=last)
                {
                    last=text;unchanged=0;
                    chat.Updates.Writer.TryWrite(new(msgId,PublicUpdateKind.Final,
                        PublicUpdateOperation.Replace,text));
                }
                else if(text.Length>0)unchanged++;
                // A stable assistant reply is suitable for this first smoke
                // test; later streaming work should use provider final events.
                if(last.Length>0 && unchanged>=7 &&
                   _settings.ActiveGenerationSelector is not null &&
                   !snapshot.IsGenerating)
                {
                    chat.Messages.Add(new VisibleConversationMessage(
                        msgId,VisibleMessageRole.Assistant,last));
                    chat.Updates.Writer.TryWrite(new(msgId,PublicUpdateKind.Final,
                        PublicUpdateOperation.Complete));
                    return;
                }
            }
        }
        catch(Exception){ }
        chat.Updates.Writer.TryWrite(new(msgId,PublicUpdateKind.Final,
            PublicUpdateOperation.Fail));
    }

    public Task<StopReceipt> StopAsync(string id,CancellationToken ct)=>
        Task.FromResult(new StopReceipt(StopDisposition.NotActive,"stop_unavailable"));

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id,[EnumeratorCancellation]CancellationToken cancellation)
    {
        if(!_chats.TryGetValue(id,out var chat))yield break;
        await foreach(var item in chat.Updates.Reader.ReadAllAsync(cancellation))
            yield return item;
    }

    public void Dispose()
    {
        _disposed=true;
        _window?.Close();
        _sendGate.Dispose();
    }
}
