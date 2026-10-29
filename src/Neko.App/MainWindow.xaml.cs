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
    private int _revision;
    private bool _ready;
    private string _newChatError="";
    private readonly string _profileDirectory;
    public MainWindow()
    {
        InitializeComponent();
        // This intentionally cannot use the frozen application's profile.
        _profileDirectory=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NekoOfflineShell");
        _boards=new OfflineBoardStore(_profileDirectory);
    }

    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        try { await InitializeFrontendAsync(); }
        catch(Exception ex)
        {
            // Never display full browser exceptions or user data.
            MessageBox.Show(this,"The offline frontend could not start. "+
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
                new MemoryStream(),403,"Offline only","Content-Type: text/plain");
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

    private async void OnWebMessage(object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if(Browser.CoreWebView2 is null)return;
        if(!IsLocalAddress(args.Source))return;
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
            if(action is null)return;
            var changed=action switch
            {
                "ready"=>true,
                "panel-board-create"=>_boards.Create(),
                "panel-board-switch"=>_boards.Switch(ReadString(message,"id")),
                "panel-board-rename"=>_boards.Rename(ReadString(message,"id"),
                    ReadString(message,"name")),
                "panel-board-delete"=>_boards.Delete(ReadString(message,"id")),
                "panel-new-chat"=>MarkOfflineOnly(),
                _=>false
            };
            if(action=="ready")_ready=true;
            if(changed && _ready)await PublishAsync();
        }
        catch(JsonException) { /* Reject malformed bridge messages. */ }
        catch(InvalidOperationException) { /* Reject invalid bridge state. */ }
        catch(IOException) { /* Keep the shell open if local persistence fails. */ }
        catch(UnauthorizedAccessException) { /* Report unavailable storage via the window. */ }
    }

    private bool MarkOfflineOnly()
    {
        _newChatError="No provider is connected. No message was sent.";
        return true;
    }

    private static string? ReadString(JsonElement value,string key)=>
        value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String
            ?item.GetString():null;

    private async Task PublishAsync()
    {
        var snapshot=new
        {
            version=++_revision,
            canvasOnly=true,
            selectedId=(string?)null,
            tabs=Array.Empty<object>(),
            library=new {revision=0,loading=false,
                chats=Array.Empty<object>(),total=0,hasMore=false},
            panelWorkspace=new
            {
                visible=true,
                activeBoardId=_boards.ActiveBoardId,
                boards=_boards.Boards.Select(x=>new {id=x.Id,name=x.Name,
                    count=0,conversationIds=Array.Empty<string>()}).ToArray(),
                panels=Array.Empty<object>(),
                capacity=OfflineBoardStore.MaxBoards,
                newChatError=_newChatError,
                creatingChat=false
            }
        };
        var json=JsonSerializer.Serialize(snapshot);
        await Browser.CoreWebView2.ExecuteScriptAsync(
            "window.__nekoShell?.receive("+json+");");
    }
}
