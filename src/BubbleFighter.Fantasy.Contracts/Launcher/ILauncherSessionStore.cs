namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>One durable launcher sign-in, renewed through its refresh credential until it expires or is revoked.</summary>
/// <param name="SessionId">Random, stable identifier of the sign-in; the same across every rotation.</param>
/// <param name="Username">Normalized account login name the sign-in belongs to.</param>
/// <param name="ExpiresAt">UTC instant after which the current refresh credential is refused.</param>
/// <param name="RevokedAt">UTC instant of sign-out, or null while the session is live.</param>
public sealed record LauncherRefreshSession(string SessionId, string Username, DateTime ExpiresAt, DateTime? RevokedAt);

/// <summary>
/// Persists launcher refresh sessions so a sign-in survives launcher and server restarts. Only a hash of the
/// current refresh credential is stored; the raw credential never reaches the store.
/// </summary>
public interface ILauncherSessionStore
{
    /// <summary>Deletes refresh sessions expired at or before the UTC cutoff and returns the deleted row count.</summary>
    /// <param name="now">UTC cutoff; unexpired sign-ins are preserved.</param>
    /// <param name="cancellationToken">Cancellation before the delete commits.</param>
    /// <returns>The number of expired rows removed, or zero when none qualified.</returns>
    ValueTask<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a sign-in atomically with a per-account live-session limit. Revokes the oldest sign-ins by creation
    /// time, then session id, and returns the retained live session ids (including the new one).
    /// </summary>
    /// <param name="sessionId">Unique random identifier for the new sign-in.</param>
    /// <param name="username">Normalized account owning the sign-in.</param>
    /// <param name="tokenHash">Hash of the new refresh credential; never the credential itself.</param>
    /// <param name="expiresAt">UTC expiry of the new refresh credential.</param>
    /// <param name="now">UTC creation instant and cutoff for expired sign-ins.</param>
    /// <param name="maximumPerAccount">Positive maximum live sign-ins retained for this account.</param>
    /// <param name="cancellationToken">Cancellation before the operation commits.</param>
    /// <returns>The account's retained live session ids after the transaction.</returns>
    ValueTask<IReadOnlyList<string>> CreateLimitedAsync(string sessionId, string username, string tokenHash,
        DateTime expiresAt, DateTime now, int maximumPerAccount, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new live session whose current refresh credential has <paramref name="tokenHash"/>, created at
    /// <paramref name="now"/>; sessions that ended before then may be dropped to keep the store bounded.
    /// </summary>
    ValueTask CreateAsync(string sessionId, string username, string tokenHash, DateTime expiresAt, DateTime now,
        CancellationToken cancellationToken);

    /// <summary>Finds the session whose current refresh credential has <paramref name="tokenHash"/>, live or not.</summary>
    ValueTask<LauncherRefreshSession?> FindAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the current credential hash only while it is still <paramref name="currentHash"/>, the session is not
    /// revoked and it has not expired at <paramref name="now"/>; exactly one of several concurrent callers wins.
    /// </summary>
    /// <returns>Whether this call performed the rotation.</returns>
    ValueTask<bool> TryRotateAsync(string sessionId, string currentHash, string nextHash, DateTime nextExpiresAt, DateTime now,
        CancellationToken cancellationToken);

    /// <summary>Marks one session revoked; revoking an already revoked or unknown session does nothing.</summary>
    ValueTask RevokeAsync(string sessionId, DateTime now, CancellationToken cancellationToken);
}
