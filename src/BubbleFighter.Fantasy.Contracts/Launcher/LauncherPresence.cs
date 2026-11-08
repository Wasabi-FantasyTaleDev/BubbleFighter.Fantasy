namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>Captures authenticated login owners for private, bounded node reports.</summary>
public interface ILocalLauncherPresence
{
    /// <summary>Returns account identities once across local builds; names are omitted unless disclosure is requested and enabled.</summary>
    IReadOnlyList<LauncherPresenceMember> Capture(bool includeRoster);
}

/// <summary>One private account observation; account numbers never enter the public Launcher response.</summary>
/// <param name="UserNum">The account identifier in the reporting node's verified database domain.</param>
/// <param name="BuildId">The actual owning game session's build.</param>
/// <param name="Name">The display name, or null when roster disclosure is disabled.</param>
/// <param name="Level">The display level, or null alongside an undisclosed name.</param>
/// <param name="Migrating">Whether this is an outgoing session awaiting transfer.</param>
public sealed record LauncherPresenceMember(int UserNum, string BuildId, string? Name, int? Level, bool Migrating = false);

/// <summary>A public player card containing no account identifier or connection address.</summary>
/// <param name="Name">The display nickname.</param>
/// <param name="Level">The current level.</param>
/// <param name="BuildId">The build used by the owning game connection.</param>
public sealed record LauncherRosterPlayer(string Name, int Level, string BuildId);

/// <summary>An observed population of an explicitly configured group, independent of selector aliases.</summary>
/// <param name="OnlinePlayers">Unique accounts in fresh node reports, including undisclosed accounts.</param>
/// <param name="PeakOnlinePlayers">The persisted maximum observed population of this membership, or null when unavailable.</param>
/// <param name="Complete">Whether every configured member has a fresh report for its verified data domain.</param>
/// <param name="RosterComplete">Whether the snapshot includes every counted account's public card.</param>
/// <param name="Players">Disclosed player cards only; empty for count-only requests.</param>
public sealed record LauncherPresenceSnapshot(int OnlinePlayers, int? PeakOnlinePlayers, bool Complete,
    bool RosterComplete, IReadOnlyList<LauncherRosterPlayer> Players);

/// <summary>Reads population groups without coupling their members to automatic placement eligibility.</summary>
public interface ILauncherPresenceDirectory
{
    /// <summary>Gets configured group identities so consumers reject an unknown group during activation.</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>Returns a fresh bounded snapshot; count-only requests must not return public roster details.</summary>
    ValueTask<LauncherPresenceSnapshot> ReadAsync(string group, bool includeRoster, CancellationToken ct);
}
