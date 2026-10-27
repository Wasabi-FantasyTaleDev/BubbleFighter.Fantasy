using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>Binds one catalog entry to its grant rule before the store commit; bounds the stock list to the inventory cap.</summary>
/// <exception cref="ArgumentException">Thrown when the stock list is empty or exceeds the bag bound.</exception>
internal sealed class ItemPurchasePolicy(ConditionalShopPurchaser purchaser, Func<ShopStockRow, PurchaseGrantTerm> resolve)
{
    internal ValueTask<ShopPurchaseResult?> CommitAsync(ShopPurchaseCommand command,
        IReadOnlyList<ShopStockRow> stocks, CancellationToken ct)
    {
        if (stocks.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException("Purchase stock count is outside the inventory bound.");
        var terms = stocks.Select(resolve).ToArray();
        return purchaser.CommitAsync(command, terms, ct);
    }
}

/// <summary>Replans a catalog purchase after concurrent item changes within an explicit attempt budget.</summary>
internal sealed class ConditionalShopPurchaser
{
    private readonly IShopPurchaseStore _purchases;
    private readonly IInventoryRepository _inventory;
    private readonly TimeProvider _clock;
    private readonly int _maxAttempts;

    internal ConditionalShopPurchaser(IShopPurchaseStore purchases, IInventoryRepository inventory,
        TimeProvider clock, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(purchases);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(clock);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        _purchases = purchases;
        _inventory = inventory;
        _clock = clock;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Commits a purchase under concurrent-item replanning: read the bag, plan the expected grants,
    /// and let the store verify them atomically under its player lock. A store-level pre-commit
    /// conflict replans and retries within the attempt budget; every other failure — cancellation,
    /// transport or database — propagates, and a budget-exhausted conflict surfaces as the store's
    /// own exception. A currency-only reward skips the plan and commits the wallet credit directly.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the attempt budget is not positive (constructor) or the command is invalid.</exception>
    internal async ValueTask<ShopPurchaseResult?> CommitAsync(ShopPurchaseCommand purchase,
        IReadOnlyList<PurchaseGrantTerm> terms, CancellationToken ct)
    {
        // A currency-only gift has no inventory plan; its receipt and wallet credit still share the store transaction.
        if (terms.Count == 0 && purchase.LucciReward > 0)
            return await _purchases.PurchaseAsync(purchase, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var bag = await _inventory.ListSlotsAsync(purchase.SeedWallet.UserNum, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            var grants = ItemPurchasePlanner.Plan(purchase.SeedWallet.UserNum, terms, bag, _clock.GetUtcNow());
            try
            {
                return await _purchases.PurchaseAsync(purchase with { Grants = grants }, ct).ConfigureAwait(false);
            }
            catch (ShopPurchaseConflictException) when (attempt < _maxAttempts - 1)
            {
                // Only the store's explicit pre-commit conflict is retryable; transport and database errors are not.
            }
        }
    }
}
