using Neko.Headless;

namespace Neko.Headless.Tests;

public sealed class SupervisorPinnedConfigTests
{
    [Fact]
    public async Task ChangedInstalledIdentityRefusesBeforeWritingAnyState()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "neko-supervisor-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var config = new HostConfiguration(
                Path.Combine(folder, "provider-image", "unused.dll"), "Unused.Factory",
                Path.Combine(folder, "unused.json"), "scope",
                new string('a', 32), Path.Combine(folder, "new-state"), 8765);
            var result = await SupervisorControls.ExecuteAsync(
                "supervise", Path.Combine(folder, "private.json"), config,
                ["supervise", "--expected-instance", new string('0', 64)]);
            Assert.Equal(4, result);
            Assert.False(Directory.Exists(config.StateDirectory));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
