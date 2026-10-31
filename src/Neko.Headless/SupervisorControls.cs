using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Neko.Headless;

internal sealed record SupervisorLaunchManifest(
    string Runtime, string Assembly, string Config, string ExpectedIdentity);

/// <summary>Opt-in HKCU logon startup. Never registers a service or elevates.</summary>
internal static class SupervisorControls
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static string StartupValueName(string stateDirectory) =>
        "NekoSupervisor-" + SupervisorIdentity.StateId(stateDirectory)[..16];

    internal static string FindLaunchScript(string assembly)
    {
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(assembly)!);
            folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "scripts", "supervisor-launch.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            "The tracked supervisor launch script is missing from this checkout.");
    }

    // Windows Run entry command lines are limited to 260 characters.
    // Keep the entry short: paths and the pinned configuration identity live
    // in an ignored local manifest next to the tracked launcher script.
    internal static string StartupCommand(string script, string key) =>
        "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden " +
        "-ExecutionPolicy Bypass -File " + Quote(script) + " -Key " + Quote(key);

    internal static string ManifestPath(string script, string key) =>
        Path.Combine(Path.GetDirectoryName(script)!, "supervisor-" + key + ".local.json");

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static SupervisorLaunchManifest? ReadManifest(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<SupervisorLaunchManifest>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;

    private static void WriteManifest(string path, SupervisorLaunchManifest manifest)
    {
        var existing = ReadManifest(path);
        if (existing is not null)
        {
            if (existing != manifest)
                throw new InvalidOperationException(
                    "Existing startup manifest differs. Remove the previous installation first.");
            return;
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                stream.Flush(flushToDisk: true);
            }
            // Never overwrite a newly created conflicting manifest.
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<int> ExecuteAsync(
        string command, string configPath, HostConfiguration config, string[] args)
    {
        configPath = Path.GetFullPath(configPath);
        var store = new SupervisorStore(config.StateDirectory);
        var valueName = StartupValueName(config.StateDirectory);
        var keyId = SupervisorIdentity.StateId(config.StateDirectory)[..16];
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve host executable.");
        var assembly = Assembly.GetExecutingAssembly().Location;

        if (command == "supervise")
        {
            var pinned = Array.IndexOf(args, "--expected-instance");
            if (pinned >= 0 && (pinned + 1 >= args.Length ||
                args[pinned + 1] != SupervisorIdentity.For(config)))
            {
                Console.Error.WriteLine("Installed configuration identity changed; " +
                    "supervision refused before accessing state. Reinstall deliberately.");
                return 4;
            }
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                return await new HostSupervisor(configPath, config,
                    executable, assembly).RunAsync(stop.Token);
            }
            finally { Console.CancelKeyPress -= handler; }
        }

        if (command == "supervisor-logs")
        {
            foreach (var line in store.Tail()) Console.WriteLine(line);
            return 0;
        }

        if (command == "supervisor-install")
        {
            if (!File.Exists(assembly) || !File.Exists(executable))
                throw new InvalidOperationException("Build the headless host before installation.");
            var script = FindLaunchScript(assembly);
            var launch = StartupCommand(script, keyId);
            if (launch.Length > 260)
                throw new InvalidOperationException(
                    "Checkout path is too long for Windows logon startup. Use a shorter install path.");
            var manifestPath = ManifestPath(script, keyId);
            var manifest = new SupervisorLaunchManifest(
                executable, assembly, configPath, SupervisorIdentity.For(config));
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open per-user startup registry.");
            if (key.GetValue(valueName) is string previous && previous != launch)
                throw new InvalidOperationException(
                    "A different supervisor is already registered for this state directory. " +
                    "Remove that installation explicitly before changing it.");
            WriteManifest(manifestPath, manifest);
            key.SetValue(valueName, launch, RegistryValueKind.String);
            Console.WriteLine("Per-user logon startup installed. No host was started.");
            return 0;
        }

        if (command == "supervisor-remove")
        {
            var script = FindLaunchScript(assembly);
            var launch = StartupCommand(script, keyId);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(valueName) is string existing)
            {
                if (existing != launch)
                    throw new InvalidOperationException(
                        "Registered command differs; remove it from the original checkout.");
                key.DeleteValue(valueName);
            }
            // This is only a local launcher manifest, not host state or a credential.
            var manifestPath = ManifestPath(script, keyId);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            Console.WriteLine("Per-user logon startup removed. Running hosts and state were untouched.");
            return 0;
        }

        if (command == "supervisor-status")
        {
            bool active = false;
            if (Directory.Exists(config.StateDirectory))
            {
                using var lockHandle = SupervisorIdentity.TryLock(
                    Path.Combine(config.StateDirectory, "supervisor.lock"));
                active = lockHandle is null;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var installed = key?.GetValue(valueName) is string;
            using var probe = new SupervisorHealthProbe(config);
            var health = await probe.CheckAsync(CancellationToken.None);
            // Historical files never prove a supervisor is currently alive.
            var report = new
            {
                supervisorActive = active,
                installedForUser = installed,
                lastRecorded = store.Read(),
                hostProbe = health.Kind.ToString().ToLowerInvariant(),
                hostHealth = health.Health
            };
            Console.WriteLine(JsonSerializer.Serialize(report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { WriteIndented = true }));
            return 0;
        }
        throw new ArgumentException("Unknown supervisor command.");
    }
}
