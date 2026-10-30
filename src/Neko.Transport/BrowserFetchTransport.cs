using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Neko.Transport;

/// <summary>
/// Backend request bridge. Browser handles signed-in session and same-origin
/// network requirements; Neko constructs every request itself. No site UI APIs,
/// DOM input, React state, or bundled submission functions are used.
/// </summary>
internal sealed class BrowserFetchTransport(BrowserSession browser)
{
    internal sealed record Response(int Status, string Mime, string Body, int RetryAfterSeconds);

    private const string Script = """
        (() => {
            const token=__TOKEN__, path=__PATH__, method=__METHOD__,
                  headers=__HEADERS__, body=__BODY__;
            const reply=result=>chrome.webview.postMessage({
                channel:'neko-backend-wire-v1',token,result
            });
            if(location.origin!==__SITE_ORIGIN__){
                reply({status:0,mime:'',body:'',retryAfterSeconds:0});return true;
            }
            fetch(path,{
                method,credentials:'same-origin',redirect:'error',cache:'no-store',
                headers:{
                    ...headers,Accept:'application/json, text/event-stream',
                    ...(method==='POST'?{'Content-Type':'application/json'}:{})
                },
                ...(method==='POST'?{body}:{}),
            }).then(async response=>{
                const mime=response.headers.get('content-type')||'';
                const retry=response.headers.get('retry-after');
                const retryAfterSeconds=retry && /^\d{1,5}$/.test(retry)
                    ?Number(retry):0;
                // SSE is consumed by a separate streaming method later.
                const responseBody=mime.includes('event-stream')
                    ?'':(await response.text()).slice(0,120000);
                reply({status:response.status,mime,
                    body:responseBody,retryAfterSeconds});
            }).catch(()=>reply({
                status:0,mime:'',body:'',retryAfterSeconds:0
            }));
            return true;
        })()
        """;

    public Task<Response> RequestAsync(
        string method, string path, string? jsonBody = null, CancellationToken cancellation = default,
        IReadOnlyDictionary<string,string>? additionalHeaders = null)
    {
        if (method is not ("GET" or "POST") ||
            !path.StartsWith(browser.Settings.ApiPrefix, StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('#', StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            (method == "GET" && jsonBody is not null) ||
            (method == "POST" && jsonBody is null))
            throw new ArgumentException("Invalid backend request.");

        // Block general POSTs. A new endpoint must be intentionally enabled
        // only after its protocol and idempotency behaviour are tested.
        if (method == "POST" && path != browser.Settings.PreparePath)
            throw new InvalidOperationException("Unverified write endpoint.");
        return browser.RunAsync(async core =>
        {
            var headers = await browser.CaptureRequestHeadersAsync();
            if (additionalHeaders is not null)
                foreach (var (name,value) in additionalHeaders)
                {
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Contains('\r') || name.Contains('\n') ||
                        value.Contains('\r') || value.Contains('\n'))
                        throw new ArgumentException("Invalid request header.");
                    headers[name]=value;
                }
            if (!headers.ContainsKey("authorization"))
                return new Response(401, "", "", 0);
            var token = Guid.NewGuid().ToString("N");
            var done = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "https" || uri.Host != browser.Settings.SiteHost ||
                    args.WebMessageAsJson.Length > 150_000) return;
                try
                {
                    using var envelope = JsonDocument.Parse(args.WebMessageAsJson);
                    var root = envelope.RootElement;
                    if (root.GetProperty("channel").GetString() != "neko-backend-wire-v1" ||
                        root.GetProperty("token").GetString() != token) return;
                    done.TrySetResult(root.GetProperty("result").GetRawText());
                }
                catch (Exception) { }
            }

            core.WebMessageReceived += Received;
            try
            {
                var script = Script.Replace("__TOKEN__", JsonSerializer.Serialize(token))
                    .Replace("__PATH__", JsonSerializer.Serialize(path))
                    .Replace("__METHOD__", JsonSerializer.Serialize(method))
                    .Replace("__HEADERS__", JsonSerializer.Serialize(headers))
                    .Replace("__BODY__", JsonSerializer.Serialize(jsonBody))
                    .Replace("__SITE_ORIGIN__",JsonSerializer.Serialize(browser.Settings.SiteOrigin));
                await core.ExecuteScriptAsync(script);
                var raw = await done.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                using var response = JsonDocument.Parse(raw);
                var root = response.RootElement;
                return new Response(
                    root.GetProperty("status").GetInt32(),
                    root.GetProperty("mime").GetString() ?? "",
                    root.GetProperty("body").GetString() ?? "",
                    root.GetProperty("retryAfterSeconds").GetInt32());
            }
            finally { core.WebMessageReceived -= Received; }
        });
    }
}
