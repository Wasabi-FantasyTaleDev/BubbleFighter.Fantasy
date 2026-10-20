using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Launcher;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Keeps launcher refresh sessions in <c>launcher_sessions</c>, so sign-ins survive a server restart.</summary>
public sealed class MySqlLauncherSessions(IDbConnectionFactory connections) : ILauncherSessionStore
{
    /// <inheritdoc />
    public async ValueTask<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        return await ExpiredDelete(session.Db, now).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes expired refresh sessions while preserving all later expiries.</summary>
    internal static IDeleteable<LauncherSessionRow> ExpiredDelete(SqlSugarClient db, DateTime now) =>
        db.Deleteable<LauncherSessionRow>().Where(row => row.ExpiresAt <= now);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> CreateLimitedAsync(string sessionId, string username, string tokenHash,
        DateTime expiresAt, DateTime now, int maximumPerAccount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPerAccount);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        var owner = await db.Queryable<AccountRow>().Where(row => row.Username == username)
            .TranLock(DbLockType.Wait).Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (owner.Count != 1) throw new InvalidOperationException("Launcher session account does not exist.");
        var retained = maximumPerAccount == 1 ? [] : await RetainedQuery(db, username, now, maximumPerAccount - 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await RevokeExcessUpdate(db, username, retained, now).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await db.Insertable(new LauncherSessionRow
        {
            SessionId = sessionId, Username = username, TokenHash = tokenHash, CreatedAt = now, ExpiresAt = expiresAt,
        }).IgnoreColumns([nameof(LauncherSessionRow.RotatedAt), nameof(LauncherSessionRow.RevokedAt)])
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        retained.Add(sessionId);
        return retained;
    }

    /// <summary>Selects the newest live sign-ins retained before inserting the new one.</summary>
    internal static ISugarQueryable<string> RetainedQuery(SqlSugarClient db, string username, DateTime now, int count) =>
        db.Queryable<LauncherSessionRow>().Where(row => row.Username == username && row.RevokedAt == null && row.ExpiresAt > now)
            .OrderByDescending(row => row.CreatedAt).OrderByDescending(row => row.SessionId)
            .Take(count).Select(row => row.SessionId);

    /// <summary>Revokes this account's live sign-ins outside the retained identity set.</summary>
    internal static IUpdateable<LauncherSessionRow> RevokeExcessUpdate(SqlSugarClient db, string username, List<string> retained, DateTime now) =>
        db.Updateable<LauncherSessionRow>().SetColumns(row => row.RevokedAt == now)
            .Where(row => row.Username == username && row.RevokedAt == null && row.ExpiresAt > now && !retained.Contains(row.SessionId));

    // Ended rows are kept this long after expiry or sign-out for inspection, then removed on the next sign-in.
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async ValueTask CreateAsync(string sessionId, string username, string tokenHash, DateTime expiresAt, DateTime now, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        var cutoff = now - Retention;
        await db.Deleteable<LauncherSessionRow>()
            .Where(s => s.ExpiresAt < cutoff || s.RevokedAt < cutoff)
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        // A new session starts unrotated and unrevoked; those columns stay at their database default (NULL).
        await db.Insertable(new LauncherSessionRow
            {
                SessionId = sessionId, Username = username, TokenHash = tokenHash, CreatedAt = now, ExpiresAt = expiresAt,
            })
            .IgnoreColumns([nameof(LauncherSessionRow.RotatedAt), nameof(LauncherSessionRow.RevokedAt)])
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<LauncherRefreshSession?> FindAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var found = await session.Db.Queryable<LauncherSessionRow>()
            .Where(s => s.TokenHash == tokenHash)
            .Take(1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return null;
        var row = found[0];
        return new LauncherRefreshSession(row.SessionId, row.Username, Utc(row.ExpiresAt),
            row.RevokedAt is { } revoked ? Utc(revoked) : null);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRotateAsync(string sessionId, string currentHash, string nextHash, DateTime nextExpiresAt, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var rotated = await RotateUpdate(session.Db, sessionId, currentHash, nextHash, nextExpiresAt, now)
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        return rotated == 1;
    }

    /// <summary>
    /// The compare-and-set that rotates a live session. Its WHERE on <c>token_hash</c> is the single winner of
    /// concurrent refreshes, so the caller checks the affected-row count for exactly one.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="sessionId">The session being rotated.</param>
    /// <param name="currentHash">The token hash the caller presented.</param>
    /// <param name="nextHash">The hash of the replacement token.</param>
    /// <param name="nextExpiresAt">The replacement token's expiry.</param>
    /// <param name="now">The rotation instant; a session expired at or before it cannot rotate.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<LauncherSessionRow> RotateUpdate(SqlSugarClient db, string sessionId, string currentHash,
        string nextHash, DateTime nextExpiresAt, DateTime now) =>
        db.Updateable<LauncherSessionRow>()
            .SetColumns(s => s.TokenHash == nextHash)
            .SetColumns(s => s.ExpiresAt == nextExpiresAt)
            .SetColumns(s => s.RotatedAt == now)
            .Where(s => s.SessionId == sessionId && s.TokenHash == currentHash && s.RevokedAt == null && s.ExpiresAt > now);

    /// <inheritdoc />
    public async ValueTask RevokeAsync(string sessionId, DateTime now, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        await session.Db.Updateable<LauncherSessionRow>()
            .SetColumns(s => s.RevokedAt == now)
            .Where(s => s.SessionId == sessionId && s.RevokedAt == null)
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
