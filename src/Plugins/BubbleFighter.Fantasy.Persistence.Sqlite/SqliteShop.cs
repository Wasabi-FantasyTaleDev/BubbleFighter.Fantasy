using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's shop fragment: purchases, shop sessions and clan membership, all as whole-snapshot
/// mutations under one write transaction, so a purchase's wallet debit, inventory grants and history
/// append cannot partially apply.
/// </summary>
internal sealed partial class SqliteRepositories : IShopPurchaseStore, IShopSessionStore, IClanStore
{
    public ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct)
    {
        command.Validate();
        return new(Mutate<ShopPurchaseResult?>(command.SeedWallet.UserNum, (state, _) =>
        {
            var wallet = state.Wallet ?? command.SeedWallet;
            var cash = command.MoneyType == "cash";
            var balance = cash ? wallet.Cash : wallet.Lucci;
            if (balance < command.Price) return null;
            var bag = state.Slots.Where(slot => slot.Bag == InventoryBag.CharacterBag).ToList();
            command.ValidateInventorySnapshot(bag);
            if (bag.Count + command.Grants.Count(grant => grant.ExpectedSlot is null) > InventoryLimits.MaxInventorySlotsPerBag)
                throw new InvalidOperationException("Character bag is full.");
            var highest = Math.Max(state.LastPurchaseSlotKey, bag.Count == 0 ? 0 : bag.Max(slot => slot.SlotKey));
            var grants = new InventorySlotRecord[command.Grants.Count];
            for (var i = 0; i < grants.Length; i++)
            {
                var grant = command.Grants[i];
                if (grant.ExpectedSlot is { } existing)
                {
                    grants[i] = existing with { Quantity = grant.Quantity, Lifetime = grant.Lifetime };
                    bag[bag.FindIndex(slot => slot == existing)] = grants[i];
                }
                else
                {
                    highest = Math.Max(command.SlotKeyBase, checked(highest + 1));
                    grants[i] = new(command.SeedWallet.UserNum, InventoryBag.CharacterBag, highest, grant.ItemId, grant.Quantity, 0)
                        { Lifetime = grant.Lifetime };
                    bag.Add(grants[i]);
                }
            }
            // Last check before the first write: a gift already taken or removed refuses the whole purchase.
            if (command.PresentId > 0)
                AcceptPresent(database, command.PresentId, command.SeedWallet.UserNum);
            var credited = checked(wallet.Lucci + command.LucciReward);
            state.Wallet = cash ? wallet with { Cash = balance - command.Price, Lucci = credited }
                : wallet with { Lucci = credited - command.Price };
            state.Progress ??= new(wallet.UserNum, state.Level, 0);
            var audit = new ShopPurchaseRecord(wallet.UserNum, command.StockSetId, grants.FirstOrDefault()?.ItemId ?? 0,
                command.MoneyType, command.Price, grants.FirstOrDefault()?.SlotKey ?? 0, command.PurchaseKey);
            state.Purchases = state.Purchases.Prepend(audit).Take(database.Options.MaxPurchaseHistoryPerPlayer).ToList();
            state.Slots.RemoveAll(slot => slot.Bag == InventoryBag.CharacterBag);
            state.Slots.AddRange(bag);
            state.LastPurchaseSlotKey = highest;
            return new(state.Wallet, grants);
        }, ct));
    }

    public ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct)
    {
        RequireBound(maxCount, 1024);
        return new(database.Read<IReadOnlyList<ShopPurchaseRecord>>(db => db.State(userNum)?.Purchases.Take(maxCount).ToArray() ?? [], ct));
    }

    public ValueTask RememberOpenAsync(ShopOpenRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind)) throw new ArgumentOutOfRangeException(nameof(record));
        ct.ThrowIfCancellationRequested();
        if (record.UserNum != 0) Mutate(record.UserNum, state => state.ShopOpen = record, ct);
        return ValueTask.CompletedTask;
    }

    ValueTask<ShopOpenRecord?> IShopSessionStore.FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        ct.ThrowIfCancellationRequested();
        return userNum == 0 ? new((ShopOpenRecord?)null) : new(database.Read(db => db.State(userNum)?.ShopOpen, ct));
    }

    public ValueTask RememberOpenAsync(ClanOpenRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind)) throw new ArgumentOutOfRangeException(nameof(record));
        ct.ThrowIfCancellationRequested();
        if (record.UserNum != 0) Mutate(record.UserNum, state => state.ClanOpen = record, ct);
        return ValueTask.CompletedTask;
    }

    ValueTask<ClanOpenRecord?> IClanStore.FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        ct.ThrowIfCancellationRequested();
        return userNum == 0 ? new((ClanOpenRecord?)null) : new(database.Read(db => db.State(userNum)?.ClanOpen, ct));
    }

    public ValueTask<ClanMembership> GetMembershipAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        ct.ThrowIfCancellationRequested();
        return new(new ClanMembership(userNum, 0));
    }

    public ValueTask<IReadOnlyList<long>> ListClanIdsAsync(int maxCount, CancellationToken ct)
    {
        RequireBound(maxCount, int.MaxValue - 1);
        ct.ThrowIfCancellationRequested();
        return new(Array.Empty<long>());
    }

    public ValueTask<IReadOnlyList<long>> ListMemberUserNumsAsync(long clanId, int maxCount, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clanId);
        return ListClanIdsAsync(maxCount, ct);
    }
}
