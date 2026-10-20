using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
namespace BubbleFighter.Fantasy.Contracts.Shop;

/// <summary>One committed purchase audit entry, retained independently of the inventory.</summary>
/// <param name="UserNum">The owning player.</param>
/// <param name="StockSetId">The purchased bundle, or zero for an individual stock.</param>
/// <param name="ItemId">The first granted equipment item, or zero for a currency-only reward.</param>
/// <param name="MoneyType">The charged catalog currency, cash or lucci.</param>
/// <param name="Price">The price actually deducted.</param>
/// <param name="SlotKey">The first granted inventory identity, or zero for a currency-only reward.</param>
/// <param name="PurchaseKey">The exact Stock or StockSet request key.</param>
public sealed record ShopPurchaseRecord(long UserNum, int StockSetId, int ItemId,
    string MoneyType, int Price, long SlotKey, int PurchaseKey = 0);

/// <summary>How a grant identifies the inventory state it is allowed to change.</summary>
public enum ShopGrantMode
{
    /// <summary>Allocates another independent inventory instance without merging existing items.</summary>
    AppendInstance = 0,

    /// <summary>Requires the item's complete stored state to match the expected single slot or absence.</summary>
    MatchItem = 1,
}

/// <summary>A catalog-resolved inventory value to commit with a purchase.</summary>
/// <param name="ItemId">The equipment identity resolved from the selected stock.</param>
/// <param name="Quantity">The item count the bag entry starts with.</param>
public sealed record ShopPurchaseGrant(int ItemId, int Quantity)
{
    /// <summary>Gets the catalog-resolved lifetime to persist atomically with the wallet charge and grant.</summary>
    public InventoryLifetime Lifetime { get; init; }

    /// <summary>Gets whether to append an instance or conditionally create/update one item entry.</summary>
    public ShopGrantMode Mode { get; init; }

    /// <summary>
    /// Gets the exact state read before calculating a MatchItem grant. Null requires that the item
    /// is absent. A matching existing slot keeps its identity, flags and position while quantity
    /// and lifetime are replaced with the grant's values.
    /// </summary>
    public InventorySlotRecord? ExpectedSlot { get; init; }
}

