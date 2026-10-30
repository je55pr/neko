using System.Reflection;
using System.Text.Json;
using Neko;

namespace Neko.Headless;

internal sealed record HostConfiguration(
    string ProviderAssembly, string ProviderFactory, string ProviderSettings,
    string CreationScope, string AccessToken, string StateDirectory, int Port = 8765)
{
    public static HostConfiguration Read(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new InvalidOperationException("Local configuration file not found.");
        var config = JsonSerializer.Deserialize<HostConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Invalid local configuration.");
        if (string.IsNullOrWhiteSpace(config.CreationScope) ||
            string.IsNullOrWhiteSpace(config.AccessToken) || config.AccessToken.Length < 24 ||
            config.Port < 1024 || config.Port > 65535)
            throw new InvalidOperationException("Creation scope, access token and valid port are required.");
        if (string.IsNullOrWhiteSpace(config.ProviderAssembly) ||
            string.IsNullOrWhiteSpace(config.ProviderFactory) ||
            string.IsNullOrWhiteSpace(config.ProviderSettings))
            throw new InvalidOperationException("Provider assembly, factory and settings are required.");
        var baseDir = Path.GetDirectoryName(path)!;
        return config with
        {
            ProviderAssembly = Resolve(baseDir, config.ProviderAssembly),
            ProviderSettings = Resolve(baseDir, config.ProviderSettings),
            StateDirectory = Resolve(baseDir,
                string.IsNullOrWhiteSpace(config.StateDirectory) ? "state" : config.StateDirectory)
        };
    }

    private static string Resolve(string baseDir, string value) =>
        Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value));

    public IConversationProvider CreateProvider()
    {
        if (!File.Exists(ProviderAssembly) || !File.Exists(ProviderSettings))
            throw new InvalidOperationException("Configured provider files are unavailable.");
        var assembly = Assembly.LoadFrom(ProviderAssembly);
        var type = assembly.GetType(ProviderFactory, throwOnError: true)!;
        if (Activator.CreateInstance(type) is not IConversationProviderFactory factory)
            throw new InvalidOperationException("The configured factory does not implement the provider contract.");
        return new ScopedConversationProvider(factory.Create(ProviderSettings), CreationScope);
    }
}
