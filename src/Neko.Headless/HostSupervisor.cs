using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Neko.Headless;

internal sealed record SupervisorStatus(
    string State, string Reason, string InstanceId, int SupervisorPid,
    int? ChildPid, int Restarts, DateTimeOffset UpdatedAt,
    bool Healthy = false, int? UncertainConversations = null,
    bool? PendingCreation = null, int? BlockedManagerInboxEntries = null);

/// <summary>Only observations about process lifecycle are stored here; no prompts or tokens.</summary>
internal sealed class SupervisorStore
{
    private readonly string _folder;
    private readonly string _status;
    public string LogFile => Path.Combine(_folder, "supervisor.log");
    public SupervisorStore(string stateDirectory)
    {
        _folder = Path.Combine(stateDirectory, "supervision");
        _status = Path.Combine(_folder, "status.json");
    }

    public void Write(SupervisorStatus status)
    {
        Directory.CreateDirectory(_folder);
        var temporary = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, status,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _status, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public SupervisorStatus? Read()
    {
        try
        {
            using var stream = new FileStream(_status, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<SupervisorStatus>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Log(string eventName)
    {
        Directory.CreateDirectory(_folder);
        File.AppendAllText(LogFile,
            DateTimeOffset.UtcNow.ToString("O") + " " + eventName + Environment.NewLine);
    }

    // Telemetry is diagnostic only. A locked or temporarily unwritable status
    // file must never become a reason to abandon a running healthy host.
    public void TryWrite(SupervisorStatus status)
    {
        try { Write(status); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Supervisor status unavailable: " + error.GetType().Name); }
    }

    public void TryLog(string eventName)
    {
        try { Log(eventName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Supervisor log unavailable: " + error.GetType().Name); }
    }

    public IReadOnlyList<string> Tail(int count = 60)
    {
        try
        {
            using var file = new FileStream(LogFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > count) lines.Dequeue();
            }
            return lines.ToArray();
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
}

/// <summary>Windowed crash budget: no endless process relaunches or retry of remote operations.</summary>
internal sealed class SupervisorRestartBudget
{
    public const int MaxRestarts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly Queue<DateTimeOffset> _attempts = new();
    public int Count => _attempts.Count;

    public bool TrySchedule(DateTimeOffset now, out TimeSpan backoff)
    {
        while (_attempts.TryPeek(out var oldest) && now - oldest >= Window)
            _attempts.Dequeue();
        if (_attempts.Count >= MaxRestarts)
        {
            backoff = TimeSpan.Zero;
            return false;
        }
        _attempts.Enqueue(now);
        backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << (_attempts.Count - 1)));
        return true;
    }
}

internal enum HostProbeKind
{
    Ready, Unreachable, Unauthorized, DifferentInstance, Unhealthy
}

internal sealed record HostProbe(HostProbeKind Kind, HostHealth? Health = null);

internal sealed class SupervisorHealthProbe : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _instanceId;
    private readonly string _secret;

    public SupervisorHealthProbe(HostConfiguration config,
        HttpMessageHandler? handler = null)
    {
        _instanceId = SupervisorIdentity.For(config);
        _secret = config.AccessToken;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{config.Port}/");
        _client.Timeout = TimeSpan.FromSeconds(2);
        // Deliberately never add a bearer header. Port ownership is untrusted.
    }

    public async Task<HostProbe> CheckAsync(CancellationToken token)
    {
        try
        {
            var nonce = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            using var response = await _client.GetAsync(
                "health/attest?nonce=" + nonce, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new(HostProbeKind.Unauthorized);
            if (!response.IsSuccessStatusCode)
                return new(HostProbeKind.Unhealthy);
            var attestation = await response.Content.ReadFromJsonAsync<HostHealthAttestation>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
            // A rogue listener can fabricate any JSON but cannot authenticate
            // a fresh nonce. Never trust a claimed identity without its MAC.
            if (!HostAttestation.Verify(attestation, nonce, _secret))
                return new(HostProbeKind.DifferentInstance);
            var health = attestation!.Health;
            if (health.InstanceId != _instanceId)
                return new(HostProbeKind.DifferentInstance);
            return health.Status == "ready" ? new(HostProbeKind.Ready, health)
                : new(HostProbeKind.Unhealthy);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(HostProbeKind.Unreachable);
        }
        catch (HttpRequestException)
        {
            return new(HostProbeKind.Unreachable);
        }
        catch (JsonException)
        {
            return new(HostProbeKind.Unhealthy);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// User-session supervisor. It NEVER calls a create/send/reconcile endpoint.
/// It only launches the original serve command after verifying leases and port ownership.
/// </summary>
internal sealed class HostSupervisor
{
    private readonly string _configPath;
    private readonly HostConfiguration _config;
    private readonly string _identity;
    private readonly SupervisorStore _store;
    private readonly SupervisorRestartBudget _budget = new();
    private readonly string _executable;
    private readonly string _assembly;
    private readonly Func<Process>? _startOverride;
    private readonly int _pid = Environment.ProcessId;

    public HostSupervisor(string configPath, HostConfiguration config,
        string executable, string assembly, Func<Process>? startOverride = null)
    {
        _configPath = Path.GetFullPath(configPath);
        _config = config;
        _identity = SupervisorIdentity.For(config);
        _store = new SupervisorStore(config.StateDirectory);
        _executable = Path.GetFullPath(executable);
        _assembly = Path.GetFullPath(assembly);
        _startOverride = startOverride;
    }

    private void Publish(string state, string reason, int? childPid = null,
        HostHealth? health = null, bool healthy = false)
    {
        _store.TryWrite(new SupervisorStatus(state, reason, _identity, _pid,
            childPid, _budget.Count, DateTimeOffset.UtcNow, healthy,
            health?.UncertainConversations, health?.PendingCreation,
            health?.BlockedManagerInboxEntries));
    }

    public async Task<int> RunAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_config.StateDirectory);
        // Separate from host.lock: two logon/interactive supervisors cannot launch two children.
        using var owner = SupervisorIdentity.TryLock(
            Path.Combine(_config.StateDirectory, "supervisor.lock"));
        if (owner is null)
        {
            Console.Error.WriteLine("A supervisor already owns this state directory.");
            return 3;
        }

        _store.TryLog("supervisor_started");
        Publish("starting", "supervisor_started");
        using var probe = new SupervisorHealthProbe(_config);
        try
        {
            while (!token.IsCancellationRequested)
            {
                HostConfiguration fresh;
                try { fresh = HostConfiguration.Read(_configPath); }
                catch (Exception)
                {
                    _store.TryLog("configuration_unreadable");
                    Publish("blocked", "configuration_unreadable");
                    return 4;
                }
                if (SupervisorIdentity.For(fresh) != _identity)
                {
                    _store.TryLog("configuration_changed_restart_requires_operator");
                    Publish("blocked", "configuration_changed");
                    return 4;
                }

                if (!SupervisorIdentity.HostLeaseAvailable(_config.StateDirectory))
                {
                    // A manually launched host may already own the journal. Monitor it,
                    // but never send work, clear its lease, or launch another host.
                    var running = await probe.CheckAsync(token);
                    if (running.Kind is HostProbeKind.DifferentInstance or
                        HostProbeKind.Unauthorized)
                    {
                        _store.TryLog("external_owner_configuration_or_auth_mismatch");
                        Publish("blocked", "external_owner_configuration_or_auth_mismatch");
                        return 5;
                    }
                    Publish("external_owner",
                        running.Kind == HostProbeKind.Ready
                            ? "existing_host_ready"
                            : "existing_host_not_ready",
                        health: running.Health, healthy: running.Kind == HostProbeKind.Ready);
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    continue;
                }

                // Lease is free. A port held by something else is NOT a healthy host
                // to adopt: starting anyway could attach to a different state.
                if (!SupervisorIdentity.LoopbackPortAvailable(_config.Port))
                {
                    var conflict = await probe.CheckAsync(token);
                    _store.TryLog("port_occupied_without_state_owner");
                    Publish("blocked", conflict.Kind == HostProbeKind.DifferentInstance
                        ? "port_owned_by_other_configuration"
                        : "port_occupied_without_state_owner");
                    return 5;
                }

                Process child;
                try
                {
                    child = StartChild();
                }
                catch (Exception)
                {
                    _store.TryLog("host_process_launch_failed");
                    Publish("blocked", "host_process_launch_failed");
                    return 5;
                }
                using (child)
                {
                    _store.TryLog("host_process_launched");
                    Publish("starting", "waiting_for_readiness", child.Id);
                    var started = DateTimeOffset.UtcNow;
                    var exited = child.WaitForExitAsync(token);
                    string? lastReason = null;
                    while (!child.HasExited && !token.IsCancellationRequested)
                    {
                        var check = await probe.CheckAsync(token);
                        var reason = check.Kind switch
                        {
                            HostProbeKind.Ready => "ready",
                            HostProbeKind.DifferentInstance => "health_identity_mismatch",
                            HostProbeKind.Unauthorized => "health_auth_mismatch",
                            _ when DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(30)
                                => "host_alive_but_not_ready",
                            _ => "waiting_for_readiness"
                        };
                        if (check.Kind is HostProbeKind.DifferentInstance or
                            HostProbeKind.Unauthorized)
                        {
                            // An authenticated identity conflict is not a
                            // transient health failure. Never kill an uncertain
                            // child or launch another one; leave its PID in a
                            // durable blocked record for manual diagnosis.
                            _store.TryLog("health_conflict_requires_operator");
                            Publish("blocked", "health_conflict_child_left_running",
                                child.Id);
                            return 5;
                        }
                        if (reason != lastReason)
                        {
                            _store.TryLog("host_health_" + reason);
                            lastReason = reason;
                        }
                        var healthy = check.Kind == HostProbeKind.Ready;
                        Publish(healthy ? "running" : "unhealthy", reason,
                            child.Id, check.Health, healthy);
                        // Wake immediately for a proven exit, even during the health poll interval.
                        // Health failure alone never causes a restart.
                        await Task.WhenAny(exited,
                            Task.Delay(TimeSpan.FromSeconds(2), token));
                    }

                    if (token.IsCancellationRequested) break;
                    // HasExited gates restart. No remote activity, receipt replay,
                    // optimistic state clearing or premature child kill occurs.
                    var exitCode = child.ExitCode;
                    _store.TryLog("host_process_exited_code_" + exitCode);
                    Publish("exited", "host_process_exited", child.Id);
                }

                if (!_budget.TrySchedule(DateTimeOffset.UtcNow, out var delay))
                {
                    _store.TryLog("restart_budget_exhausted");
                    Publish("exhausted", "restart_budget_exhausted");
                    return 6;
                }
                _store.TryLog("restart_scheduled");
                Publish("backoff", "process_exited_restarting");
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // Stop does not kill the host. The host's exclusive lease remains its
        // own responsibility and a new supervisor will observe an external owner.
        _store.TryLog("supervisor_stopped_host_not_killed");
        Publish("stopped", "supervisor_stopped_host_not_killed");
        return 0;
    }

    private Process StartChild()
    {
        if (_startOverride is not null) return _startOverride();
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_configPath)!
        };
        if (Path.GetFileNameWithoutExtension(_executable)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(_assembly);
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(_configPath);
        return Process.Start(start) ?? throw new InvalidOperationException("Host failed to start.");
    }
}