/// <summary>Validated purchase inputs; the store owns balance reads and identity allocation.</summary>
/// <param name="SeedWallet">Initial wallet used only when the player has no stored wallet.</param>
/// <param name="PurchaseKey">Exact Stock or StockSet key received from the client.</param>
/// <param name="StockSetId">Bundle identity, or zero for an individual stock.</param>
/// <param name="MoneyType">Catalog currency, cash or lucci.</param>
/// <param name="Price">Nonnegative catalog price.</param>
/// <param name="SlotKeyBase">Lower bound of the server-allocated identity range for this player.</param>
/// <param name="Grants">All items granted for the single charge.</param>
public sealed record ShopPurchaseCommand(PlayerWallet SeedWallet, int PurchaseKey, int StockSetId,
    string MoneyType, int Price, long SlotKeyBase, IReadOnlyList<ShopPurchaseGrant> Grants)
{
    /// <summary>Gets the Lucci reward credited under the same wallet lock as the inventory grants.</summary>
    public int LucciReward { get; init; }

    /// <summary>
    /// Gets the stored gift (<see cref="Presents.IPresentStore"/>) this purchase delivers to the buying player, or zero
    /// for an ordinary purchase. The store marks the gift taken in the same transaction that applies the grants and
    /// throws <see cref="Presents.PresentUnavailableException"/> without changing anything when the gift is no longer
    /// pending for the player.
    /// </summary>
    public long PresentId { get; init; }

    /// <summary>Rejects invalid inputs before any storage is modified.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SeedWallet);
        ArgumentNullException.ThrowIfNull(Grants);
        if (SeedWallet.UserNum <= 0 || PurchaseKey <= 0 || StockSetId < 0 || SlotKeyBase <= 0 || PresentId < 0)
            throw new ArgumentOutOfRangeException(nameof(PurchaseKey), "Purchase identities must be positive.");
        if (Price < 0 || MoneyType is not ("cash" or "lucci"))
            throw new ArgumentException("A purchase requires a nonnegative price and a known currency.");
        if (SeedWallet.Lucci < 0 || SeedWallet.Cash < 0)
            throw new ArgumentException("Starting balances cannot be negative.");
        if (LucciReward is < 0 or > 1000000 || LucciReward > 0 && (Price != 0 || MoneyType != "lucci")
            || Grants.Count == 0 && LucciReward == 0
            || Grants.Count > InventoryLimits.MaxInventorySlotsPerBag || Grants.Any(g => g.ItemId <= 0))
            throw new ArgumentException("Purchase grants must contain a bounded list of positive item ids.");
        foreach (var grant in Grants)
        {
            if (!Enum.IsDefined(grant.Mode))
                throw new ArgumentException("The inventory grant mode is not supported.");
            if (grant.ExpectedSlot is { } expected && (grant.Mode != ShopGrantMode.MatchItem
                || expected.UserNum != SeedWallet.UserNum || expected.Bag != InventoryBag.CharacterBag
                || expected.ItemId != grant.ItemId || expected.SlotKey <= 0))
                throw new ArgumentException("A conditional grant must expect a character-bag slot of the same player and item.");
            if (grant.Mode == ShopGrantMode.MatchItem && Grants.Count(g => g.ItemId == grant.ItemId) != 1)
                throw new ArgumentException("A conditional purchase may change each item only once.");
        }
    }

    /// <summary>
    /// Checks conditional grants against the locked character bag before any charge or grant.
    /// A changed quantity, lifetime, flag, identity, missing row or duplicate item invalidates the plan.
    /// </summary>
    /// <param name="slots">The complete character bag read under the store's player lock.</param>
    public void ValidateInventorySnapshot(IReadOnlyList<InventorySlotRecord> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        foreach (var grant in Grants.Where(g => g.Mode == ShopGrantMode.MatchItem))
        {
            var matches = slots.Where(s => s.ItemId == grant.ItemId).Take(2).ToArray();
            if (grant.ExpectedSlot is null ? matches.Length != 0
                : matches.Length != 1 || matches[0] != grant.ExpectedSlot)
                throw new ShopPurchaseConflictException(grant.ItemId);
        }
    }
}

/// <summary>An item changed after purchase planning; the transaction has not committed and must be replanned.</summary>
public sealed class ShopPurchaseConflictException : InvalidOperationException
{
    /// <summary>Identifies the item whose locked state no longer matches the purchase plan.</summary>
    /// <param name="itemId">The conflicting equipment identity.</param>
    public ShopPurchaseConflictException(int itemId)
        : base($"Inventory item {itemId} changed before the purchase could commit.") => ItemId = itemId;

    /// <summary>Gets the item that must be read again before another purchase attempt.</summary>
    public int ItemId { get; }
}

/// <summary>The wallet and exact grants made visible by one successful commit.</summary>
/// <param name="Wallet">The committed balance snapshot.</param>
/// <param name="Grants">The committed inventory rows in grant order.</param>
public sealed record ShopPurchaseResult(PlayerWallet Wallet, IReadOnlyList<InventorySlotRecord> Grants);

/// <summary>Commits wallet, inventory and purchase audit changes as one indivisible operation.</summary>
public interface IShopPurchaseStore
{
    /// <summary>
    /// Atomically charges, applies every grant and appends the audit entry. Returns null for
    /// insufficient funds. Stale conditional grants throw ShopPurchaseConflictException.
    /// Conflicts, capacity, cancellation and storage failures leave all three unchanged.
    /// </summary>
    /// <param name="command">Catalog-resolved inputs; prices are never accepted from the client.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct);

    /// <summary>Returns at most 1024 recent audit entries for one player, newest first.</summary>
    /// <param name="userNum">The owning player.</param>
    /// <param name="maxCount">Requested count, between 1 and 1024.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct);
}
