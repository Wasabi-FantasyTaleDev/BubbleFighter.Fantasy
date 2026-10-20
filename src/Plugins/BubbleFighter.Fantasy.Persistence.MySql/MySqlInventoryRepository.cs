using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using System.Linq.Expressions;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>MySQL store for <c>inventory_slots</c> (migrations 0005, 0016, 0021, 0022, 0027).</summary>
public sealed class MySqlInventoryRepository(IDbConnectionFactory connections) : IInventoryRepository
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId,
        DateTimeOffset now, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var owned = await db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == userNum && row.ItemId == itemId && (row.Bag == 0 || row.Bag == 1))
            .OrderBy(row => row.Bag).OrderBy(row => row.Ordinal)
            .Take(2 * InventoryLimits.MaxInventorySlotsPerBag + 1)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var removed = InventoryItemRemoval.SelectExpired(userNum, itemId, owned.Select(ToRecord).ToArray(), now);
        if (removed is null) return null;
        if (removed.Count > 0)
        {
            var count = await RemoveExpiredItemStatement(db, userNum, itemId, now.ToUnixTimeMilliseconds())
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (count != removed.Count)
                throw new InvalidOperationException($"Expired-item removal for user_num {userNum} deleted {count} rows; expected {removed.Count}.");
        }
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync().ConfigureAwait(false);
        return removed;
    }

    // Share the expiry predicate with the sweep, and bind the requested identity independently.
    internal static IDeleteable<InventorySlotRow> RemoveExpiredItemStatement(SqlSugarClient db,
        long userNum, int itemId, long nowMs) => db.Deleteable<InventorySlotRow>()
        .Where(EndedRows(userNum, nowMs)).Where(row => row.ItemId == itemId);

    /// <inheritdoc />
    public async ValueTask<int?> ConsumeItemAsync(long userNum, int itemId, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var owned = await db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == userNum && row.Bag == 0 && row.ItemId == itemId)
            .OrderBy(row => row.Ordinal, OrderByType.Desc).Take(1)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var item = owned.SingleOrDefault();
        if (item is null || item.Quantity <= 0 || item.LifetimeKind == (int)InventoryLifetimeKind.Life
            || item.ExpiresAtUnixMilliseconds is { } end && end <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return null;
        var remaining = item.Quantity - 1;
        if (remaining == 0)
            await db.Deleteable<InventorySlotRow>()
                .Where(row => row.UserNum == userNum && row.ItemId == itemId && (row.Bag == 0 || row.Bag == 1))
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
        else
            await db.Updateable<InventorySlotRow>()
                .SetColumns(row => row.Quantity == remaining)
                .SetColumns(row => row.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
                .Where(row => row.UserNum == userNum && row.ItemId == itemId && (row.Bag == 0 || row.Bag == 1))
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return remaining;
    }
    /// <inheritdoc />
    /// <remarks>
    /// Takes the per-player lock first, then reads the stored last-login day and lowers the life of this
    /// player's life rows in both bags in one statement (<see cref="LifeUpdate"/>) before storing today.
    /// </remarks>
    public async ValueTask<int> AdvanceLifeAsync(long userNum, DateOnly today, CancellationToken ct)
    {
        RequireUserNum(userNum);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var stored = await db.Queryable<PlayerRow>().Where(p => p.UserNum == userNum)
            .Select(p => p.LastLoginDay).Take(1).ToListAsync(ct).ConfigureAwait(false);
        DateOnly? last = stored.Count == 1 && stored[0] is { } day ? DateOnly.FromDateTime(day) : null;
        var days = InventoryLife.DaysMissed(last, today);
        if (days > 0)
            await LifeUpdate(db, userNum, days).ExecuteCommandAsync(ct).ConfigureAwait(false);
        var next = InventoryLife.NextLoginDay(last, today).ToDateTime(TimeOnly.MinValue);
        var written = await db.Updateable<PlayerRow>().SetColumns(p => p.LastLoginDay == next)
            .Where(p => p.UserNum == userNum).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (written != 1)
            throw new InvalidOperationException($"players has no row for user_num {userNum}; the last-login day cannot be stored.");
        await tx.CommitAsync().ConfigureAwait(false);
        return days;
    }

    /// <summary>
    /// The statement that lowers the life of one player's life rows in both bags by <paramref name="days"/>,
    /// never below zero. The day count is bound as a parameter; the conditional equals GREATEST(quantity - days, 0).
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="userNum">The player.</param>
    /// <param name="days">The whole days to charge; positive.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<InventorySlotRow> LifeUpdate(SqlSugarClient db, long userNum, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        var lifeKind = (int)InventoryLifetimeKind.Life;
        return db.Updateable<InventorySlotRow>()
            .SetColumns(s => s.Quantity == SqlFunc.IIF(s.Quantity > days, s.Quantity - days, 0))
            .SetColumns(s => s.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
            .Where(s => s.UserNum == userNum && (s.Bag == 0 || s.Bag == 1) && s.LifetimeKind == lifeKind);
    }

    /// <summary>
    /// The rows that have ended for one player in both bags: a deadline at or before <paramref name="nowMs"/>
    /// (Unix milliseconds), or a life row whose quantity is zero or less (<see cref="InventoryExpiry"/>).
    /// </summary>
    /// <param name="userNum">The player.</param>
    /// <param name="nowMs">The comparison instant in Unix milliseconds.</param>
    /// <returns>The row predicate shared by the read and the delete, so they always agree.</returns>
    internal static Expression<Func<InventorySlotRow, bool>> EndedRows(long userNum, long nowMs)
    {
        var expiringKind = (int)InventoryLifetimeKind.ExpiresAt;
        var lifeKind = (int)InventoryLifetimeKind.Life;
        return row => row.UserNum == userNum && (row.Bag == 0 || row.Bag == 1)
            && (row.LifetimeKind == expiringKind && row.ExpiresAtUnixMilliseconds <= nowMs
                || row.LifetimeKind == lifeKind && row.Quantity <= 0);
    }

    /// <inheritdoc />
    /// <remarks>Takes the per-player lock first, like <see cref="ConsumeItemAsync"/>, then deletes only this player's ended rows.</remarks>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long userNum, DateTimeOffset now, CancellationToken ct)
    {
        RequireUserNum(userNum);
        var ended = EndedRows(userNum, now.ToUnixTimeMilliseconds());
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var expired = await db.Queryable<InventorySlotRow>()
            .Where(ended)
            .OrderBy(row => row.Bag).OrderBy(row => row.Ordinal)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            var removed = await db.Deleteable<InventorySlotRow>()
                .Where(ended)
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (removed != expired.Count)
                throw new InvalidOperationException($"Expiry removal for user_num {userNum} deleted {removed} rows; expected {expired.Count}.");
        }
        await tx.CommitAsync().ConfigureAwait(false);
        return expired.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var stored = await db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && (s.Bag == 0 || s.Bag == 1))
            .OrderBy(s => s.Bag)
            .OrderBy(s => s.UpdatedAt)
            .OrderBy(s => s.Ordinal)
            .Take(2 * InventoryLimits.MaxInventorySlotsPerBag + 1)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        var rows = stored.Select(ToRecord).ToArray();
        var changed = InventoryWearSelection.Apply(rows, groups, itemId, worn);
        if (changed is null) return null;
        var updates = Enumerable.Range(0, rows.Length).Where(i => rows[i].Flags != changed[i].Flags).ToArray();
        if (updates.Length > 0)
        {
            // One bounded statement keeps remote-database latency independent of the number of changed slots.
            // The player and inventory locks remain held until every changed row has been checked and committed.
            var (sql, parameters) = BuildWornUpdate(userNum,
                updates.Select(i => (stored[i].Bag, stored[i].Ordinal, changed[i].Flags)).ToArray());
            var written = await db.Ado.ExecuteCommandAsync(sql, parameters, ct).ConfigureAwait(false);
            if (written != updates.Length)
                throw new InvalidOperationException($"An equipment update affected {written} stored rows; expected {updates.Length}.");
        }
        await tx.CommitAsync().ConfigureAwait(false);
        return changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray();
    }

    /// <summary>
    /// Builds the single statement that rewrites the flags of every changed slot. SqlSugar cannot express a
    /// per-row CASE over the composite key, so this stays a parameterized fragment; values are bound, never
    /// concatenated.
    /// </summary>
    /// <param name="userNum">The player whose rows change.</param>
    /// <param name="changes">Bag, ordinal and new flags of each changed slot.</param>
    /// <returns>The SQL text and its parameters.</returns>
    internal static (string Sql, List<SugarParameter> Parameters) BuildWornUpdate(
        long userNum, IReadOnlyList<(int Bag, int Ordinal, byte Flags)> changes)
    {
        var parameters = new List<SugarParameter> { new("@userNum", userNum) };
        var cases = new List<string>(changes.Count);
        var keys = new List<string>(changes.Count);
        for (var n = 0; n < changes.Count; n++)
        {
            parameters.Add(new($"@bag{n}", changes[n].Bag));
            parameters.Add(new($"@ordinal{n}", changes[n].Ordinal));
            parameters.Add(new($"@flags{n}", (int)changes[n].Flags));
            cases.Add($"WHEN bag = @bag{n} AND ordinal = @ordinal{n} THEN @flags{n}");
            keys.Add($"(@bag{n}, @ordinal{n})");
        }
        var sql = $"UPDATE inventory_slots SET flags = CASE {string.Join(' ', cases)} ELSE flags END, "
            + $"updated_at = UTC_TIMESTAMP() WHERE user_num = @userNum AND (bag, ordinal) IN ({string.Join(", ", keys)})";
        return (sql, parameters);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
        long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag, nameof(maxCount));

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var bagId = (int)bag;
        var rows = await session.Db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && s.Bag == bagId)
            .OrderBy(s => s.Ordinal)
            .Take(maxCount)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// One transaction that first locks the player's <c>players</c> row, the same per-player lock
    /// <see cref="MySqlShopPurchaseStore"/> takes first, so an append and a purchase for one player
    /// never interleave.
    /// </remarks>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        ArgumentNullException.ThrowIfNull(candidates);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var bagId = (int)bag;
        var rows = await db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && s.Bag == bagId)
            .OrderBy(s => s.Ordinal)
            .Take(InventoryLimits.MaxInventorySlotsPerBag)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        var current = rows.Select(ToRecord).ToList();
        var merged = InventoryAppend.Merge(userNum, bag, current, candidates);
        var nextOrdinal = rows.Count == 0 ? 0 : rows.Max(r => r.Ordinal) + 1;
        var added = new List<InventorySlotRow>(merged.Count - current.Count);
        for (var i = current.Count; i < merged.Count; i++)
            added.Add(ToRow(merged[i], nextOrdinal++));
        if (added.Count > 0)
        {
            var written = await db.Insertable(added).ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (written != added.Count)
                throw new InvalidOperationException($"inventory_slots insert for user_num {userNum} wrote {written} row(s); expected {added.Count}.");
        }
        await tx.CommitAsync().ConfigureAwait(false);
        return merged;
    }

    /// <inheritdoc />
    public async ValueTask ReplaceSlotsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(slots), slots.Count, $"inventory slot count must be 0..{InventoryLimits.MaxInventorySlotsPerBag}.");

        foreach (var slot in slots)
        {
            if (slot.UserNum != userNum)
                throw new ArgumentException($"inventory slot user_num {slot.UserNum} does not match replace target {userNum}.", nameof(slots));
            if (slot.Bag != bag)
                throw new ArgumentException($"inventory slot bag {slot.Bag} does not match replace target {bag}.", nameof(slots));
        }

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        var bagId = (int)bag;
        await db.Deleteable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && s.Bag == bagId)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (slots.Count > 0)
        {
            var inserts = slots.Select((slot, i) => ToRow(slot, i)).ToList();
            var written = await db.Insertable(inserts).ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (written != inserts.Count)
                throw new InvalidOperationException($"inventory_slots insert for user_num {userNum} wrote {written} row(s); expected {inserts.Count}.");
        }
        await tx.CommitAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<InventorySlotRecord?> SetSlotWornAsync(
        long userNum, int itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (itemId <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemId), itemId, "item id must be a positive item id.");

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        var bagId = (int)InventoryBag.CharacterBag;
        // Existence is decided by the read, never by the UPDATE row count: a
        // repeated wear writes the same flags byte, which MySQL reports as zero
        // affected rows even though the row is there.
        var found = await db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && s.Bag == bagId && s.ItemId == itemId)
            .OrderBy(s => s.Ordinal, OrderByType.Desc)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        if (found.Count == 0)
            return null;
        var row = found[0];
        var record = ToRecord(row);

        await WornUpdate(db, userNum, itemId, worn).ExecuteCommandAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
        var flags = worn
            ? (byte)(row.Flags | InventorySlotRecord.WornFlagBit)
            : (byte)(row.Flags & ~InventorySlotRecord.WornFlagBit);
        return record with { Flags = flags };
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }

    private static void RequireBag(InventoryBag bag)
    {
        if (bag is not InventoryBag.CharacterBag and not InventoryBag.EquippedBag)
            throw new ArgumentOutOfRangeException(nameof(bag), bag, "bag must be CharacterBag (802) or EquippedBag (803 container 1).");
    }

    private static void RequireBound(int maxCount, int cap, string name)
    {
        if (maxCount < 1 || maxCount > cap)
            throw new ArgumentOutOfRangeException(name, maxCount, $"maxCount must be 1..{cap}.");
    }

    /// <summary>
    /// The wear update for one item id in the character bag. The bit flip stays server-side so other flag
    /// bits are preserved per row even where duplicates of the same item id disagree. The clear mask and the
    /// set value are bound parameters, so the expression carries no text from the caller.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="userNum">The player.</param>
    /// <param name="itemId">The item whose rows change.</param>
    /// <param name="worn">Whether the worn bit is set or cleared.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<InventorySlotRow> WornUpdate(SqlSugarClient db, long userNum, int itemId, bool worn)
    {
        var bagId = (int)InventoryBag.CharacterBag;
        var clearMask = ~(int)InventorySlotRecord.WornFlagBit;
        var setValue = worn ? (int)InventorySlotRecord.WornFlagBit : 0;
        return db.Updateable<InventorySlotRow>()
            .SetColumns(s => s.Flags == ((s.Flags & clearMask) | setValue))
            .SetColumns(s => s.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
            .Where(s => s.UserNum == userNum && s.Bag == bagId && s.ItemId == itemId);
    }

    /// <summary>Maps a stored row to the contract record; the lifetime constructor rejects corrupt rows.</summary>
    internal static InventorySlotRecord ToRecord(InventorySlotRow row) =>
        new(row.UserNum, (InventoryBag)row.Bag, row.SlotKey, row.ItemId, row.Quantity, row.Flags)
        {
            Lifetime = new((InventoryLifetimeKind)row.LifetimeKind, row.ExpiresAtUnixMilliseconds),
        };

    /// <summary>Maps a contract record to the row written at <paramref name="ordinal"/>.</summary>
    internal static InventorySlotRow ToRow(InventorySlotRecord slot, int ordinal) => new()
    {
        UserNum = slot.UserNum,
        Bag = (int)slot.Bag,
        Ordinal = ordinal,
        SlotKey = slot.SlotKey,
        ItemId = slot.ItemId,
        Quantity = slot.Quantity,
        Flags = slot.Flags,
        UpdatedAt = DateTime.UtcNow,
        LifetimeKind = (int)slot.Lifetime.Kind,
        ExpiresAtUnixMilliseconds = slot.Lifetime.ExpiresAtUnixMilliseconds,
    };
}
