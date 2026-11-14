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
internal sealed record PurchaseGrantTerm(int ItemId, PurchaseGrantKind Kind, int Amount,
    int Maximum, bool RejectOwned);

/// <summary>Adapts shared catalog grant planning to the shop's existing refusal outcomes.</summary>
internal static class ItemPurchasePlanner
{
    internal static IReadOnlyList<ShopPurchaseGrant> Plan(long userNum, IReadOnlyList<PurchaseGrantTerm> terms,
        IReadOnlyList<InventorySlotRecord> bag, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException("Purchase planning requires bounded inventory inputs.", nameof(terms));
        var resolved = terms.Select(term => new InventoryGrantTerm(term.ItemId, term.Kind switch
        {
            PurchaseGrantKind.Count => InventoryGrantKind.Count,
            PurchaseGrantKind.Days => InventoryGrantKind.Days,
            PurchaseGrantKind.Permanent => InventoryGrantKind.Permanent,
            PurchaseGrantKind.Life => InventoryGrantKind.Life,
            _ => throw new PurchaseRejectedException("The inventory grant kind is unsupported."),
        }, term.Amount, term.Maximum, term.RejectOwned)).ToArray();
        try { return InventoryGrantPlanner.Plan(userNum, resolved, bag, now); }
        catch (InventoryGrantRejectedException exception)
        {
            throw new PurchaseRejectedException(exception.Message, exception.Refusal);
        }
    }
}

/// <summary>A purchase-level rejection: the user's request is refused, not a store or transport fault.</summary>
/// <param name="message">The logged reason.</param>
/// <param name="refusal">The reason the client is told.</param>
internal sealed class PurchaseRejectedException(string message,
    ShopPurchaseRefusal refusal = ShopPurchaseRefusal.Unspecified) : InvalidOperationException(message)
{
    /// <summary>Gets the reason the client is told.</summary>
    internal ShopPurchaseRefusal Refusal { get; } = refusal;
}
