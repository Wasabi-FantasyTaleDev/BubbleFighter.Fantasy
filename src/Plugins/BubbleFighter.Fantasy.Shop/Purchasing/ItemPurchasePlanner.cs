using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

internal enum PurchaseGrantKind { Count, Days, Permanent, Life }

internal sealed record PurchaseGrantTerm(int ItemId, PurchaseGrantKind Kind, int Amount,
    int Maximum, bool RejectOwned);

/// <summary>Plans one item entry per catalog identity; persistence checks the expected state under its player lock.</summary>
internal static class ItemPurchasePlanner
{
    internal static IReadOnlyList<ShopPurchaseGrant> Plan(long userNum, IReadOnlyList<PurchaseGrantTerm> terms,
        IReadOnlyList<InventorySlotRecord> bag, DateTimeOffset now)
    {
        if (userNum <= 0 || terms.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag
            || bag.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException("Purchase planning requires a valid player and bounded inventory inputs.");
        if (bag.Any(s => s.UserNum != userNum || s.Bag != InventoryBag.CharacterBag))
            throw new ArgumentException("Purchase planning received another player's bag or a non-character bag.");

        var grants = new List<ShopPurchaseGrant>(terms.Count);
        foreach (var group in terms.GroupBy(t => t.ItemId))
        {
            var term = group.First();
            if (term.ItemId <= 0 || !Enum.IsDefined(term.Kind) || term.Maximum <= 0
                || group.Any(t => t.Kind != term.Kind || t.Maximum != term.Maximum || t.RejectOwned != term.RejectOwned))
                throw new PurchaseRejectedException("The purchase contains incompatible or unsupported item terms.");
            var amount = group.Sum(t => (long)t.Amount);
            if (group.Any(t => t.Amount <= 0) || amount > int.MaxValue
                || (term.Kind == PurchaseGrantKind.Permanent && amount != 1))
                throw new PurchaseRejectedException("The purchase amount is invalid or duplicates permanent ownership.");
            var found = bag.Where(s => s.ItemId == term.ItemId).Take(2).ToArray();
            if (found.Length > 1)
                throw new PurchaseRejectedException($"Item {term.ItemId} has multiple stored instances and requires reconciliation.");
            var existing = found.SingleOrDefault();
            if (existing is not null && term.RejectOwned)
                throw new PurchaseRejectedException($"Item {term.ItemId} cannot be acquired again while owned.");

            int quantity;
            InventoryLifetime lifetime;
            switch (term.Kind)
            {
                case PurchaseGrantKind.Count:
                    if (existing is { Quantity: < 0 } || existing?.Lifetime.Kind is InventoryLifetimeKind.ExpiresAt or InventoryLifetimeKind.Life)
                        throw new PurchaseRejectedException("The stored count item has incompatible quantity or expiry metadata.");
                    var total = (existing?.Quantity ?? 0) + amount;
                    if (total > term.Maximum)
                        throw new PurchaseRejectedException($"Item {term.ItemId} would exceed its catalog count limit.");
                    quantity = checked((int)total);
                    lifetime = InventoryLifetime.Permanent;
                    break;
                case PurchaseGrantKind.Days:
                    if (existing is not null && existing.Lifetime.Kind != InventoryLifetimeKind.ExpiresAt)
                        throw new PurchaseRejectedException("Timed renewal requires an established finite deadline.");
                    var start = existing is null ? now
                        : DateTimeOffset.FromUnixTimeMilliseconds(existing.Lifetime.ExpiresAtUnixMilliseconds!.Value);
                    if (start < now)
                        start = now;
                    // KR812 0x978350 / 0x4C75D0 limit whole remaining days, rounded at half a second.
                    var wholeDays = (long)Math.Truncate((start - now).TotalDays + 0.5 / 86400);
                    if (wholeDays + amount > term.Maximum)
                        throw new PurchaseRejectedException($"Item {term.ItemId} would exceed its catalog day limit.");
                    lifetime = InventoryLifetime.Until(start.AddDays(amount));
                    quantity = 1;
                    break;
                case PurchaseGrantKind.Life:
                    // kr-812 sub_978350: an owned kind 3 item may be acquired again only while its life is 0 or less.
                    if (existing is not null && (existing.Lifetime.Kind != InventoryLifetimeKind.Life || existing.Quantity > 0))
                        throw new PurchaseRejectedException($"Item {term.ItemId} cannot be acquired again while it still has life.");
                    if (amount > term.Maximum)
                        throw new PurchaseRejectedException($"Item {term.ItemId} would exceed its catalog life limit.");
                    lifetime = InventoryLifetime.Life;
                    quantity = checked((int)amount);
                    break;
                case PurchaseGrantKind.Permanent:
                    if (existing is not null)
                        throw new PurchaseRejectedException("The unlimited stock cannot be acquired while the item is already owned.");
                    lifetime = InventoryLifetime.Permanent;
                    quantity = 1;
                    break;
                default:
                    throw new PurchaseRejectedException("The item grant kind is unsupported.");
            }
            grants.Add(new(term.ItemId, quantity)
            {
                Mode = ShopGrantMode.MatchItem,
                ExpectedSlot = existing,
                Lifetime = lifetime,
            });
        }
        return grants;
    }
}

internal sealed class PurchaseRejectedException(string message) : InvalidOperationException(message);
