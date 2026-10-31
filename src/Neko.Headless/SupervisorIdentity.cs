using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neko.Headless;

/// <summary>Public identifiers are digests, never secrets or local paths.</summary>
internal static class SupervisorIdentity
{
    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant(); // Windows paths are case-insensitive.

    public static string StateId(string stateDirectory) => Digest(Canonical(stateDirectory));

    private static string ContentDigest(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unreadable contents cannot pass for the previous pinned bytes.
            // A subsequently repaired file changes the identity and requires
            // an explicit reinstall instead of silently switching providers.
            return "UNAVAILABLE";
        }
    }

    // Pin all behaviour- and authorization-changing host configuration, as
    // well as the bytes behind provider and PR CLI paths. A mere path pin is
    // not enough for unattended restart safety.
    public static string For(HostConfiguration config)
    {
        var cli = config.ExternalPrCliExecutable;
        var cliPath = string.IsNullOrWhiteSpace(cli) ? "" : Canonical(cli);
        var cliBytes = string.IsNullOrWhiteSpace(cli) ? "" : ContentDigest(cli);
        var reviewPolicy = config.ReviewGate is null ? "" :
            Digest(JsonSerializer.Serialize(config.ReviewGate));
        return Digest(string.Join("\n",
            Canonical(config.StateDirectory), Canonical(config.ProviderAssembly),
            ContentDigest(config.ProviderAssembly), config.ProviderFactory,
            Canonical(config.ProviderSettings), ContentDigest(config.ProviderSettings),
            config.CreationScope,
            config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            config.AccessToken, cliPath, cliBytes, reviewPolicy));
    }

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

// The supervisor never transmits a bearer credential to an unknown process
// occupying the configured loopback port. Instead, it challenges the host with
// a fresh nonce and verifies the signed, read-only health evidence.
internal sealed record HostHealthAttestation(HostHealth Health, string Nonce,
    string Signature);

internal static class HostAttestation
{
    public static bool ValidNonce(string? nonce) =>
        nonce is { Length: 64 } && nonce.All(c =>
            c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static byte[] Mac(HostHealth health, string nonce, string secret)
    {
        var content = string.Join("\n", "neko-health-attestation-v1", nonce,
            health.Status, health.InstanceId,
            health.UncertainConversations.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            health.PendingCreation ? "1" : "0",
            health.BlockedManagerInboxEntries.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(content));
    }

    public static HostHealthAttestation Create(HostHealth health, string nonce,
        string secret)
    {
        if (!ValidNonce(nonce))
            throw new ArgumentException("Invalid attestation nonce.");
        return new(health, nonce, Convert.ToHexString(Mac(health, nonce, secret)));
    }

    public static bool Verify(HostHealthAttestation? claim, string nonce,
        string secret)
    {
        if (!ValidNonce(nonce) || claim?.Health is null ||
            !StringComparer.Ordinal.Equals(claim.Nonce, nonce) ||
            claim.Signature is not { Length: 64 }) return false;
        try
        {
            var claimed = Convert.FromHexString(claim.Signature);
            return CryptographicOperations.FixedTimeEquals(claimed,
                Mac(claim.Health, nonce, secret));
        }
        catch (FormatException) { return false; }
    }
}

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
