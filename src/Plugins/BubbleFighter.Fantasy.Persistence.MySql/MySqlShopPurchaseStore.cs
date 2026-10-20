using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Commits each purchase in one transaction under the per-player lock (<see cref="MySqlPlayerLock"/>),
/// so it is serialized against inventory appends and other purchases for the same player.
/// </summary>
public sealed class MySqlShopPurchaseStore(IDbConnectionFactory connections) : IShopPurchaseStore
{
    /// <inheritdoc />
    public async ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct)
    {
        command.Validate();
        var user = command.SeedWallet.UserNum;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, user, ct).ConfigureAwait(false);
        // The no-op duplicate update creates a missing wallet atomically instead of read-then-insert;
        // updated_at takes the column default on insert.
        var seed = new WalletRow { UserNum = user, Lucci = command.SeedWallet.Lucci, Cash = command.SeedWallet.Cash };
        await SugarSql.UpsertAsync(db, seed, [nameof(WalletRow.UpdatedAt)], [nameof(WalletRow.UserNum)], [], ct)
            .ConfigureAwait(false);
        var walletRows = await db.Queryable<WalletRow>()
            .Where(w => w.UserNum == user)
            .Take(1)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        if (walletRows.Count != 1)
            throw new InvalidOperationException($"player_wallets row for user_num {user} is missing after the seed insert.");
        var wallet = new PlayerWallet(walletRows[0].UserNum, walletRows[0].Lucci, walletRows[0].Cash);
        var cash = command.MoneyType == "cash";
        var balance = cash ? wallet.Cash : wallet.Lucci;
        if (balance < command.Price)
            return null;

        var creditedLucci = checked(wallet.Lucci + command.LucciReward);
        var bag = await db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == user && s.Bag == 0)
            .OrderBy(s => s.Ordinal)
            .Take(InventoryLimits.MaxInventorySlotsPerBag + 1)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        var stored = bag.Select(MySqlInventoryRepository.ToRecord).ToArray();
        command.ValidateInventorySnapshot(stored);
        var addedCount = command.Grants.Count(g => g.ExpectedSlot is null);
        if (bag.Count + addedCount > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidOperationException($"Character bag for user {user} is full.");
        // A delivered gift is marked taken under the player lock taken above; one that is no longer pending
        // refuses the whole purchase before any write.
        if (command.PresentId > 0
            && await MySqlPresentStore.AcceptUpdate(db, command.PresentId, user).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new PresentUnavailableException(command.PresentId);
        var lastKey = (await db.Queryable<PurchaseLogRow>()
            .Where(p => p.UserNum == user)
            .Select(p => SqlFunc.IsNull(SqlFunc.AggregateMax(p.LastSlotKey), 0L))
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        var highest = Math.Max(lastKey, bag.Count == 0 ? 0 : bag.Max(s => s.SlotKey));
        var lastOrdinal = bag.Count == 0 ? -1 : bag.Max(s => s.Ordinal);
        var grants = new InventorySlotRecord[command.Grants.Count];
        for (var i = 0; i < grants.Length; i++)
        {
            var planned = command.Grants[i];
            if (planned.ExpectedSlot is { } existing)
            {
                var replacement = existing with { Quantity = planned.Quantity, Lifetime = planned.Lifetime };
                if (replacement == existing)
                {
                    grants[i] = replacement;
                    continue;
                }
                var affected = await SlotUpdate(db, replacement).ExecuteCommandAsync(ct).ConfigureAwait(false);
                if (affected != 1)
                    throw new InvalidOperationException("The locked purchase inventory row could not be updated.");
                grants[i] = replacement;
                continue;
            }

            highest = Math.Max(command.SlotKeyBase, checked(highest + 1));
            lastOrdinal = checked(lastOrdinal + 1);
            var grant = new InventorySlotRecord(user, InventoryBag.CharacterBag, highest,
                planned.ItemId, planned.Quantity, 0) { Lifetime = planned.Lifetime };
            grants[i] = grant;
            // updated_at takes the column default, as it did before the ORM.
            var inserted = await db.Insertable(MySqlInventoryRepository.ToRow(grant, lastOrdinal))
                .IgnoreColumns(nameof(InventorySlotRow.UpdatedAt))
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (inserted < 1)
                throw new InvalidOperationException($"inventory_slots insert for user_num {user} wrote {inserted} row(s).");
        }
        // The balance was read under the wallet row lock above, so this absolute write cannot lose a concurrent change.
        var charged = cash ? wallet with { Cash = balance - command.Price }
            : wallet with { Lucci = creditedLucci - command.Price };
        await ChargeUpdate(db, charged).ExecuteCommandAsync(ct).ConfigureAwait(false);
        var audit = new PurchaseLogRow
        {
            UserNum = user,
            StockSetId = command.StockSetId,
            ItemId = grants.FirstOrDefault()?.ItemId ?? 0,
            MoneyType = command.MoneyType,
            Price = command.Price,
            SlotKey = grants.FirstOrDefault()?.SlotKey ?? 0,
            PurchaseKey = command.PurchaseKey,
            LastSlotKey = highest,
        };
        var logged = await db.Insertable(audit).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (logged < 1)
            throw new InvalidOperationException($"purchase_log insert for user_num {user} wrote {logged} row(s).");
        await tx.CommitAsync().ConfigureAwait(false);
        return new(charged, grants);
    }

    /// <summary>
    /// Rewrites quantity and lifetime of one existing bag row (never flags or ordinal), keyed by the slot
    /// key the purchase locked.
    /// </summary>
    /// <param name="db">The client bound to the purchase transaction.</param>
    /// <param name="slot">The replacement values for the locked row.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<InventorySlotRow> SlotUpdate(SqlSugarClient db, InventorySlotRecord slot)
    {
        var user = slot.UserNum;
        var slotKey = slot.SlotKey;
        var quantity = slot.Quantity;
        var lifetimeKind = (int)slot.Lifetime.Kind;
        var expires = slot.Lifetime.ExpiresAtUnixMilliseconds;
        return db.Updateable<InventorySlotRow>()
            .SetColumns(s => s.Quantity == quantity)
            .SetColumns(s => s.LifetimeKind == lifetimeKind)
            .SetColumns(s => s.ExpiresAtUnixMilliseconds == expires)
            .Where(s => s.UserNum == user && s.Bag == 0 && s.SlotKey == slotKey);
    }

    /// <summary>
    /// Writes the post-charge balances. The balances were read under the wallet row lock, so this absolute
    /// write cannot lose a concurrent change.
    /// </summary>
    /// <param name="db">The client bound to the purchase transaction.</param>
    /// <param name="charged">The wallet after the price is deducted.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<WalletRow> ChargeUpdate(SqlSugarClient db, PlayerWallet charged)
    {
        var user = charged.UserNum;
        var lucci = charged.Lucci;
        var cash = charged.Cash;
        return db.Updateable<WalletRow>()
            .SetColumns(w => w.Lucci == lucci)
            .SetColumns(w => w.Cash == cash)
            .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
            .Where(w => w.UserNum == user);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct)
    {
        if (userNum <= 0 || maxCount is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<PurchaseLogRow>()
            .Where(p => p.UserNum == userNum)
            .OrderBy(p => p.Id, OrderByType.Desc)
            .Take(maxCount)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new ShopPurchaseRecord(r.UserNum, r.StockSetId, r.ItemId, r.MoneyType, r.Price, r.SlotKey, r.PurchaseKey))
            .ToList();
    }
}
