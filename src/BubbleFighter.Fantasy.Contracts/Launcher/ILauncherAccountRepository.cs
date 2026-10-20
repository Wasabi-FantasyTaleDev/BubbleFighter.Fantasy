namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>Stored launcher credentials and the corresponding game identity, when a character exists.</summary>
/// <param name="Username">Normalized account login name.</param>
/// <param name="PasswordHash">Versioned password hash, or empty for a legacy identity without credentials; never included in API responses.</param>
/// <param name="UserNumber">Existing game player id, or null before first game login.</param>
/// <param name="Nickname">Existing character name, when present.</param>
/// <param name="BannedUntil">UTC account suspension end, or null.</param>
public sealed record LauncherAccount(string Username, string PasswordHash, long? UserNumber, string? Nickname, DateTime? BannedUntil);

/// <summary>Persists launcher credentials independently of transient HTTP sessions.</summary>
public interface ILauncherAccountRepository
{
    /// <summary>Looks up one normalized login name without creating a game character.</summary>
    ValueTask<LauncherAccount?> FindAsync(string username, CancellationToken cancellationToken);

    /// <summary>Creates credentials only for an unclaimed name; existing game identities cannot be claimed through registration.</summary>
    ValueTask<bool> RegisterAsync(string username, string passwordHash, CancellationToken cancellationToken);

    /// <summary>Atomically sets the first password of an existing, unsuspended identity; never creates a new identity or replaces a nonempty hash.</summary>
    ValueTask<bool> TrySetInitialPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken);
}
