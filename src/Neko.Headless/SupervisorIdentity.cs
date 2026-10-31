using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Neko.Headless;

/// <summary>Public identifiers are digests, never secrets or local paths.</summary>
internal static class SupervisorIdentity
{
    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant(); // Windows paths are case-insensitive.

    public static string StateId(string stateDirectory) => Digest(Canonical(stateDirectory));

    public static string For(HostConfiguration config) => Digest(string.Join("\n",
        Canonical(config.StateDirectory), Canonical(config.ProviderAssembly),
        Canonical(config.ProviderSettings), config.CreationScope,
        config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.AccessToken));

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static FileStream? TryLock(string file)
    {
        try
        {
            return new FileStream(file, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        // Access denied is NOT another owner: it must surface as a fatal error.
    }

    public static bool HostLeaseAvailable(string stateDirectory)
    {
        using var lease = TryLock(Path.Combine(stateDirectory, "host.lock"));
        return lease is not null;
    }

    public static bool LoopbackPortAvailable(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream,
            ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}

internal sealed record HostHealth(
    string Status, string InstanceId, int UncertainConversations,
    bool PendingCreation, int BlockedManagerInboxEntries);

internal static class HostHealthSnapshot
{
    public static HostHealth Create(HostConfiguration config, ChatJournal journal,
        ChatRuntime runtime, ManagerService managers, ManagerInboxService inbox) =>
        new("ready", SupervisorIdentity.For(config),
            runtime.List().Count(record => record.Status == "uncertain"),
            journal.GetPendingCreation() is not null,
            managers.List().Sum(manager => inbox.List(manager.Id)?.Count(message =>
                message.State is InboxStates.Uncertain or InboxStates.Dispatching) ?? 0));
}
