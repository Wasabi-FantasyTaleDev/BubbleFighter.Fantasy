using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>How a catalog entry's grant behaves once owned.</summary>
internal enum PurchaseGrantKind
{
    /// <summary>Stacks quantity on an unowned or unexpired permanent-lifetime row, bounded by the catalog count limit.</summary>
    Count,
    /// <summary>Extends or starts a finite deadline from the later of now and the stored expiry, whole remaining days rounded at half a second.</summary>
    Days,
    /// <summary>One unbounded, unstackable grant: only acquirable while the item is not owned.</summary>
    Permanent,
    /// <summary>Uses charges: re-acquirable only while the owned item's life is exhausted.</summary>
    Life,
}

/// <summary>One catalog entry's grant rule, planned against the player's current bag before any store commit.</summary>
/// <param name="ItemId">The granted item id.</param>
/// <param name="Kind">The grant behavior once owned.</param>
/// <param name="Amount">The quantity or duration the entry grants.</param>
/// <param name="Maximum">The catalog's bound on the resulting quantity or duration.</param>
/// <param name="RejectOwned">Whether owning the item already rejects the entry.</param>
internal sealed record PurchaseGrantTerm(int ItemId, PurchaseGrantKind Kind, int Amount,
    int Maximum, bool RejectOwned);

/// <summary>Plans one item entry per catalog identity; persistence checks the expected state under its player lock.</summary>
internal static class ItemPurchasePlanner
{
    /// <summary>
    /// Turns grant terms into expected-state grants the purchase store verifies atomically. Every term
    /// group for one item id must agree on kind, bound and ownership rule; duplicate stored instances
    /// of an item require reconciliation before planning; and each kind has its own renewal rule —
    /// count items stack only unexpired permanent rows, timed items renew from the stored deadline
    /// (whole remaining days, half-second rounding, per <c>kr-812 sub_978350</c> and
    /// <c>kr-812 sub_4C75D0</c>), life items only while the stored charges are used up (same
    /// <c>kr-812 sub_978350</c> rule), and permanent items only while unowned. Rejections are
    /// purchase-level user outcomes, not store faults.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown for unbounded inputs or a foreign bag.</exception>
    /// <exception cref="PurchaseRejectedException">Thrown for incompatible terms, invalid amounts, cap overruns, owned-item rules or unreconciled duplicates.</exception>
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
                    // kr-812 sub_978350 / sub_4C75D0 limit whole remaining days, rounded at half a second.
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

/// <summary>A purchase-level rejection: the user's request is refused, not a store or transport fault.</summary>
internal sealed class PurchaseRejectedException(string message) : InvalidOperationException(message);
