using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Neko;

public partial class MainWindow : Window
{
    private const string LocalOrigin="https://neko.internal";
    private const string BridgeChannel="neko-ui-bridge-v1";
    private readonly string _token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly OfflineBoardStore _boards;
    private readonly ConversationWorkspaceHost _workspace;
    private readonly IConversationProvider _provider;
    private readonly string _profileDirectory;
    private bool _ready;
    private int _revision;

    private static string DefaultProfileDirectory()=>
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),"NekoOfflineShell");

    // The ordinary desktop launch keeps the configured provider unchanged.
    public MainWindow():this(LocalProviderConfiguration.Load(),
        DefaultProfileDirectory()) {}

    /// <summary>
    /// An explicitly constructed local host may supply its own provider.
    /// The window owns and disposes the supplied provider when it closes.
    /// No external assemblies are discovered or loaded by Neko.
    /// </summary>
    public MainWindow(IConversationProvider provider):
        this(provider,DefaultProfileDirectory()) {}

    // Isolated board state for deterministic integration tests.
    internal MainWindow(IConversationProvider provider,string profileDirectory):
        this((provider??throw new ArgumentNullException(nameof(provider)),
            "Conversation provider supplied by host."),profileDirectory) {}

    private MainWindow((IConversationProvider Provider,string Status) configured,
        string profileDirectory)
    {
        if(string.IsNullOrWhiteSpace(profileDirectory) ||
           !Path.IsPathFullyQualified(profileDirectory))
            throw new ArgumentException("Expected an absolute profile directory.",
                nameof(profileDirectory));
        InitializeComponent();
        _profileDirectory=Path.GetFullPath(profileDirectory);
        _boards=new OfflineBoardStore(_profileDirectory);
        _provider=configured.Provider;
        ProviderStatus.Text=configured.Status;
        _workspace=new ConversationWorkspaceHost(_provider,_boards);
        _workspace.Changed+=OnWorkspaceChanged;
    }

    private void OnWorkspaceChanged()
    {
        if(!_ready || Dispatcher.HasShutdownStarted)return;
        _=Dispatcher.InvokeAsync(async () =>
        {
            try {await PublishAsync();}
            catch(Exception) { /* Keep UI alive if the browser was closed. */ }
        });
    }

    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        try
        {
            await InitializeFrontendAsync();
            await _workspace.RefreshLibraryAsync();
            await _workspace.RestoreBoardAsync();
        }
        catch(Exception ex)
        {
            MessageBox.Show(this,"The frontend could not start. "+
                ex.GetType().Name,"Neko",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }

    private async Task InitializeFrontendAsync()
    {
        var resources=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"wwwroot"));
        var main=Path.Combine(resources,"index.html");
        var surface=Path.Combine(resources,"scripts","NekoSurface.js");
        if(!File.Exists(main)||!File.Exists(surface))
            throw new FileNotFoundException("Bundled frontend resources are unavailable.");

        var environment=await CoreWebView2Environment.CreateAsync(
            userDataFolder:Path.Combine(_profileDirectory,"WebViewProfile"));
        await Browser.EnsureCoreWebView2Async(environment);
        var web=Browser.CoreWebView2;
        web.Settings.AreDevToolsEnabled=false;
        web.Settings.IsStatusBarEnabled=false;
        web.Settings.AreDefaultContextMenusEnabled=false;
        web.Settings.IsWebMessageEnabled=true;
        web.SetVirtualHostNameToFolderMapping("neko.internal",resources,
            CoreWebView2HostResourceAccessKind.DenyCors);
        web.NavigationStarting+=(_,args)=>{
            if(!IsLocalAddress(args.Uri))args.Cancel=true;
        };
        web.NewWindowRequested+=(_,args)=>args.Handled=true;
        web.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        web.WebResourceRequested+=(_,args)=>{
            if(IsLocalAddress(args.Request.Uri))return;
            args.Response=web.Environment.CreateWebResourceResponse(
                new MemoryStream(),403,"Native provider only","Content-Type: text/plain");
        };
        web.WebMessageReceived+=OnWebMessage;
        var source=File.ReadAllText(surface);
        const string placeholder="__NEKO_EMBED_TOKEN__";
        if(!source.Contains(placeholder,StringComparison.Ordinal))
            throw new InvalidDataException("Frontend bridge token placeholder is missing.");
        source=source.Replace(placeholder,JsonSerializer.Serialize(_token),
            StringComparison.Ordinal);
        await web.AddScriptToExecuteOnDocumentCreatedAsync(source);
        web.Navigate(LocalOrigin+"/index.html");
    }

    internal static bool IsLocalAddress(string? value)
    {
        return Uri.TryCreate(value,UriKind.Absolute,out var url) &&
            url.Scheme==Uri.UriSchemeHttps &&
            url.Host.Equals("neko.internal",StringComparison.OrdinalIgnoreCase) &&
            url.IsDefaultPort;
    }

    private async void OnWebMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs args)
    {
        if(Browser.CoreWebView2 is null || !IsLocalAddress(args.Source))return;
        var raw=args.WebMessageAsJson;
        if(raw.Length>34_000)return;
        try
        {
            using var parsed=JsonDocument.Parse(raw);
            var message=parsed.RootElement;
            if(message.ValueKind!=JsonValueKind.Object ||
               ReadString(message,"channel")!=BridgeChannel ||
               ReadString(message,"token")!=_token)return;
            var action=ReadString(message,"action");
            var id=ReadString(message,"id");
            var text=ReadString(message,"text");
            switch(action)
            {
                case "ready":
                    _ready=true;await PublishAsync();break;
                case "panel-board-create":
                    _boards.Create();await PublishAsync();break;
                case "panel-board-switch":
                    if(_boards.Switch(id))
                    {
                        await _workspace.RestoreBoardAsync();
                        await PublishAsync();
                    }
                    break;
                case "panel-board-rename":
                    if(_boards.Rename(id,ReadString(message,"name")))
                        await PublishAsync();
                    break;
                case "panel-board-delete":
                    if(_boards.Delete(id))await PublishAsync();
                    break;
                case "library-refresh":
                    await _workspace.RefreshLibraryAsync();break;
                case "panel-add":
                    await _workspace.OpenPanelAsync(id);break;
                case "panel-remove":
                    _workspace.RemovePanel(id);break;
                case "panel-refresh-history":
                    await _workspace.RefreshPanelAsync(id);break;
                case "panel-draft":
                    _workspace.SetDraft(id,text);break;
                case "panel-send":
                    await _workspace.SendAsync(id,text);break;
                case "panel-stop":
                    await _workspace.StopAsync(id);break;
                case "panel-verify-send":
                    await _workspace.VerifyAsync(id);break;
                case "panel-new-chat":
                    if(ReadString(message,"boardId")==_boards.ActiveBoardId)
                        await _workspace.CreateAsync(text);
                    break;
                case "panel-new-chat-reset":
                    // Never clear uncertain delivery without authoritative evidence.
                    break;
                default:
                    // Unsupported bridge actions are inert. Never translate an
                    // unknown command into a provider operation.
                    break;
            }
        }
        catch(JsonException) { }
        catch(InvalidOperationException) { }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        catch(OperationCanceledException) { }
    }

    private static string? ReadString(JsonElement value,string key)=>
        value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String
            ?item.GetString():null;

    private async Task PublishAsync()
    {
        if(!_ready || Browser.CoreWebView2 is null)return;
        var snapshot=new
        {
            version=++_revision,
            canvasOnly=true,
            selectedId=(string?)null,
            tabs=Array.Empty<object>(),
            library=_workspace.Library(),
            panelWorkspace=_workspace.Snapshot()
        };
        var json=JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        await Browser.CoreWebView2.ExecuteScriptAsync(
            "window.__nekoShell?.receive("+json+");");
    }

    protected override void OnClosed(EventArgs e)
    {
        _workspace.Changed-=OnWorkspaceChanged;
        _workspace.Dispose();
        (_provider as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
