using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Neko.Headless;

internal sealed record SupervisorLaunchManifest(
    string Runtime, string Assembly, string Config, string ExpectedIdentity,
    IReadOnlyDictionary<string, string> PinnedFiles);

/// <summary>Opt-in HKCU logon startup. Never registers a service or elevates.</summary>
internal static class SupervisorControls
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ManifestKey = @"Software\Neko\SupervisorManifests";

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
    // in a per-user private app-data manifest, not the source checkout.
    internal static string StartupCommand(string script, string key) =>
        "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden " +
        "-ExecutionPolicy Bypass -File " + Quote(script) + " -Key " + Quote(key);

    internal static string ManifestPath(string key) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Neko", "Supervision", "supervisor-" + key + ".local.json");

    internal static string InstalledLauncherPath(string key) =>
        Path.Combine(Path.GetDirectoryName(ManifestPath(key))!,
            "supervisor-launch-" + key + ".ps1");

    internal static string ManifestDigest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Pin the actual executable image and loadable dependencies, not merely
    // the startup script and the JSON containing paths. Any changed output
    // requires explicit uninstall/reinstall before the next logon.
    internal static IReadOnlyDictionary<string, string> PinnedRuntimeFiles(
        string runtime, string assembly, HostConfiguration config)
    {
        var folder = Path.GetDirectoryName(assembly)
            ?? throw new InvalidOperationException("Host assembly directory unavailable.");
        var files = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*",
                     SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                files[Path.GetFullPath(path)] = ManifestDigest(path);
        }
        foreach (var path in new[] { runtime, assembly, config.ProviderAssembly,
                     config.ProviderSettings })
            files[Path.GetFullPath(path)] = ManifestDigest(path);
        if (!string.IsNullOrWhiteSpace(config.ExternalPrCliExecutable))
        {
            if (!Path.IsPathFullyQualified(config.ExternalPrCliExecutable))
                throw new InvalidOperationException(
                    "Supervised PR CLI must be an explicit absolute executable path.");
            var cli = Path.GetFullPath(config.ExternalPrCliExecutable);
            files[cli] = ManifestDigest(cli);
        }
        return files;
    }

    private static bool ManifestVerified(string key)
    {
        using var values = Registry.CurrentUser.OpenSubKey(ManifestKey, writable: false);
        var stored = values?.GetValue(key) as string;
        var launcher = values?.GetValue(key + "-launcher") as string;
        if (stored is not { Length: 64 } || launcher is not { Length: 64 })
            return false;
        try { return StringComparer.OrdinalIgnoreCase.Equals(
            stored, ManifestDigest(ManifestPath(key))) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                launcher, ManifestDigest(InstalledLauncherPath(key))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static SupervisorLaunchManifest? ReadManifest(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<SupervisorLaunchManifest>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;

    // The source checkout is not a logon-time trust boundary. Install a
    // private, fixed copy of the launcher, refusing silent replacement.
    private static void WriteLauncher(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    ManifestDigest(source), ManifestDigest(destination)))
                throw new InvalidOperationException(
                    "Installed launcher differs. Remove previous registration first.");
            return;
        }
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp);
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteManifest(string path, SupervisorLaunchManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = ReadManifest(path);
        if (existing is not null)
        {
            if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(manifest))
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
            var sourceScript = FindLaunchScript(assembly);
            var script = InstalledLauncherPath(keyId);
            var launch = StartupCommand(script, keyId);
            if (launch.Length > 260)
                throw new InvalidOperationException(
                    "Private launcher path is too long for Windows logon startup.");
            var manifestPath = ManifestPath(keyId);
            var manifest = new SupervisorLaunchManifest(
                executable, assembly, configPath, SupervisorIdentity.For(config),
                PinnedRuntimeFiles(executable, assembly, config));
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open per-user startup registry.");
            if (key.GetValue(valueName) is string previous && previous != launch)
                throw new InvalidOperationException(
                    "A different supervisor is already registered for this state directory. " +
                    "Remove that installation explicitly before changing it.");
            WriteLauncher(sourceScript, script);
            WriteManifest(manifestPath, manifest);
            using var digests = Registry.CurrentUser.CreateSubKey(ManifestKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open trusted startup registry.");
            var digest = ManifestDigest(manifestPath);
            var scriptDigest = ManifestDigest(script);
            if (digests.GetValue(keyId + "-launcher") is string oldScript &&
                !StringComparer.OrdinalIgnoreCase.Equals(oldScript, scriptDigest))
                throw new InvalidOperationException(
                    "Registered launcher digest differs. Remove and reinstall deliberately.");
            if (digests.GetValue(keyId) is string previousDigest &&
                !StringComparer.OrdinalIgnoreCase.Equals(previousDigest, digest))
                throw new InvalidOperationException(
                    "Registered startup manifest digest differs. Remove and reinstall deliberately.");
            digests.SetValue(keyId, digest, RegistryValueKind.String);
            digests.SetValue(keyId + "-launcher", scriptDigest, RegistryValueKind.String);
            key.SetValue(valueName, launch, RegistryValueKind.String);
            Console.WriteLine("Per-user logon startup installed. No host was started.");
            return 0;
        }

        if (command == "supervisor-remove")
        {
            var script = InstalledLauncherPath(keyId);
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
            using var digests = Registry.CurrentUser.OpenSubKey(ManifestKey, writable: true);
            digests?.DeleteValue(keyId, throwOnMissingValue: false);
            digests?.DeleteValue(keyId + "-launcher", throwOnMissingValue: false);
            var manifestPath = ManifestPath(keyId);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            if (File.Exists(script)) File.Delete(script);
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
                startupManifestVerified = installed && ManifestVerified(keyId),
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
