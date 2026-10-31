using System.Net;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class LocalClientReadTests
{
    [Theory]
    [InlineData(HttpStatusCode.Accepted, 0)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    public async Task ReadCommandPreservesPendingAndUnknownHttpSemantics(
        HttpStatusCode status, int expectedExitCode)
    {
        const string pendingJson =
            "{\"status\":\"pending\",\"reason\":\"provider_history_unavailable\",\"conversationId\":\"chat-1\"}";
        using var handler = new StubReadHandler(status, pendingJson);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8765/")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await LocalClient.RunCommandAsync(
            client, "read", ["read", "chat-1"], output, error);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, handler.RequestCount);
        if (status == HttpStatusCode.Accepted)
        {
            Assert.Equal(pendingJson + Environment.NewLine, output.ToString());
            Assert.Contains("History pending", error.ToString());
            Assert.DoesNotContain("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Conversation not found.", error.ToString());
            Assert.DoesNotContain("rejected the request", error.ToString());
        }
    }

    private sealed class StubReadHandler(HttpStatusCode status, string pendingJson)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/conversations/chat-1", request.RequestUri?.AbsolutePath);
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.Accepted ? pendingJson : string.Empty)
            });
        }
    }
}
