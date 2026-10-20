using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

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
