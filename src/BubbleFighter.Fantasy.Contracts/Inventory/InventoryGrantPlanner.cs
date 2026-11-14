using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>How a catalog entry's grant behaves once owned.</summary>
public enum InventoryGrantKind
{
    /// <summary>Stacks quantity on an unowned or unexpired permanent-lifetime row, bounded by the catalog count limit.</summary>
    Count,
    /// <summary>Extends or starts a finite deadline from the later of now and the stored expiry, whole remaining days rounded at half a second.</summary>
    Days,
    /// <summary>One unbounded, unstackable grant: acquirable while the item is not owned, or over a timed copy it makes permanent.</summary>
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
public sealed record InventoryGrantTerm(int ItemId, InventoryGrantKind Kind, int Amount,
    int Maximum, bool RejectOwned);

/// <summary>Plans one item entry per catalog identity; persistence checks the expected state under its player lock.</summary>
public static class InventoryGrantPlanner
{
    /// <summary>
    /// Turns grant terms into expected-state grants the purchase store verifies atomically. Every term
    /// group for one item id must agree on kind, bound and ownership rule; duplicate stored instances
    /// of an item require reconciliation before planning; and each kind has its own renewal rule —
    /// count items stack only unexpired permanent rows, timed items renew from the stored deadline
    /// (whole remaining days, half-second rounding, per <c>kr-812 sub_978350</c> and
    /// <c>kr-812 sub_4C75D0</c>), life items only while the stored charges are used up (same
    /// <c>kr-812 sub_978350</c> rule), and permanent items while unowned or over a timed copy, whose
    /// row then loses its deadline. Rejections are purchase-level user outcomes, not store faults,
    /// and each names the reason the client can show.
    /// </summary>
    /// <param name="userNum">The owner of every row supplied for grant planning.</param>
    /// <param name="terms">Catalog-resolved grant kinds, amounts and limits for this operation.</param>
    /// <param name="bag">The complete character bag from the operation's inventory snapshot.</param>
    /// <param name="now">Authoritative time used to calculate finite renewals.</param>
    /// <returns>Conditional grants that must be revalidated inside the committing account transaction.</returns>
    /// <exception cref="ArgumentException">Thrown for unbounded inputs or a foreign bag.</exception>
    /// <exception cref="InventoryGrantRejectedException">Thrown for incompatible terms, invalid amounts, cap overruns, owned-item rules or unreconciled duplicates.</exception>
    public static IReadOnlyList<ShopPurchaseGrant> Plan(long userNum, IReadOnlyList<InventoryGrantTerm> terms,
        IReadOnlyList<InventorySlotRecord> bag, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(bag);
        if (userNum <= 0 || terms.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag
            || bag.Count > InventoryLimits.MaxInventorySlotsPerBag || terms.Any(term => term is null)
            || bag.Any(row => row is null))
            throw new ArgumentException("Purchase planning requires a valid player and bounded inventory inputs.");
        if (bag.Any(s => s.UserNum != userNum || s.Bag != InventoryBag.CharacterBag))
            throw new ArgumentException("Purchase planning received another player's bag or a non-character bag.");

        var grants = new List<ShopPurchaseGrant>(terms.Count);
        foreach (var group in terms.GroupBy(t => t.ItemId))
        {
            var term = group.First();
            if (term.ItemId <= 0 || !Enum.IsDefined(term.Kind) || term.Maximum <= 0
                || group.Any(t => t.Kind != term.Kind || t.Maximum != term.Maximum || t.RejectOwned != term.RejectOwned))
                throw new InventoryGrantRejectedException("The purchase contains incompatible or unsupported item terms.");
            var amount = group.Sum(t => (long)t.Amount);
            if (group.Any(t => t.Amount <= 0) || amount > int.MaxValue
                || (term.Kind == InventoryGrantKind.Permanent && amount != 1))
                throw new InventoryGrantRejectedException("The purchase amount is invalid or duplicates permanent ownership.");
            var found = bag.Where(s => s.ItemId == term.ItemId).Take(2).ToArray();
            if (found.Length > 1)
                throw new InventoryGrantRejectedException($"Item {term.ItemId} has multiple stored instances and requires reconciliation.");
            var existing = found.SingleOrDefault();
            if (existing is not null && term.RejectOwned)
                throw new InventoryGrantRejectedException($"Item {term.ItemId} cannot be acquired again while owned.",
                    ShopPurchaseRefusal.AlreadyOwned);

            int quantity;
            InventoryLifetime lifetime;
            switch (term.Kind)
            {
                case InventoryGrantKind.Count:
                    if (existing is { Quantity: < 0 } || existing?.Lifetime.Kind is InventoryLifetimeKind.ExpiresAt or InventoryLifetimeKind.Life)
                        throw new InventoryGrantRejectedException("The stored count item has incompatible quantity or expiry metadata.");
                    var total = (existing?.Quantity ?? 0) + amount;
                    if (total > term.Maximum)
                        throw new InventoryGrantRejectedException($"Item {term.ItemId} would exceed its catalog count limit.",
                            ShopPurchaseRefusal.LimitReached);
                    quantity = checked((int)total);
                    lifetime = InventoryLifetime.Permanent;
                    break;
                case InventoryGrantKind.Days:
                    if (existing is not null && existing.Lifetime.Kind != InventoryLifetimeKind.ExpiresAt)
                        throw new InventoryGrantRejectedException("Timed renewal requires an established finite deadline.",
                            existing.Lifetime.Kind == InventoryLifetimeKind.Permanent
                                ? ShopPurchaseRefusal.AlreadyOwned : ShopPurchaseRefusal.Unspecified);
                    var start = existing is null ? now
                        : DateTimeOffset.FromUnixTimeMilliseconds(existing.Lifetime.ExpiresAtUnixMilliseconds!.Value);
                    if (start < now)
                        start = now;
                    // kr-812 sub_978350 / kr-812 sub_4C75D0 limit whole remaining days, rounded at half a second.
                    var wholeDays = (long)Math.Truncate((start - now).TotalDays + 0.5 / 86400);
                    if (wholeDays + amount > term.Maximum)
                        throw new InventoryGrantRejectedException($"Item {term.ItemId} would exceed its catalog day limit.",
                            ShopPurchaseRefusal.LimitReached);
                    lifetime = InventoryLifetime.Until(start.AddDays(amount));
                    quantity = 1;
                    break;
                case InventoryGrantKind.Life:
                    // kr-812 sub_978350: an owned kind 3 item may be acquired again only while its life is 0 or less.
                    if (existing is not null && (existing.Lifetime.Kind != InventoryLifetimeKind.Life || existing.Quantity > 0))
                        throw new InventoryGrantRejectedException($"Item {term.ItemId} cannot be acquired again while it still has life.",
                            ShopPurchaseRefusal.NoMoreAllowed);
                    if (amount > term.Maximum)
                        throw new InventoryGrantRejectedException($"Item {term.ItemId} would exceed its catalog life limit.",
                            ShopPurchaseRefusal.LimitReached);
                    lifetime = InventoryLifetime.Life;
                    quantity = checked((int)amount);
                    break;
                case InventoryGrantKind.Permanent:
                    // A timed copy is upgraded in place: the same row keeps its slot and loses its deadline.
                    if (existing is not null && existing.Lifetime.Kind != InventoryLifetimeKind.ExpiresAt)
                        throw new InventoryGrantRejectedException("The unlimited stock cannot be acquired while the item is already owned.",
                            ShopPurchaseRefusal.AlreadyOwned);
                    lifetime = InventoryLifetime.Permanent;
                    quantity = 1;
                    break;
                default:
                    throw new InventoryGrantRejectedException("The item grant kind is unsupported.");
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

/// <summary>A catalog grant cannot be applied to the supplied ownership without violating its native terms.</summary>
/// <param name="message">The reason the grant plan was rejected before any inventory write.</param>
/// <param name="refusal">The existing inventory-purchase outcome corresponding to the refusal.</param>
public sealed class InventoryGrantRejectedException(string message,
    ShopPurchaseRefusal refusal = ShopPurchaseRefusal.Unspecified) : InvalidOperationException(message)
{
    /// <summary>Gets the established user-visible inventory grant outcome.</summary>
    public ShopPurchaseRefusal Refusal { get; } = refusal;
}
