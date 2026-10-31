using System.Diagnostics;
using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class SupervisorExecutionBoundaryTests
{
    [Fact]
    public async Task MutableStateNestedUnderEitherOutputIsRefusedBeforeAnyWrite()
    {
        using var folder = new ScratchFolder();
        var hostDir = Path.Combine(folder.Path, "host");
        var providerDir = Path.Combine(folder.Path, "provider");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(providerDir);
        var assembly = Path.Combine(hostDir, "Neko.Headless.dll");
        var provider = Path.Combine(providerDir, "Neko.Transport.dll");
        foreach (var root in new[] { hostDir, providerDir })
        {
            var state = Path.Combine(root, "mutable-state");
            var config = new HostConfiguration(provider, "Unused.Factory",
                Path.Combine(folder.Path, "provider-settings.json"), "scope",
                new string('s', 32), state);
            Assert.Throws<InvalidOperationException>(() =>
                SupervisorControls.RejectStateNestedInExecutionImage(
                    assembly, config));
            var supervisor = new HostSupervisor(
                Path.Combine(folder.Path, "isolated-config.json"), config,
                assembly, assembly, () => throw new Exception(
                    "Nested mutable state must not start a child."));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                supervisor.RunAsync(CancellationToken.None));
            Assert.False(Directory.Exists(state));
        }

        var outside = new HostConfiguration(provider, "Unused.Factory",
            Path.Combine(folder.Path, "provider-settings.json"), "scope",
            new string('s', 32), Path.Combine(folder.Path, "state"));
        SupervisorControls.RejectStateNestedInExecutionImage(
            assembly, outside);
    }

    [Fact]
    public async Task RealPowerShellStartupInventorySeesHiddenSystemDlls()
    {
        using var folder = new ScratchFolder();
        var image = Path.Combine(folder.Path, "image");
        Directory.CreateDirectory(image);
        var normal = Path.Combine(image, "provider.dll");
        File.WriteAllText(normal, "known");
        var pinned = new SupervisorExecutionImage(
            new Dictionary<string, string> {
                [normal] = SupervisorControls.ManifestDigest(normal)
            }, [image]);
        Assert.True(SupervisorControls.PinnedFilesMatch(pinned));
        var baseline = await RunActualLauncherInventoryAsync(image, normal);
        Assert.True(baseline.ExitCode == 0, baseline.Output);

        var hidden = Path.Combine(image, "late-hidden.dll");
        File.WriteAllText(hidden, "unregistered");
        try
        {
            File.SetAttributes(hidden,
                FileAttributes.Hidden | FileAttributes.System);
            Assert.False(SupervisorControls.PinnedFilesMatch(pinned));
            var after = await RunActualLauncherInventoryAsync(image, normal);
            Assert.Equal(7, after.ExitCode);
            Assert.Contains("REJECTED", after.Output);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task RealPowerShellStartupInventoryRejectsAddedJunction()
    {
        using var folder = new ScratchFolder();
        var image = Path.Combine(folder.Path, "image");
        var outside = Path.Combine(folder.Path, "outside");
        Directory.CreateDirectory(image);
        Directory.CreateDirectory(outside);
        var normal = Path.Combine(image, "provider.dll");
        File.WriteAllText(normal, "known");
        File.WriteAllText(Path.Combine(outside, "native.dll"), "unexpected");
        var pinned = new SupervisorExecutionImage(
            new Dictionary<string, string> {
                [normal] = SupervisorControls.ManifestDigest(normal)
            }, [image]);
        var junction = Path.Combine(image, "runtimes");
        var makeLink = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = "/C mklink /J \"" + junction + "\" \"" + outside + "\""
        };
        using (var command = Process.Start(makeLink)!)
        {
            await command.WaitForExitAsync();
            Assert.Equal(0, command.ExitCode);
        }
        try
        {
            Assert.True((File.GetAttributes(junction) &
                FileAttributes.ReparsePoint) != 0);
            Assert.False(SupervisorControls.PinnedFilesMatch(pinned));
            var result = await RunActualLauncherInventoryAsync(image, normal);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("REJECTED", result.Output);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    // Execute the FunctionDefinitionAst extracted from the tracked launcher,
    // without executing any HKCU startup registration or touching live state.
    private static async Task<(int ExitCode, string Output)>
        RunActualLauncherInventoryAsync(string directory, string pinnedFile)
    {
        var script = SupervisorControls.FindLaunchScript(
            typeof(HostSupervisor).Assembly.Location);
        static string Quote(string value) => "'" +
            value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var command =
            "$ErrorActionPreference='Stop';" +
            "$tokens=$null;$errors=$null;" +
            "$ast=[System.Management.Automation.Language.Parser]::ParseFile(" +
                Quote(script) + ",[ref]$tokens,[ref]$errors);" +
            "if($errors.Count -gt 0){throw 'Script parse failed'};" +
            "$fn=$ast.Find({param($n) $n -is " +
                "[System.Management.Automation.Language.FunctionDefinitionAst] " +
                "-and $n.Name -eq 'Assert-PinnedDirectoryInventory'},$true);" +
            "if($null -eq $fn){throw 'Inventory function missing'};" +
            "Invoke-Expression $fn.Extent.Text;" +
            "$p=@{};$p[" + Quote(pinnedFile) + "]='installed-digest';" +
            "try { Assert-PinnedDirectoryInventory -Directories @(" +
                Quote(directory) + ") -Pinned $p;" +
                "Write-Output 'MATCHED'; exit 0 }" +
            "catch { Write-Output ('REJECTED:' + $_.Exception.Message); exit 7 }";
        var start = new ProcessStartInfo("powershell.exe") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        // EncodedCommand avoids differing Windows shell argument quoting.
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes(command)));
        using var child = Process.Start(start)!;
        // A cold Windows PowerShell 5.1 startup on a shared CI runner may take
        // much longer than on the developer machine. Keep the subprocess
        // bounded, but avoid a flaky 15-second deadline.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            var message = await stdout + "\n" + await stderr;
            return (child.ExitCode, message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException(
                "Isolated PowerShell inventory test did not exit within 90 seconds.");
        }
    }

    private sealed class ScratchFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "neko-supervisor-image-" + Guid.NewGuid().ToString("N"));

        public ScratchFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
