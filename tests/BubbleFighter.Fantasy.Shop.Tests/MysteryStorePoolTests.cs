using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class MysteryStorePoolTests
{
    [Fact]
    public void WalletRewardsAreExcludedWithoutDroppingMembersOfInventorySets()
    {
        var sets = Enumerable.Range(1, 6).ToDictionary(i => i, i => new ShopGift(i, i, 0,
            [new ShopStockRow(i, 100 + i, true, 1, 7, 100, 100, 0, 0)]));
        sets[7] = new(7, 7, 50, [new ShopStockRow(7, 107, true, 1, 7, 100, 100, 0, 0)]);
        sets[8] = new(8, 8, 50, []);
        var catalog = new ShopCatalog("fixture", [], []) { Gifts = new ShopGiftIndex(new Dictionary<int, ShopStockRow>(), sets) };
        var scope = new InventoryLoadoutScope("kr-1256", [100385, .. Enumerable.Range(101, 7)]);
        var offers = MysteryStoreFlow.ResolvePool(new() { CoinItemId = 100385 }, Enumerable.Range(1, 8).ToArray(),
            catalog, stock => new(stock.ItemId, PurchaseGrantKind.Count, 1, 99, false), scope, NullLogger.Instance);
        Assert.Equal(Enumerable.Range(1, 6), offers.Select(offer => offer.NativeKey));
        Assert.All(offers, offer => Assert.Single(offer.Grants));
        Assert.Throws<ConfigException>(() => MysteryStoreFlow.ResolvePool(new() { CoinItemId = 100385 },
            [1, 2, 3, 4, 5, 7, 8], catalog, stock => new(stock.ItemId, PurchaseGrantKind.Count, 1, 99, false),
            scope, NullLogger.Instance));
    }
}
