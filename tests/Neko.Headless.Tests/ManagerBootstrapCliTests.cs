using System.Net;
using System.Text.Json;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class ManagerBootstrapCliTests
{
    [Theory]
    [InlineData("manager-create", "/managers", "POST")]
    [InlineData("manager-list", "/managers", "GET")]
    [InlineData("manager-status", "/managers/main", "GET")]
    [InlineData("manager-history", "/managers/main/history", "GET")]
    [InlineData("manager-send", "/managers/main/messages", "POST")]
    public async Task BootstrapCliRoutesThroughAuthenticatedHost(
        string command, string expectedPath, string expectedMethod)
    {
        using var handler = new ManagerHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        var args = command switch {
            "manager-create" => new[] { command, "main", "initial instructions" },
            "manager-send" => new[] { command, "manager/main", "next instruction" },
            "manager-list" => [command],
            _ => new[] { command, "manager/main" }
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await LocalClient.RunCommandAsync(
            client, command, args, output, error));
        Assert.Equal(expectedPath, handler.LastPath);
        Assert.Equal(expectedMethod, handler.LastMethod);
        Assert.Equal("", error.ToString());
        if (command == "manager-create")
        {
            using var body = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal("manager/main", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("initial instructions", body.RootElement.GetProperty("text").GetString());
        }
        if (command == "manager-send")
        {
            using var body = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal("next instruction", body.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task BadManagerIdentityCannotIssueCreate()
    {
        using var handler = new ManagerHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalClient.RunCommandAsync(client, "manager-create",
                ["manager-create", "invalid/path", "prompt"],
                new StringWriter(), new StringWriter()));
        Assert.Null(handler.LastPath);
    }

    [Theory]
    [InlineData("manager-create")]
    [InlineData("manager-send")]
    public async Task UnsupportedManagerWaitFailsBeforeRemoteRequest(
        string command)
    {
        using var handler = new ManagerHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        var args = new[] { command, "manager/main", "task", "--wait" };
        var error = await Assert.ThrowsAsync<UnsupportedManagerWaitException>(() =>
            LocalClient.RunCommandAsync(client, command, args,
                new StringWriter(), new StringWriter()));
        Assert.Contains("--wait is not supported", error.Message);
        Assert.Null(handler.LastPath);
    }

    [Fact]
    public async Task AlreadyCanonicalManagerCreateDoesNotDoublePrefix()
    {
        using var handler = new ManagerHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        Assert.Equal(0, await LocalClient.RunCommandAsync(client,
            "manager-create", ["manager-create", "manager/main", "initial"],
            new StringWriter(), new StringWriter()));
        using var payload = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("manager/main", payload.RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("manager-status")]
    [InlineData("manager-history")]
    [InlineData("manager-send")]
    [InlineData("manager-queue")]
    [InlineData("manager-message")]
    [InlineData("manager-cancel")]
    [InlineData("manager-retry")]
    [InlineData("reconcile-manager")]
    public async Task InvalidManagerNameIsRejectedBeforeAnyNetworkCall(string command)
    {
        using var handler = new ManagerHandler();
        using var client = new HttpClient(handler) {
            BaseAddress = new Uri("http://127.0.0.1:9875/")
        };
        var args = command switch {
            "manager-send" or "manager-queue" => new[] {command, "invalid/path", "text"},
            "manager-message" or "manager-cancel" or "manager-retry" =>
                [command, "invalid/path", "message-id"],
            _ => [command, "invalid/path"]
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalClient.RunCommandAsync(client, command, args,
                new StringWriter(), new StringWriter()));
        Assert.Null(handler.LastPath);
    }

    private sealed class ManagerHandler : HttpMessageHandler
    {
        public string? LastPath;
        public string? LastMethod;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastMethod = request.Method.Method;
            LastBody = request.Content is null ? null :
                await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"ok\":true}")
            };
        }
    }
}
