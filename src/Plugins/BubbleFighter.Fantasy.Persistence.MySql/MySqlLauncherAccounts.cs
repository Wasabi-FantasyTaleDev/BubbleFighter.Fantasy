using System.Data;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Launcher;
using MySqlConnector;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Stores launcher credentials in accounts and reads game identity by its unique username.</summary>
public sealed class MySqlLauncherAccounts(IDbConnectionFactory connections) : ILauncherAccountRepository
{
    // Raw on purpose: the 1062 duplicate-key code must reach the catch that reports a lost race, and SqlSugar wraps driver errors.
    private const string InsertAccountSql = "INSERT INTO accounts (username, password_hash) VALUES (@name, @hash)";

    /// <inheritdoc />
    public async ValueTask<LauncherAccount?> FindAsync(string username, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var found = await AccountLookup(session.Db, username).Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return null;
        var row = found[0];
        return new LauncherAccount(row.Username, row.PasswordHash, row.UserNum, row.Nickname,
            row.BannedUntil is { } banned ? DateTime.SpecifyKind(banned, DateTimeKind.Utc) : null);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RegisterAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        var db = session.Db;
        var sameName = await db.Queryable<PlayerIdentityRow>()
            .Where(p => p.Username == username)
            .Select(p => p.Username)
            .Take(1)
            .TranLock(DbLockType.Wait)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (sameName.Count != 0) return false;
        try
        {
            await SugarSql.ExecuteOnConnectionAsync(db, InsertAccountSql,
                [("@name", username), ("@hash", passwordHash)], cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException e) when (e.Number == 1062) { return false; }
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySetInitialPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        var db = session.Db;
        // Registration and enrollment lock the player before credentials to serialize competing claims.
        var hasPlayer = (await db.Queryable<PlayerIdentityRow>()
            .Where(p => p.Username == username)
            .Select(p => p.Username)
            .Take(1)
            .TranLock(DbLockType.Wait)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Count != 0;
        var credentials = await db.Queryable<AccountRow>()
            .Where(a => a.Username == username)
            .Select(a => new { a.PasswordHash, a.BannedUntil })
            .TranLock(DbLockType.Wait)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasAccount = credentials.Count != 0;
        if (hasAccount && (credentials[0].PasswordHash.Length != 0
            || credentials[0].BannedUntil is { } until && DateTime.SpecifyKind(until, DateTimeKind.Utc) > DateTime.UtcNow))
            return false;
        if (!hasAccount && !hasPlayer) return false;
        try
        {
            if (hasAccount)
            {
                await InitialPasswordUpdate(db, username, passwordHash).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SugarSql.ExecuteOnConnectionAsync(db, InsertAccountSql,
                    [("@name", username), ("@hash", passwordHash)], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (MySqlException e) when (e.Number == 1062) { return false; }
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The launcher account lookup: an account joined to its player, followed by a player with no account yet
    /// (which reports an empty hash and no ban). Both branches project to <see cref="LauncherAccountSourceRow"/>.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="username">The login name to find.</param>
    /// <returns>The union, not yet executed; the caller takes the first row.</returns>
    internal static ISugarQueryable<LauncherAccountSourceRow> AccountLookup(SqlSugarClient db, string username)
    {
        var withAccount = db.Queryable<AccountRow>()
            .LeftJoin<PlayerIdentityRow>((a, p) => a.Username == p.Username)
            .Where((a, p) => a.Username == username)
            .Select((a, p) => new LauncherAccountSourceRow
            {
                Username = a.Username, PasswordHash = a.PasswordHash, UserNum = p.UserNum, Nickname = p.Nickname, BannedUntil = a.BannedUntil,
            });
        var playerOnly = db.Queryable<PlayerIdentityRow>()
            .Where(p => p.Username == username && SqlFunc.Subqueryable<AccountRow>().Where(a => a.Username == p.Username).Any() == false)
            .Select(p => new LauncherAccountSourceRow
            {
                Username = p.Username, PasswordHash = "", UserNum = p.UserNum, Nickname = p.Nickname, BannedUntil = null,
            });
        return db.UnionAll(withAccount, playerOnly);
    }

    /// <summary>
    /// Sets the password of an account whose hash is still empty (an enrollment of a player that predates credentials).
    /// The OCTET_LENGTH condition is what makes the write conditional; the caller holds the row lock.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="username">The account to enroll.</param>
    /// <param name="passwordHash">The new password hash.</param>
    /// <returns>The conditional update, not yet executed.</returns>
    internal static IUpdateable<AccountRow> InitialPasswordUpdate(SqlSugarClient db, string username, string passwordHash) =>
        db.Updateable<AccountRow>()
            .SetColumns(a => a.PasswordHash == passwordHash)
            .Where(a => a.Username == username && SqlFunc.MappingColumn(default(int), "OCTET_LENGTH(`password_hash`)") == 0);
}
