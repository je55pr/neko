using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class SupervisorTests
{
    [Fact]
    public void FreshStatusAndLogReadsDoNotRequireExistingSupervisionDirectory()
    {
        using var folder = new TestFolder();
        var store = new SupervisorStore(Path.Combine(folder.Path, "fresh-state"));
        Assert.Null(store.Read());
        Assert.Empty(store.Tail());
    }

    [Fact]
    public void UnavailableTelemetryFilesDoNotPreventSupervisorStatusAndLogging()
    {
        using var folder = new TestFolder();
        var root = Path.Combine(folder.Path, "supervision");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "status.json"));
        Directory.CreateDirectory(Path.Combine(root, "supervisor.log"));
        var store = new SupervisorStore(folder.Path);
        var status = new SupervisorStatus("running", "test", "instance", 1,
            2, 0, DateTimeOffset.UtcNow);
        store.TryWrite(status); // locked/unwritable destination must fail safely
        store.TryLog("host_alive");
        Assert.True(Directory.Exists(Path.Combine(root, "status.json")));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void RestartBudgetIsBoundedAndRecoversOnlyOutsideSlidingWindow()
    {
        var budget = new SupervisorRestartBudget();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var attempt = 0; attempt < SupervisorRestartBudget.MaxRestarts; attempt++)
        {
            Assert.True(budget.TrySchedule(now.AddSeconds(attempt), out var backoff));
            Assert.Equal(TimeSpan.FromSeconds(1 << attempt), backoff);
        }
        Assert.False(budget.TrySchedule(now.AddMinutes(5), out _));
        Assert.Equal(5, budget.Count);
        Assert.True(budget.TrySchedule(now.AddMinutes(16), out var resetBackoff));
        Assert.Equal(TimeSpan.FromSeconds(1), resetBackoff);
        Assert.Equal(1, budget.Count);
    }

    [Fact]
    public void StateAndConfigurationIdentityAreStableAndDistinctWithoutRawSecrets()
    {
        using var folder = new TestFolder();
        var config = Config(folder.Path);
        var id = SupervisorIdentity.For(config);
        Assert.Equal(id, SupervisorIdentity.For(config with
        {
            StateDirectory = config.StateDirectory.ToUpperInvariant()
        }));
        Assert.NotEqual(id, SupervisorIdentity.For(config with { Port = config.Port + 1 }));
        Assert.NotEqual(id, SupervisorIdentity.For(config with { AccessToken = new string('z', 32) }));
        Assert.NotEqual(id, SupervisorIdentity.For(config with
        {
            StateDirectory = Path.Combine(folder.Path, "other")
        }));
        Assert.DoesNotContain(folder.Path, id);
        Assert.DoesNotContain(config.AccessToken, id);
        Assert.Equal(SupervisorControls.StartupValueName(folder.Path),
            SupervisorControls.StartupValueName(folder.Path.ToUpperInvariant()));
        var key = SupervisorIdentity.StateId(folder.Path)[..16];
        var launch = SupervisorControls.StartupCommand(
            @"C:\some folder\launch.ps1", key);
        Assert.Contains("-Key \"" + key + "\"", launch);
        Assert.DoesNotContain(config.AccessToken, launch);
        Assert.DoesNotContain(folder.Path, launch);
        Assert.True(launch.Length <= 260);
        var manifestPath = SupervisorControls.ManifestPath(key);
        Assert.EndsWith("supervisor-" + key + ".local.json", manifestPath);
        Assert.StartsWith(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), manifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("Neko", "Supervision"), manifestPath,
            StringComparison.OrdinalIgnoreCase);
        var installedScript = SupervisorControls.InstalledLauncherPath(key);
        Assert.StartsWith(Path.GetDirectoryName(manifestPath)!, installedScript,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("supervisor-launch-" + key + ".ps1", installedScript);
        Assert.True(SupervisorControls.StartupCommand(installedScript, key).Length <= 260);
        Assert.Contains("-WindowStyle Hidden", launch);
    }

    [Fact]
    public void StartupManifestDigestDetectsTampering()
    {
        using var folder = new TestFolder();
        var source = Path.Combine(folder.Path, "startup.local.json");
        File.WriteAllText(source, "{\"runtime\":\"trusted\"}");
        var original = SupervisorControls.ManifestDigest(source);
        Assert.Equal(64, original.Length);
        File.WriteAllText(source, "{\"runtime\":\"replaced\"}");
        Assert.NotEqual(original, SupervisorControls.ManifestDigest(source));
    }

    [Fact]
    public void SeparateLeasesPreventConcurrentOwnersWithoutRemovingState()
    {
        using var folder = new TestFolder();
        var journal = new ChatJournal(folder.Path);
        Assert.True(SupervisorIdentity.HostLeaseAvailable(folder.Path));
        using (journal.AcquireHostLease())
        {
            Assert.False(SupervisorIdentity.HostLeaseAvailable(folder.Path));
            using var supervisor = SupervisorIdentity.TryLock(
                Path.Combine(folder.Path, "supervisor.lock"));
            Assert.NotNull(supervisor);
            Assert.Null(SupervisorIdentity.TryLock(
                Path.Combine(folder.Path, "supervisor.lock")));
        }
        Assert.True(SupervisorIdentity.HostLeaseAvailable(folder.Path));
    }

    [Fact]
    public async Task HealthProbeRequiresMatchingAuthenticatedInstance()
    {
        using var folder = new TestFolder();
        var config = Config(folder.Path);
        using var handler = new ReplyHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(config.AccessToken, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new HostHealth(
                    "ready", SupervisorIdentity.For(config), 2, true, 1))
            };
        });
        using var probe = new SupervisorHealthProbe(config, handler);
        var healthy = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(HostProbeKind.Ready, healthy.Kind);
        Assert.Equal(2, healthy.Health?.UncertainConversations);
        Assert.True(healthy.Health?.PendingCreation);
        Assert.Equal(1, healthy.Health?.BlockedManagerInboxEntries);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "ready", "Unauthorized")]
    [InlineData(HttpStatusCode.OK, "ready", "DifferentInstance")]
    [InlineData(HttpStatusCode.OK, "starting", "Unhealthy")]
    public async Task InvalidOrForeignHealthNeverCountsAsReady(
        HttpStatusCode responseCode, string status, string expected)
    {
        using var folder = new TestFolder();
        var config = Config(folder.Path);
        using var handler = new ReplyHandler(_ => new HttpResponseMessage(responseCode)
        {
            Content = JsonContent.Create(new HostHealth(status, "wrong-state",
                0, false, 0))
        });
        using var probe = new SupervisorHealthProbe(config, handler);
        Assert.Equal(expected, (await probe.CheckAsync(CancellationToken.None)).Kind.ToString());
    }

    [Fact]
    public async Task ExistingHostLeaseStopsSupervisorFromStartingAnyChild()
    {
        using var folder = new TestFolder();
        var config = Config(folder.Path);
        var path = SaveConfig(folder.Path, config);
        using var hostLease = new ChatJournal(folder.Path).AcquireHostLease();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var supervisor = new HostSupervisor(path, config, LocalNoopExecutable(),
            typeof(HostSupervisor).Assembly.Location,
            () => throw new InvalidOperationException("Should not start under a held lease."));
        Assert.Equal(0, await supervisor.RunAsync(cancel.Token));
        var logs = new SupervisorStore(folder.Path).Tail();
        Assert.Contains(logs, line => line.Contains("supervisor_started"));
        Assert.DoesNotContain(logs, line => line.Contains("host_process_launched"));
        Assert.True(File.Exists(Path.Combine(folder.Path, "host.lock")));
    }

    [Fact]
    public async Task ExitedLocalProcessDoesNotReplayOrClearUncertainEvidence()
    {
        using var folder = new TestFolder();
        var config = Config(folder.Path);
        var path = SaveConfig(folder.Path, config);
        var journal = new ChatJournal(folder.Path);
        journal.Set(new ChatRecord("saved-chat", "uncertain", DispatchCount: 1,
            PendingTurn: new PendingTurnEvidence("original-digest", ["before"], true)));
        Assert.True(journal.TryStartCreation("creation-digest", "manager/main"));
        var inbox = new ManagerInboxJournal(folder.Path);
        var queued = inbox.Enqueue("manager/main", "queued", null).Message;
        var accepted = inbox.Enqueue("manager/main", "accepted", null).Message;
        var uncertain = inbox.Enqueue("manager/main", "uncertain", null).Message;
        Assert.NotNull(inbox.Transition("manager/main", accepted.Id, InboxStates.Queued,
            message => message with { State = InboxStates.Accepted,
                ConversationId = "saved-chat", RemoteMessageId = "receipt" }));
        Assert.NotNull(inbox.Transition("manager/main", uncertain.Id, InboxStates.Queued,
            message => message with { State = InboxStates.Uncertain }));

        // A local cmd.exe with /C exit is a promptly exiting surrogate child.
        // No browser/provider/session is opened and no external operation is attempted.
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var supervisor = new HostSupervisor(path, config, LocalNoopExecutable(),
            typeof(HostSupervisor).Assembly.Location, () =>
            {
                var command = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                command.ArgumentList.Add("/C");
                command.ArgumentList.Add("exit");
                command.ArgumentList.Add("42");
                return Process.Start(command)!;
            });
        Assert.Equal(0, await supervisor.RunAsync(cancel.Token));

        var recovered = new ChatJournal(folder.Path);
        Assert.Equal("uncertain", recovered.Get("saved-chat")?.Status);
        Assert.Equal(1, recovered.Get("saved-chat")?.DispatchCount);
        Assert.Equal("original-digest", recovered.Get("saved-chat")?.PendingTurn?.PromptSha256);
        Assert.NotNull(recovered.GetPendingCreation());
        var restoredInbox = new ManagerInboxJournal(folder.Path).List("manager/main");
        Assert.Equal([InboxStates.Queued, InboxStates.Accepted, InboxStates.Uncertain],
            restoredInbox.Select(message => message.State));
        Assert.Equal([queued.Id, accepted.Id, uncertain.Id],
            restoredInbox.Select(message => message.Id));
        Assert.Equal("receipt", restoredInbox[1].RemoteMessageId);
        var logs = new SupervisorStore(folder.Path).Tail();
        Assert.Contains(logs, line => line.Contains("host_process_exited_code_"));
        Assert.Contains(logs, line => line.Contains("restart_scheduled"));
        Assert.DoesNotContain(logs, line => line.Contains("send_") ||
            line.Contains("reconcile_") || line.Contains("creation_cleared"));
    }

    private static string LocalNoopExecutable()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "whoami.exe");
        Assert.True(File.Exists(file));
        return file;
    }

    private static HostConfiguration Config(string stateDirectory) =>
        new(Path.Combine(stateDirectory, "not-installed.dll"), "Unused.Factory",
            Path.Combine(stateDirectory, "not-installed.json"), "scope-test",
            new string('s', 32), stateDirectory, FreePort());

    private static string SaveConfig(string folder, HostConfiguration config)
    {
        var path = Path.Combine(folder, "isolated-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken _) =>
            Task.FromResult(reply(request));
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "neko-supervisor-tests-" + Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
