using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Neko;

/// <summary>
/// Optional, standards-based chat-completions adapter. This does NOT use
/// browser session cookies or the frozen application's private transport.
/// </summary>
internal sealed class ConfiguredChatProvider :
    IConversationProvider, IPreparedConversationProvider, IDisposable
{
    private sealed class Conversation(string id,string title)
    {
        public string Id=id;
        public string Title=title;
        public readonly List<VisibleConversationMessage> Messages=[];
        public readonly Channel<PublicConversationUpdate> Updates=
            Channel.CreateUnbounded<PublicConversationUpdate>(new UnboundedChannelOptions
            {SingleReader=true,SingleWriter=false});
        public CancellationTokenSource? Generating;
        public readonly object Gate=new();
    }

    private readonly ConcurrentDictionary<string,Conversation> _chats=new(StringComparer.Ordinal);
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _key;
    private readonly CancellationTokenSource _lifetime=new();

    internal ConfiguredChatProvider(HttpClient client,Uri endpoint,string model,string key)
    {
        if(endpoint.Scheme!="https" &&
            !(endpoint.Scheme=="http" && endpoint.IsLoopback))
            throw new ArgumentException("Endpoint requires HTTPS.");
        if(string.IsNullOrWhiteSpace(model)||string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Provider configuration is incomplete.");
        _client=client;_endpoint=endpoint;_model=model;_key=key;
    }

    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ConversationSummary>>(
            _chats.Values.Select(c=>new ConversationSummary(c.Id,c.Title))
                .OrderBy(c=>c.Title,StringComparer.Ordinal).ToArray());
    }

    public Task<VisibleConversation?> ReadAsync(string id,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!_chats.TryGetValue(id,out var chat))
            return Task.FromResult<VisibleConversation?>(null);
        lock(chat.Gate)
            return Task.FromResult<VisibleConversation?>(
                new VisibleConversation(id,chat.Title,chat.Messages.ToArray()));
    }

    public Task<SendReceipt> CreateAsync(string text,CancellationToken cancellation)
    {
        var prepared=OutboundMessagePreparation.ForNew(text);
        return prepared.Message is null
            ?Task.FromResult(new SendReceipt(SendDisposition.Rejected,"invalid_submission"))
            :CreatePreparedAsync(prepared.Message,cancellation);
    }

    public async Task<SendReceipt> CreatePreparedAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           !submission.CreatesConversation)
            return new SendReceipt(SendDisposition.Rejected,"invalid_submission");
        var id=Guid.NewGuid().ToString("D");
        var trimmed=submission.Text.Trim();
        var chat=new Conversation(id,trimmed.Length>70?trimmed[..70]:trimmed);
        if(!_chats.TryAdd(id,chat))
            return new SendReceipt(SendDisposition.Rejected,"creation_conflict");
        var receipt=await BeginSendAsync(chat,submission,cancellation);
        if(receipt.Disposition==SendDisposition.Rejected)
            _chats.TryRemove(id,out _);
        // Keep the local conversation identifier available for inspection
        // even if the first submission's delivery is uncertain.
        return receipt with {ConversationId=receipt.Disposition is
            SendDisposition.Accepted or SendDisposition.Uncertain?id:null};
    }

    public Task<SendReceipt> SendAsync(string id,string text,CancellationToken cancellation)
    {
        var prepared=OutboundMessagePreparation.ForExisting(id,text);
        return prepared.Message is null
            ?Task.FromResult(new SendReceipt(SendDisposition.Rejected,"invalid_submission"))
            :SendPreparedAsync(prepared.Message,cancellation);
    }

    public Task<SendReceipt> SendPreparedAsync(
        PreparedOutboundMessage submission,CancellationToken cancellation)
    {
        if(!OutboundMessagePreparation.IsValid(submission) ||
           submission.ConversationId is null)
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected,"invalid_submission"));
        if(!_chats.TryGetValue(submission.ConversationId,out var chat))
            return Task.FromResult(new SendReceipt(SendDisposition.Rejected,"unknown_conversation"));
        return BeginSendAsync(chat,submission,cancellation);
    }

    private async Task<SendReceipt> BeginSendAsync(
        Conversation chat,PreparedOutboundMessage submission,CancellationToken cancellation)
    {
        var text=submission.Text;
        CancellationTokenSource source;
        object[] history;
        lock(chat.Gate)
        {
            if(chat.Generating is not null)
                return new SendReceipt(SendDisposition.Rejected,"already_generating");
            source=CancellationTokenSource.CreateLinkedTokenSource(
                cancellation,_lifetime.Token);
            chat.Generating=source;
            history=chat.Messages.Where(m=>m.Role is
                VisibleMessageRole.User or VisibleMessageRole.Assistant)
                .Select(m=>new
                {
                    role=m.Role==VisibleMessageRole.User?"user":"assistant",
                    content=m.Text
                } as object).Concat([new {role="user",content=text}])
                .ToArray();
        }

        HttpResponseMessage response;
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,_endpoint);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);
            request.Content=new StringContent(JsonSerializer.Serialize(
                new {model=_model,messages=history,stream=true}),
                Encoding.UTF8,"application/json");
            response=await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,source.Token);
            if(!response.IsSuccessStatusCode)
            {
                var uncertain=(int)response.StatusCode>=500;
                response.Dispose();
                EndGeneration(chat,source);
                return new SendReceipt(uncertain?
                    SendDisposition.Uncertain:SendDisposition.Rejected,
                    uncertain?"delivery_unknown":"provider_rejected");
            }
        }
        catch(OperationCanceledException)
        {
            EndGeneration(chat,source);
            return new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(HttpRequestException)
        {
            EndGeneration(chat,source);
            return new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }
        catch(Exception)
        {
            EndGeneration(chat,source);
            return new SendReceipt(SendDisposition.Uncertain,"delivery_unknown");
        }

        lock(chat.Gate)
            chat.Messages.Add(new VisibleConversationMessage(
                submission.ClientMessageId,VisibleMessageRole.User,text));
        _=ConsumeStreamAsync(chat,source,response);
        return new SendReceipt(SendDisposition.Accepted);
    }

    private async Task ConsumeStreamAsync(
        Conversation chat,CancellationTokenSource source,HttpResponseMessage response)
    {
        var id=Guid.NewGuid().ToString("D");
        var body=new StringBuilder();
        var completed=false;
        try
        {
            using(response)
            using(var stream=await response.Content.ReadAsStreamAsync(source.Token))
            using(var reader=new StreamReader(stream))
            {
                chat.Updates.Writer.TryWrite(new PublicConversationUpdate(
                    id,PublicUpdateKind.Final,PublicUpdateOperation.Start));
                while(true)
                {
                    source.Token.ThrowIfCancellationRequested();
                    var line=await reader.ReadLineAsync(source.Token);
                    if(line is null)break;
                    if(!line.StartsWith("data:",StringComparison.Ordinal))continue;
                    var data=line[5..].Trim();
                    if(data=="[DONE]"){completed=true;break;}
                    try
                    {
                        using var item=JsonDocument.Parse(data);
                        if(!item.RootElement.TryGetProperty("choices",out var choices)
                           || choices.GetArrayLength()==0)continue;
                        var choice=choices[0];
                        if(choice.TryGetProperty("delta",out var delta) &&
                           delta.TryGetProperty("content",out var content) &&
                           content.ValueKind==JsonValueKind.String)
                        {
                            var chunk=content.GetString()??"";
                            if(body.Length+chunk.Length>1_000_000)
                                throw new InvalidDataException("Output limit");
                            if(chunk.Length>0)
                            {
                                body.Append(chunk);
                                chat.Updates.Writer.TryWrite(new PublicConversationUpdate(
                                    id,PublicUpdateKind.Final,PublicUpdateOperation.Append,chunk));
                            }
                        }
                        if(choice.TryGetProperty("finish_reason",out var finish) &&
                           finish.ValueKind==JsonValueKind.String)
                            completed=true;
                    }
                    catch(JsonException) { /* Ignore malformed isolated SSE frames. */ }
                }
            }
            if(completed)
            {
                lock(chat.Gate)
                    if(body.Length>0)
                        chat.Messages.Add(new VisibleConversationMessage(
                            id,VisibleMessageRole.Assistant,body.ToString()));
                chat.Updates.Writer.TryWrite(new PublicConversationUpdate(
                    id,PublicUpdateKind.Final,PublicUpdateOperation.Complete));
            }
            else chat.Updates.Writer.TryWrite(new PublicConversationUpdate(
                id,PublicUpdateKind.Final,PublicUpdateOperation.Fail));
        }
        catch(Exception)
        {
            chat.Updates.Writer.TryWrite(new PublicConversationUpdate(
                id,PublicUpdateKind.Final,PublicUpdateOperation.Fail));
        }
        finally {EndGeneration(chat,source);}
    }

    public Task<StopReceipt> StopAsync(string id,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!_chats.TryGetValue(id,out var chat))
            return Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        lock(chat.Gate)
        {
            if(chat.Generating is null)
                return Task.FromResult(new StopReceipt(StopDisposition.NotActive));
            chat.Generating.Cancel();
            return Task.FromResult(new StopReceipt(StopDisposition.Requested));
        }
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id,[EnumeratorCancellation]CancellationToken cancellation)
    {
        if(!_chats.TryGetValue(id,out var chat))yield break;
        await foreach(var update in chat.Updates.Reader.ReadAllAsync(cancellation))
            yield return update;
    }

    private static void EndGeneration(Conversation chat,CancellationTokenSource source)
    {
        lock(chat.Gate)
            if(ReferenceEquals(chat.Generating,source))
            {
                chat.Generating=null;
                source.Dispose();
            }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach(var chat in _chats.Values)
        {
            lock(chat.Gate)chat.Generating?.Cancel();
            chat.Updates.Writer.TryComplete();
        }
        _lifetime.Dispose();
        _client.Dispose();
    }
}

