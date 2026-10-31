using System.Net;
using System.Text.Json;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class ManagerInboxCliTests
{
    [Fact]
    public async Task QueueCliAcceptsKeyAndConfigInEitherOrder()
    {
        using var handler = new InboxHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        foreach (var args in new[] {
            new[] { "manager-queue", "main", "hello", "--key", "dedupe:1", "--config", "host.json" },
            new[] { "manager-queue", "--config", "host.json", "--key", "dedupe:1", "main", "hello" }
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await LocalClient.RunCommandAsync(
                client, "manager-queue", args, output, error));
            Assert.Equal("", error.ToString());
        }
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task QueueCliRejectsMissingKeyValueBeforeNetworkCall()
    {
        using var handler = new InboxHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalClient.RunCommandAsync(client, "manager-queue",
                ["manager-queue", "main", "hello", "--key"],
                new StringWriter(), new StringWriter()));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class InboxHandler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/managers/main/inbox", request.RequestUri?.AbsolutePath);
            using var json = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("hello", json.RootElement.GetProperty("text").GetString());
            Assert.Equal("dedupe:1", json.RootElement.GetProperty("idempotencyKey").GetString());
            Interlocked.Increment(ref Calls);
            return new HttpResponseMessage(HttpStatusCode.Accepted) {
                Content = new StringContent("{\"message\":{\"state\":\"queued\"}}")
            };
        }
    }
}
