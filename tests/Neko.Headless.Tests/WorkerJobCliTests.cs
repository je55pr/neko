using System.Net;
using System.Text.Json;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class WorkerJobCliTests
{
    [Theory]
    [InlineData("manager-jobs", "/managers/main/jobs", "GET")]
    [InlineData("manager-job", "/managers/main/jobs/abc", "GET")]
    [InlineData("manager-interrupt", "/managers/main/jobs/abc/interrupt", "POST")]
    public async Task InspectionAndInterruptionRoutesAreStable(
        string command, string path, string method)
    {
        using var handler = new JobHandler();
        using var client = Client(handler);
        string[] args = command switch {
            "manager-jobs" => [command, "manager/main"],
            "manager-job" => [command, "manager/main", "abc"],
            _ => [command, "manager/main", "abc", "Please inspect worker"]
        };
        Assert.Equal(0, await LocalClient.RunCommandAsync(client, command, args,
            new StringWriter(), new StringWriter()));
        Assert.Equal(path, handler.Path);
        Assert.Equal(method, handler.Method);
        if (command == "manager-interrupt")
        {
            using var body = JsonDocument.Parse(handler.Body!);
            Assert.Equal("Please inspect worker",
                body.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task RegistrationCliCarriesWorkerAndSourceBinding()
    {
        using var handler = new JobHandler();
        using var client = Client(handler);
        Assert.Equal(0, await LocalClient.RunCommandAsync(client, "manager-track",
            ["manager-track", "manager/main", "worker-2", "sample/project", "7",
                "--operation", "op-2", "--deadline", "2026-11-01T12:00:00+00:00"],
            new StringWriter(), new StringWriter()));
        Assert.Equal("/managers/main/jobs", handler.Path);
        Assert.Equal("POST", handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("worker-2", body.RootElement.GetProperty("workerConversationId").GetString());
        Assert.Equal("op-2", body.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("sample/project", body.RootElement.GetProperty("repository").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("prNumber").GetInt32());
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T12:00:00+00:00"),
            body.RootElement.GetProperty("deadline").GetDateTimeOffset());
    }

    [Fact]
    public async Task InvalidRegistrationFlagsDoNotSendRequests()
    {
        using var handler = new JobHandler();
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalClient.RunCommandAsync(client, "manager-track",
                ["manager-track", "main", "worker", "--operation"],
                new StringWriter(), new StringWriter()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalClient.RunCommandAsync(client, "manager-track",
                ["manager-track", "main", "worker", "--deadline", "not-a-date"],
                new StringWriter(), new StringWriter()));
        Assert.Null(handler.Path);
    }

    private static HttpClient Client(JobHandler handler) => new(handler) {
        BaseAddress = new Uri("http://127.0.0.1:9875/")
    };

    private sealed class JobHandler : HttpMessageHandler
    {
        public string? Path;
        public string? Method;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Method = request.Method.Method;
            Body = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}")
            };
        }
    }
}
