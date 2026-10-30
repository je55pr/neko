namespace Neko.Headless;

/// <summary>
/// Additive local status metadata. Nullable progress values mean this host has
/// no live feed for the turn (for example, after a process restart).
/// </summary>
internal sealed record ConversationStatus(
    string Id,
    string Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    long? ElapsedMilliseconds,
    DateTimeOffset? LastPublicUpdateAt,
    long? PublicEventCount,
    string? LastPublicUpdateType,
    bool? IsTerminal,
    string? TerminalOperation);
