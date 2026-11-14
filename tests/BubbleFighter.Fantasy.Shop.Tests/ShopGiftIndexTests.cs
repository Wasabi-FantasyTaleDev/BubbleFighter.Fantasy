using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>The gift index answers like the dictionary it replaces, without storing a gift per Stock.</summary>
public sealed class ShopGiftIndexTests
{
    private static ShopStockRow Stock(int id, int itemId) => new(id, itemId, true, 1, 7, 100, 100, 0, 0);

    private static ShopGiftIndex Index()
    {
        var stocks = new Dictionary<int, ShopStockRow>
        {
            [10] = Stock(10, 5001),
            [11] = Stock(11, 0),
            [12] = Stock(12, 5002),
        };
        var sets = new Dictionary<int, ShopGift>
        {
            [11] = new(11, 11, 300, []),
            [20] = new(20, 20, 0, [stocks[10], stocks[12]]),
        };
        return new ShopGiftIndex(stocks, sets);
    }

    [Fact]
    public void StockWithAnItemIsAGiftOfItself()
    {
        var index = Index();

        Assert.True(index.TryGetValue(10, out var gift));
        Assert.Equal((10, 0, 0), (gift.Key, gift.StockSetId, gift.Lucci));
        Assert.Equal(5001, Assert.Single(gift.Stocks).ItemId);
    }

    [Fact]
    public void StockWithoutAnItemLeavesItsIdToASet()
    {
        var index = Index();

        Assert.True(index.TryGetValue(11, out var gift));
        Assert.Equal((11, 300), (gift.StockSetId, gift.Lucci));
        Assert.True(index.ContainsKey(20));
        Assert.False(index.ContainsKey(99));
        Assert.False(index.TryGetValue(99, out _));
        Assert.Throws<KeyNotFoundException>(() => index[99]);
    }

    [Fact]
    public void CountAndEnumerationListEveryGiftOnce()
    {
        var index = Index();

        Assert.Equal(4, index.Count);
        Assert.Equal([10, 12, 11, 20], index.Keys);
        Assert.Equal(index.Count, index.Values.Count());
    }
}