internal static class LocalProviderConfiguration
{
    internal sealed record Settings(
        string Provider,string? Endpoint=null,string? Model=null,string? ApiKey=null,
        string? HomeUrl=null,string? ProfileDirectory=null,
        string? RoutePrefix=null,string? ComposerSelector=null,
        string? SubmitSelector=null,string? UserMessageSelector=null,
        string? AssistantMessageSelector=null,
        bool ExperimentalBrowserSend=false,
        string? ActiveGenerationSelector=null);

    internal static (IConversationProvider Provider,string Status) Load()
    {
        // An environment override is useful for installed applications.
        // Neither real configuration nor resolved paths are logged.
        var path=Environment.GetEnvironmentVariable("NEKO_SECRETS_FILE");
        if(string.IsNullOrWhiteSpace(path))
        {
            var besideExe=Path.Combine(AppContext.BaseDirectory,"secrets.json");
            path=File.Exists(besideExe)?besideExe:
                Path.Combine(Environment.CurrentDirectory,"secrets.json");
        }
        if(!File.Exists(path))
            return (new DisconnectedConversationProvider(),"No provider connected • create a local secrets.json");
        try
        {
            var settings=JsonSerializer.Deserialize<Settings>(
                File.ReadAllText(path),
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(settings?.Provider=="web-session")
            {
                if(!Uri.TryCreate(settings.HomeUrl,UriKind.Absolute,out var home))
                    throw new InvalidDataException();
                var config=new WebSessionConversationProvider.Settings(
                    home,settings.ProfileDirectory??"",
                    settings.RoutePrefix??"",
                    settings.ComposerSelector??"",
                    settings.SubmitSelector??"",
                    settings.UserMessageSelector??"",
                    settings.AssistantMessageSelector??"",
                    settings.ExperimentalBrowserSend,
                    settings.ActiveGenerationSelector);
                return (new WebSessionConversationProvider(config),
                    settings.ExperimentalBrowserSend
                        ?"Experimental browser send enabled. Verification required."
                        :"Browser session configured. Sending disabled by default.");
            }
            if(settings?.Provider!="chat-completions" ||
               !Uri.TryCreate(settings.Endpoint,UriKind.Absolute,out var endpoint))
                throw new InvalidDataException();
            return (new ConfiguredChatProvider(new HttpClient(),endpoint,
                settings.Model??"",settings.ApiKey??""),
                "Chat-completions provider configured");
        }
        catch(Exception)
        {
            return (new DisconnectedConversationProvider(),
                "Provider configuration invalid • no connection attempted");
        }
    }
}
