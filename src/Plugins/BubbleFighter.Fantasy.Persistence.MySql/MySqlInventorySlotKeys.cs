using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Shares the account's durable ordinary inventory key watermark across purchases and mileage grants.</summary>
internal static class MySqlInventorySlotKeys
{
    /// <summary>Reads the stored watermark and the current allocation floor, including older-node purchases and both active bags.</summary>
    internal static async ValueTask<(long Stored, long Highest)> ReadAsync(SqlSugarClient db, long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await ReadQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count != 1 || rows[0] < 0)
            throw new InvalidDataException("The ordinary inventory key watermark is missing or invalid.");
        // Older nodes advance purchase history without this column; locking reads stay current under REPEATABLE READ.
        var purchases = await PurchaseKeyQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);
        var active = await ActiveKeyQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);
        return (rows[0], Math.Max(rows[0], Math.Max(purchases.FirstOrDefault(), active.FirstOrDefault())));
    }

    /// <summary>Advances the watermark alongside the inventory write, retaining it when no new key was allocated.</summary>
    internal static async ValueTask AdvanceAsync(SqlSugarClient db, long userNum, long expected, long next, CancellationToken ct)
    {
        Validate(db, userNum, expected, next);
        ct.ThrowIfCancellationRequested();
        if (next == expected) return;
        if (await AdvanceStatement(db, userNum, expected, next).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The ordinary inventory key watermark changed under its account lock.");
    }

    /// <summary>Builds the current locking read so the account watermark never comes from an earlier snapshot.</summary>
    internal static ISugarQueryable<long> ReadQuery(SqlSugarClient db, long userNum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        return db.Queryable<PlayerRow>().Where(row => row.UserNum == userNum)
            .Select(row => row.LastInventorySlotKey).Take(1).TranLock(DbLockType.Wait);
    }

    /// <summary>Reads the highest historical purchase key as current committed data after acquiring the account lock.</summary>
    internal static ISugarQueryable<long> PurchaseKeyQuery(SqlSugarClient db, long userNum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        return db.Queryable<PurchaseLogRow>().Where(row => row.UserNum == userNum)
            .OrderBy(row => row.LastSlotKey, OrderByType.Desc).Select(row => row.LastSlotKey)
            .Take(1).TranLock(DbLockType.Wait);
    }

    /// <summary>Reads the highest active key from either bag without narrowing the allocation floor to the character bag.</summary>
    internal static ISugarQueryable<long> ActiveKeyQuery(SqlSugarClient db, long userNum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        return db.Queryable<InventorySlotRow>().Where(row => row.UserNum == userNum && (row.Bag == 0 || row.Bag == 1))
            .OrderBy(row => row.SlotKey, OrderByType.Desc).Select(row => row.SlotKey)
            .Take(1).TranLock(DbLockType.Wait);
    }

    /// <summary>Builds a checked account update without changing inventory, purchase history or Article allocation.</summary>
    internal static IUpdateable<PlayerRow> AdvanceStatement(SqlSugarClient db, long userNum, long expected, long next)
    {
        Validate(db, userNum, expected, next);
        return db.Updateable<PlayerRow>().SetColumns(row => row.LastInventorySlotKey == next)
            .Where(row => row.UserNum == userNum && row.LastInventorySlotKey == expected);
    }

    private static void Validate(SqlSugarClient db, long userNum, long expected, long next)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegative(expected);
        if (next < expected) throw new ArgumentOutOfRangeException(nameof(next), "Ordinary inventory keys cannot move backwards.");
        if (db.Ado.Transaction is null)
            throw new InvalidOperationException("An ordinary inventory key update requires its account transaction.");
    }
}
