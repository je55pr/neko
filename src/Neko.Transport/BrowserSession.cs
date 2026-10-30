using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Neko.Transport;

internal sealed class BrowserSession : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, string> _headers =
        new(StringComparer.OrdinalIgnoreCase);
    private Dispatcher? _dispatcher;
    private CoreWebView2? _core;
    internal TransportSettings Settings { get; }

    public BrowserSession(string profile,TransportSettings settings)
    {
        Settings=settings;
        _thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            _dispatcher = Dispatcher.CurrentDispatcher;
            var window = new Window
            {
                Width = 720, Height = 560, Left = -4000, Top = -4000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0.02
            };
            var view = new WebView2();
            window.Content = view;
            app.MainWindow = window;
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
                DispatcherPriority.Normal);
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                    await view.EnsureCoreWebView2Async(env);
                    _core = view.CoreWebView2;
                    _core.AddWebResourceRequestedFilter(
                        Settings.SiteOrigin+Settings.ApiPrefix+"*", CoreWebView2WebResourceContext.All);
                    _core.WebResourceRequested += CaptureHeaders;
                    await NavigateOnUiAsync(Settings.SiteOrigin);
                    _ready.TrySetResult();
                }
                catch (Exception ex)
                {
                    try {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "neko-private-adapter.log"),
                            DateTimeOffset.UtcNow.ToString("O") + " browser_setup " +
                            ex.GetType().Name + " " + ex.Message + Environment.NewLine);
                    } catch { }
                    _ready.TrySetException(new InvalidOperationException(
                        "Authenticated browser session unavailable."));
                    window.Close();
                }
            };
            app.Run(window);
        }) { IsBackground = true, Name = "Neko private browser transport" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void CaptureHeaders(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (args.Request.Method != "GET") return;
        foreach (var name in Settings.ForwardHeaders)
        {
            try
            {
                var value = args.Request.Headers.GetHeader(name);
                if (!string.IsNullOrWhiteSpace(value) && value.Length < 8192)
                    _headers[name] = value;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    public async Task<T> RunAsync<T>(Func<CoreWebView2, Task<T>> work)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("No browser dispatcher.");
        return await dispatcher.InvokeAsync(() => work(_core!), DispatcherPriority.Normal)
            .Task.Unwrap();
    }

    private async Task NavigateOnUiAsync(string url)
    {
        var core = _core!;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
            completion.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Finished;
        try
        {
            core.Navigate(url);
            if (!await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Navigation failed.");
            await Task.Delay(2500);
        }
        finally { core.NavigationCompleted -= Finished; }
    }

    /// <summary>Copies short-lived request headers into process memory.
    /// Session cookies remain under WebView2's control.</summary>
    internal Task<Dictionary<string, string>> CaptureRequestHeadersAsync() =>
        RunAsync(core => Task.FromResult(new Dictionary<string, string>(
            _headers, StringComparer.OrdinalIgnoreCase)));

    internal Task<string> CurrentRouteKindAsync(string conversationId) =>
        RunAsync(async core =>
        {
            var actual=JsonSerializer.Deserialize<string>(
                await core.ExecuteScriptAsync("location.pathname"))??"";
            if(actual.Contains(conversationId,StringComparison.OrdinalIgnoreCase))
                return "conversation_route";
            if(actual.StartsWith("/g/",StringComparison.Ordinal))
                return "project_route";
            if(actual.StartsWith("/auth",StringComparison.Ordinal) ||
               actual.StartsWith("/login",StringComparison.Ordinal))
                return "authentication_route";
            if(actual=="/")return "home_route";
            return "other_route";
        });

    public Task<bool> NavigateAsync(string url) => RunAsync(async core =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != Settings.SiteHost)
            return false;
        await NavigateOnUiAsync(url);
        var path = await core.ExecuteScriptAsync("location.pathname");
        var actual = JsonSerializer.Deserialize<string>(path);
        if(actual is null)return false;
        if(actual.StartsWith(uri.AbsolutePath,StringComparison.Ordinal))return true;
        // Project-scoped conversations redirect from /c/{id} to
        // /g/{project}/c/{id}. Verify the full terminal segment.
        return uri.AbsolutePath.StartsWith("/c/",StringComparison.Ordinal) &&
            actual.EndsWith(uri.AbsolutePath,StringComparison.OrdinalIgnoreCase) &&
            actual.StartsWith("/g/",StringComparison.Ordinal);
    });

    private const string ReadScript = """
        (() => {
            const path=__PATH__,kind=__KIND__,context=__HEADERS__,token=__TOKEN__;
            const reply=result=>chrome.webview.postMessage({
                channel:'neko-read',token,result
            });
            if(location.origin!==__SITE_ORIGIN__){
                reply({status:0,ok:false});return true;
            }
            fetch(path,{method:'GET',credentials:'same-origin',cache:'no-store',
                headers:{...context,Accept:'application/json'}})
            .then(async response=>{
                if(!response.ok){
                    const raw=response.headers.get('retry-after');
                    const retryAfterSeconds=raw && /^\d{1,5}$/.test(raw) ? Number(raw) : 0;
                    reply({status:response.status,ok:false,retryAfterSeconds});return;
                }
                const data=await response.json();
                if(kind==='status'){
                    reply({status:response.status,ok:true,
                        streamState:typeof data?.status==='string'?data.status:''});
                    return;
                }
                if(kind==='project'){
                    const items=Array.isArray(data.items)?data.items:[];
                    reply({status:response.status,ok:true,
                        items:items.slice(0,100).map(x=>({
                            id:typeof x?.id==='string'?x.id:''
                        })).filter(x=>x.id),
                        cursor:typeof data.cursor==='string'?data.cursor:null});
                    return;
                }
                const messages=Array.isArray(data.messages)?data.messages:[];
                const publicMessages=messages.map(item=>{
                    const m=item?.message||item;
                    const role=m?.author?.role||'';
                    const channel=m?.channel||m?.metadata?.channel||
                        m?.author?.metadata?.channel||'';
                    const recipient=m?.recipient||'';
                    if(!['user','assistant'].includes(role))return null;
                    if(role==='assistant' && !['','final'].includes(channel))return null;
                    if(recipient && recipient!=='all' && recipient!=='none')return null;
                    const parts=Array.isArray(m?.content?.parts)?m.content.parts:[];
                    const text=parts.filter(p=>typeof p==='string').join('')||
                        (typeof m?.content?.text==='string'?m.content.text:'');
                    const fallback=m?.metadata?.[__FALLBACK_METADATA_KEY__]?.fallbackMarkdown;
                    return {
                        id:typeof m?.id==='string'?m.id:'',
                        role,text:(typeof fallback==='string' && role==='assistant'
                            ?fallback:text).slice(0,150000),
                        status:typeof m?.status==='string'?m.status:''
                    };
                }).filter(m=>m && m.id);
                reply({status:response.status,ok:true,
                    currentNode:typeof data.current_node==='string'?data.current_node:'',
                    id:typeof data.conversation_id==='string'?data.conversation_id:'',
                    title:typeof data.title==='string'?data.title.slice(0,200):'',
                    messages:publicMessages.slice(-300)});
            }).catch(()=>reply({status:0,ok:false}));
            return true;
        })()
        """;

    public Task<JsonDocument> ReadAsync(string path, string kind) => RunAsync(async core =>
    {
        if (!_headers.ContainsKey("authorization"))
            return JsonDocument.Parse("{\"status\":401,\"ok\":false}");
        var token = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) ||
                source.Scheme != "https" || source.Host != Settings.SiteHost ||
                args.WebMessageAsJson.Length > 2_000_000) return;
            try
            {
                using var data = JsonDocument.Parse(args.WebMessageAsJson);
                var root = data.RootElement;
                if (root.GetProperty("channel").GetString() != "neko-read" ||
                    root.GetProperty("token").GetString() != token) return;
                completion.TrySetResult(root.GetProperty("result").GetRawText());
            }
            catch (Exception) { }
        }
        core.WebMessageReceived += Received;
        try
        {
            var script = ReadScript.Replace("__PATH__", JsonSerializer.Serialize(path))
                .Replace("__KIND__", JsonSerializer.Serialize(kind))
                .Replace("__HEADERS__", JsonSerializer.Serialize(_headers))
                .Replace("__TOKEN__", JsonSerializer.Serialize(token))
                .Replace("__SITE_ORIGIN__", JsonSerializer.Serialize(Settings.SiteOrigin))
                .Replace("__FALLBACK_METADATA_KEY__", JsonSerializer.Serialize(Settings.FallbackMetadataKey));
            await core.ExecuteScriptAsync(script);
            return JsonDocument.Parse(await completion.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        }
        finally { core.WebMessageReceived -= Received; }
    });

    public void Dispose()
    {
        try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal); }
        catch (Exception) { }
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
