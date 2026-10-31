using System.Net;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class ReconciliationCliTests
{
    [Theory]
    [InlineData("reconcile", "chat-1", "/conversations/chat-1/reconcile")]
    [InlineData("reconcile-manager", "manager/main", "/managers/main/reconcile")]
    [InlineData("reconcile-manager", "main", "/managers/main/reconcile")]
    public async Task ExplicitCliReconciliationUsesAuthenticatedPostAndReportsBlock(
        string command, string argument, string expectedPath)
    {
        using var handler = new ReconcileHandler(expectedPath);
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await LocalClient.RunCommandAsync(
            client, command, [command, argument], output, error);
        Assert.Equal(0, code);
        Assert.Contains("provider_rate_limited", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CreationReconcileHasItsOwnExplicitEndpoint()
    {
        using var handler = new ReconcileHandler("/conversations/creation/reconcile");
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        using var output = new StringWriter();
        var code = await LocalClient.RunCommandAsync(client,
            "reconcile-creation", ["reconcile-creation"], output, new StringWriter());
        Assert.Equal(0, code);
        Assert.Contains("blocked", output.ToString());
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ReconcileHandler(string expectedPath) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedPath, request.RequestUri?.AbsolutePath);
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    "{\"outcome\":\"blocked\",\"reason\":\"provider_rate_limited\"}")
            });
        }
    }
}
