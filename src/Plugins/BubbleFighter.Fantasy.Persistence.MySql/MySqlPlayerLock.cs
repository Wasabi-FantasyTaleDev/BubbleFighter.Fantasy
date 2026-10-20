using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// The per-player lock every multi-table write for one player takes first: a row lock on the
/// player's <c>players</c> row. Taking it before any wallet or inventory row keeps the lock order
/// the same everywhere (players, then player_wallets, then inventory_slots), so writers queue
/// instead of deadlocking.
///
/// </summary>
internal static class MySqlPlayerLock
{
    /// <summary>
    /// Locks the player's row for the rest of the transaction (<c>SELECT... FOR UPDATE</c> through
    /// <c>TranLock</c>, which itself refuses to run outside a transaction). </summary> <param name="db">The
    /// client whose transaction the lock belongs to.</param> <param name="userNum">The player.</param> <param
    /// name="ct">Cancellation token.</param> <exception cref="InvalidOperationException">Thrown when the player
    /// row does not exist.</exception>
    public static async Task AcquireAsync(SqlSugarClient db, long userNum, CancellationToken ct)
    {
        var locked = await LockQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);
        if (locked.Count == 0)
            throw new InvalidOperationException($"Player {userNum} does not exist; refusing to write inventory or money for it.");
    }

    /// <summary>The locking read itself, exposed so tests can render it without a server.</summary>
    /// <param name="db">The client whose transaction the lock belongs to.</param>
    /// <param name="userNum">The player.</param>
    /// <returns>A query that selects the player's key and holds its row lock.</returns>
    internal static ISugarQueryable<long> LockQuery(SqlSugarClient db, long userNum) =>
        db.Queryable<PlayerRow>()
            .Where(p => p.UserNum == userNum)
            .Select(p => p.UserNum)
            .Take(1)
            .TranLock(DbLockType.Wait);
}
