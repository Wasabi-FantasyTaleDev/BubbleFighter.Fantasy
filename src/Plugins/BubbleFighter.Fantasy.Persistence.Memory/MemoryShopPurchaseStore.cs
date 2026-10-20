using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Atomic purchase commits under the same per-player gate as money and inventory repositories.</summary>
/// <param name="table">The player table whose gates serialize each player's purchases.</param>
/// <param name="presents">
/// The gift store a purchase delivering a gift marks the gift taken in; null for a row that keeps no gifts, which then
/// refuses such a purchase.
/// </param>
public sealed class MemoryShopPurchaseStore(MemoryPlayerTable table, MemoryPresentStore? presents = null) : IShopPurchaseStore
{
    /// <summary>Maximum audit entries retained per player in this process.</summary>
    public const int MaxRowsPerPlayer = 256;

    /// <inheritdoc />
    public ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct)
    {
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var user = command.SeedWallet.UserNum;
        var entry = table.GetOrAdd(user);
        lock (entry.Gate)
        {
            var wallet = entry.Money ?? command.SeedWallet;
            var cash = command.MoneyType == "cash";
            var balance = cash ? wallet.Cash : wallet.Lucci;
            if (balance < command.Price)
                return ValueTask.FromResult<ShopPurchaseResult?>(null);
            var creditedLucci = checked(wallet.Lucci + command.LucciReward);
            var bag = entry.Bags.GetValueOrDefault((int)InventoryBag.CharacterBag) ?? [];
            command.ValidateInventorySnapshot(bag);
            var addedCount = command.Grants.Count(g => g.ExpectedSlot is null);
            if (bag.Count + addedCount > InventoryLimits.MaxInventorySlotsPerBag)
                throw new InvalidOperationException($"Character bag for user {user} is full.");

            var highest = Math.Max(entry.LastPurchaseSlotKey, bag.Count == 0 ? 0 : bag.Max(s => s.SlotKey));
            var grants = new InventorySlotRecord[command.Grants.Count];
            var updated = bag.ToList();
            for (var i = 0; i < grants.Length; i++)
            {
                var grant = command.Grants[i];
                if (grant.ExpectedSlot is { } existing)
                {
                    grants[i] = existing with { Quantity = grant.Quantity, Lifetime = grant.Lifetime };
                    updated[updated.FindIndex(s => s.SlotKey == existing.SlotKey)] = grants[i];
                }
                else
                {
                    highest = Math.Max(command.SlotKeyBase, checked(highest + 1));
                    grants[i] = new(user, InventoryBag.CharacterBag, highest, grant.ItemId, grant.Quantity, 0)
                        { Lifetime = grant.Lifetime };
                    updated.Add(grants[i]);
                }
            }
            var charged = cash ? wallet with { Cash = balance - command.Price }
                : wallet with { Lucci = creditedLucci - command.Price };
            var audit = new ShopPurchaseRecord(user, command.StockSetId, grants.FirstOrDefault()?.ItemId ?? 0,
                command.MoneyType, command.Price, grants.FirstOrDefault()?.SlotKey ?? 0, command.PurchaseKey);
            var log = entry.Purchases.Prepend(audit).Take(MaxRowsPerPlayer).ToList();
            ct.ThrowIfCancellationRequested();
            // Last check before the first write: a gift already taken or removed refuses the whole purchase.
            if (command.PresentId > 0)
                (presents ?? throw new InvalidOperationException("This row keeps no gifts, so it cannot deliver one."))
                    .AcceptLocked(command.PresentId, user);
            entry.Bags[(int)InventoryBag.CharacterBag] = updated;
            entry.Purchases = log;
            entry.Money = charged;
            entry.LastPurchaseSlotKey = highest;
            return ValueTask.FromResult<ShopPurchaseResult?>(new(charged, grants));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct)
    {
        if (userNum <= 0 || maxCount is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<IReadOnlyList<ShopPurchaseRecord>>([]);
        lock (entry.Gate)
            return ValueTask.FromResult<IReadOnlyList<ShopPurchaseRecord>>(entry.Purchases.Take(maxCount).ToArray());
    }
}
