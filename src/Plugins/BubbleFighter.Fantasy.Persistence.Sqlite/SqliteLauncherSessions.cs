using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Durable launcher refresh sessions in the save's <c>launcher_sessions</c> table.</summary>
internal sealed class SqliteLauncherSessions(SqliteDatabase database) : ILauncherSessionStore
{
    public ValueTask<IReadOnlyList<string>> CreateLimitedAsync(string sessionId, string username, string tokenHash,
        DateTime expiresAt, DateTime now, int maximumPerAccount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPerAccount);
        var nowMs = Ms(now);
        return new(database.Write<IReadOnlyList<string>>((db, _) =>
        {
            db.Orm.Deleteable<LauncherSessionRow>().Where(row => row.ExpiresAt <= nowMs || row.RevokedAt != null).ExecuteCommand();
            var retained = maximumPerAccount == 1 ? [] : db.Orm.Queryable<LauncherSessionRow>()
                .Where(row => row.Username == username && row.ExpiresAt > nowMs)
                .OrderByDescending(row => row.CreatedAt).OrderByDescending(row => row.SessionId)
                .Take(maximumPerAccount - 1).Select(row => row.SessionId).ToList();
            db.Orm.Updateable<LauncherSessionRow>().SetColumns(row => new LauncherSessionRow { RevokedAt = nowMs })
                .Where(row => row.Username == username && row.RevokedAt == null && row.ExpiresAt > nowMs && !retained.Contains(row.SessionId))
                .ExecuteCommand();
            // Revoked rows no longer consume the provider's live-session capacity.
            db.Orm.Deleteable<LauncherSessionRow>().Where(row => row.RevokedAt != null).ExecuteCommand();
            db.RequireCapacity("launcher_sessions", db.Options.MaxAccounts);
            db.Orm.Insertable(new LauncherSessionRow
            {
                SessionId = sessionId, Username = username, TokenHash = tokenHash, CreatedAt = nowMs, ExpiresAt = Ms(expiresAt),
            }).ExecuteCommand();
            retained.Add(sessionId);
            return retained;
        }, ct));
    }

    public ValueTask<int> PruneExpiredAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = Ms(now);
        return new(database.Write((db, _) => db.Orm.Deleteable<LauncherSessionRow>()
            .Where(row => row.ExpiresAt <= cutoff).ExecuteCommand(), ct));
    }

    public ValueTask CreateAsync(string sessionId, string username, string tokenHash, DateTime expiresAt, DateTime now, CancellationToken ct)
    {
        var nowMs = Ms(now);
        var expiresMs = Ms(expiresAt);
        database.Write((db, _) =>
        {
            // Ended sign-ins are removed before the bound is checked, so only live ones count against it.
            db.Orm.Deleteable<LauncherSessionRow>().Where(row => row.ExpiresAt <= nowMs || row.RevokedAt != null).ExecuteCommand();
            db.RequireCapacity("launcher_sessions", db.Options.MaxAccounts);
            db.Orm.Insertable(new LauncherSessionRow
            {
                SessionId = sessionId, Username = username, TokenHash = tokenHash, CreatedAt = nowMs, ExpiresAt = expiresMs,
            }).ExecuteCommand();
            return 0;
        }, ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask<LauncherRefreshSession?> FindAsync(string tokenHash, CancellationToken ct) => new(database.Read(db =>
    {
        var session = db.Orm.Queryable<LauncherSessionRow>().Where(row => row.TokenHash == tokenHash).ToList().FirstOrDefault();
        return session is null ? null : new LauncherRefreshSession(session.SessionId, session.Username, Utc(session.ExpiresAt),
            session.RevokedAt is { } revoked ? Utc(revoked) : null);
    }, ct));

    public ValueTask<bool> TryRotateAsync(string sessionId, string currentHash, string nextHash, DateTime nextExpiresAt, DateTime now,
        CancellationToken ct)
    {
        var nowMs = Ms(now);
        var nextExpiresMs = Ms(nextExpiresAt);
        return new(database.Write((db, _) => db.Orm.Updateable<LauncherSessionRow>()
            .SetColumns(row => new LauncherSessionRow { TokenHash = nextHash, ExpiresAt = nextExpiresMs, RotatedAt = nowMs })
            .Where(row => row.SessionId == sessionId && row.TokenHash == currentHash && row.RevokedAt == null && row.ExpiresAt > nowMs)
            .ExecuteCommand() == 1, ct));
    }

    public ValueTask RevokeAsync(string sessionId, DateTime now, CancellationToken ct)
    {
        var nowMs = Ms(now);
        database.Write((db, _) => db.Orm.Updateable<LauncherSessionRow>()
            .SetColumns(row => new LauncherSessionRow { RevokedAt = nowMs })
            .Where(row => row.SessionId == sessionId && row.RevokedAt == null).ExecuteCommand(), ct);
        return ValueTask.CompletedTask;
    }

    private static long Ms(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
