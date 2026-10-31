using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Neko.Headless;

internal sealed record SupervisorExecutionImage(
    IReadOnlyDictionary<string, string> PinnedFiles,
    IReadOnlyList<string> PinnedDirectories);

internal sealed record SupervisorLaunchManifest(
    string Runtime, string Assembly, string Config, string ExpectedIdentity,
    IReadOnlyDictionary<string, string> PinnedFiles,
    IReadOnlyList<string> PinnedDirectories);

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
    internal static IReadOnlyList<string> PinnedRuntimeDirectories(
        string assembly, HostConfiguration config)
    {
        var host = Path.GetDirectoryName(assembly)
            ?? throw new InvalidOperationException("Host assembly directory unavailable.");
        var provider = Path.GetDirectoryName(config.ProviderAssembly)
            ?? throw new InvalidOperationException("Provider assembly directory unavailable.");
        return new[] { host, provider }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Mutable state inside an executable output directory would invalidate
    // its own pinned JSON files as soon as supervision writes status.json.
    // Refuse this layout BEFORE creating any state or installing startup.
    internal static void RejectStateNestedInExecutionImage(
        string assembly, HostConfiguration config)
    {
        var state = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(config.StateDirectory));
        foreach (var directory in PinnedRuntimeDirectories(assembly, config))
        {
            var root = Path.TrimEndingDirectorySeparator(directory);
            var prefix = Path.EndsInDirectorySeparator(root)
                ? root : root + Path.DirectorySeparatorChar;
            if (StringComparer.OrdinalIgnoreCase.Equals(state, root) ||
                state.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Supervision state directory must be outside executable " +
                    "and provider output directories.");
        }
    }

    // Explicit recursion sees hidden/system dependencies. Reject reparse-point
    // directories and files instead of following mutable junction/symlink
    // targets that could redirect execution around the installed pinset.
    private static IEnumerable<string> EnumerateImageFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        var options = new EnumerationOptions {
            RecurseSubdirectories = false, AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Execution image contains a directory reparse point.");
            foreach (var path in Directory.EnumerateFiles(directory, "*", options))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        "Execution image contains a file reparse point.");
                if (LoadableFile(path)) yield return Path.GetFullPath(path);
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
                pending.Push(child);
        }
    }

    internal static IReadOnlyDictionary<string, string> PinnedRuntimeFiles(
        string runtime, string assembly, HostConfiguration config)
    {
        var files = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        // Provider implementations may be built into a *different* output
        // directory, with native and managed sidecar dependencies. Pin both
        // dependency closures, not just the provider's primary assembly.
        RejectStateNestedInExecutionImage(assembly, config);
        foreach (var directory in PinnedRuntimeDirectories(assembly, config))
            foreach (var path in EnumerateImageFiles(directory))
                files[path] = ManifestDigest(path);
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

    private static bool LoadableFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PinnedFilesMatch(SupervisorExecutionImage? image)
    {
        if (image?.PinnedFiles is not { Count: > 0 } pinned ||
            image.PinnedDirectories is not { Count: > 0 } roots)
            return false;
        try
        {
            foreach (var file in pinned)
            {
                if (!Path.IsPathFullyQualified(file.Key) ||
                    file.Value.Length != 64 ||
                    !StringComparer.OrdinalIgnoreCase.Equals(
                        file.Value, ManifestDigest(file.Key)))
                    return false;
            }
            // A late-added sidecar must fail just as a changed original file
            // does. These roots are part of the registry-pinned manifest.
            foreach (var root in roots)
            {
                if (!Path.IsPathFullyQualified(root) ||
                    !Directory.Exists(root)) return false;
                foreach (var path in EnumerateImageFiles(root))
                    if (!pinned.ContainsKey(path))
                        return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or ArgumentException or
            InvalidOperationException)
        { return false; }
    }

    private static SupervisorLaunchManifest? TrustedManifest(string key)
    {
        using var values = Registry.CurrentUser.OpenSubKey(ManifestKey, writable: false);
        var stored = values?.GetValue(key) as string;
        var launcher = values?.GetValue(key + "-launcher") as string;
        if (stored is not { Length: 64 } || launcher is not { Length: 64 })
            return null;
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    stored, ManifestDigest(ManifestPath(key))) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    launcher, ManifestDigest(InstalledLauncherPath(key))))
                return null;
            var manifest = ReadManifest(ManifestPath(key));
            return PinnedFilesMatch(manifest is null ? null :
                new SupervisorExecutionImage(manifest.PinnedFiles,
                    manifest.PinnedDirectories)) ? manifest : null;
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    private static bool ManifestVerified(string key) => TrustedManifest(key) is not null;

    internal static bool MatchesInstalledCheckout(SupervisorLaunchManifest? manifest,
        string executable, string assembly) =>
        manifest is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(manifest.Runtime), Path.GetFullPath(executable)) &&
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(manifest.Assembly), Path.GetFullPath(assembly));

    // Removal may be necessary after executable drift, so verify only the
    // registry-pinned manifest bytes and original checkout ownership here,
    // without requiring all executable hashes to still be valid.
    private static bool RegisteredCheckoutMatches(
        string key, string executable, string assembly)
    {
        using var values = Registry.CurrentUser.OpenSubKey(ManifestKey, writable: false);
        var digest = values?.GetValue(key) as string;
        if (digest is not { Length: 64 }) return false;
        try
        {
            return StringComparer.OrdinalIgnoreCase.Equals(
                    digest, ManifestDigest(ManifestPath(key))) &&
                MatchesInstalledCheckout(
                    ReadManifest(ManifestPath(key)), executable, assembly);
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    internal static SupervisorExecutionImage InstalledPinset(
        string key, string identity, string executable, string assembly)
    {
        var manifest = TrustedManifest(key) ?? throw new InvalidOperationException(
            "Trusted startup image is unavailable or has changed. Reinstall deliberately.");
        if (manifest.ExpectedIdentity != identity ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(manifest.Runtime), Path.GetFullPath(executable)) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(manifest.Assembly), Path.GetFullPath(assembly)))
            throw new InvalidOperationException("Registered startup identity differs.");
        return new SupervisorExecutionImage(
            manifest.PinnedFiles, manifest.PinnedDirectories);
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
        if (command is "supervise" or "supervisor-install")
            RejectStateNestedInExecutionImage(assembly, config);

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
            // An installed invocation must use the HKCU-backed trusted pinset,
            // not a fresh snapshot of potentially changed executable bytes.
            var image = pinned >= 0
                ? InstalledPinset(keyId, SupervisorIdentity.For(config),
                    executable, assembly)
                : new SupervisorExecutionImage(
                    PinnedRuntimeFiles(executable, assembly, config),
                    PinnedRuntimeDirectories(assembly, config));
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                return await new HostSupervisor(configPath, config,
                    executable, assembly, pinnedImage: image).RunAsync(stop.Token);
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
                PinnedRuntimeFiles(executable, assembly, config),
                PinnedRuntimeDirectories(assembly, config));
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("Cannot open per-user startup registry.");
            if (key.GetValue(valueName) is string previous &&
                (previous != launch ||
                 !RegisteredCheckoutMatches(keyId, executable, assembly)))
                throw new InvalidOperationException(
                    "A different supervisor is already registered for this state directory. " +
                    "Remove that installation from its original checkout before changing it.");
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
            var registered = key?.GetValue(valueName) as string;
            if (registered is not null || File.Exists(ManifestPath(keyId)))
            {
                if (registered is not null && registered != launch ||
                    !RegisteredCheckoutMatches(keyId, executable, assembly))
                    throw new InvalidOperationException(
                        "Startup registration belongs to another checkout or " +
                        "has an invalid manifest. Remove it from the original checkout.");
            }
            if (registered is not null) key!.DeleteValue(valueName);
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
                launcherFailureLog = Path.Combine(
                    Path.GetDirectoryName(ManifestPath(keyId))!,
                    "launcher-failures.log"),
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
