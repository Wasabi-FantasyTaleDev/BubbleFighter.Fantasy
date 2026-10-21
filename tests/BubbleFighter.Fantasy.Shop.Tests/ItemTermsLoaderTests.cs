using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ItemTermsLoaderTests
{
    private static readonly string[] Header = ["itemId", "durationType", "maxDuration", "buyAgain", "function"];

    [Fact]
    public void ReadsIndependentTermsAndPreservesBuyAgainKeysWithoutBooleanConversion()
    {
        var items = ItemTermsLoader.Parse([Header,
            ["1", "count", "10", "2000001", "1"], ["2", "day", "360", "0", "0"],
            ["3", "endurance", "2000", "-1", "0"], ["# comment"], []]);
        Assert.Equal(3, items.Count);
        Assert.Equal(2000001, items[1].BuyAgain);
        Assert.Equal(0, items[2].BuyAgain);
        Assert.Equal(-1, items[3].BuyAgain);
        Assert.Equal("count", items[1].DurationType);
        Assert.Equal(360, items[2].MaxDuration);
    }

    [Theory]
    [InlineData("endurance", 1, 7)]
    [InlineData("day", 1, -1)]
    [InlineData("count", 9, -1)]
    [InlineData("count", 1, 0)]
    public void UnsupportedTermsFailBeforeGrantsArePlanned(string durationType, ushort type, int duration)
    {
        var terms = new ItemTerms(1, durationType, 360, 0, 0);
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(new(123, 1, true, type, duration, 100, 100, 0, 0)));
    }

    [Theory]
    [InlineData("0", "day", "360", "0", "0")]
    [InlineData("1", "unknown", "360", "0", "0")]
    [InlineData("1", "day", "0", "0", "0")]
    [InlineData("1", "day", "360", "true", "0")]
    [InlineData("1", "day", "360", "0", "bad")]
    public void InvalidItemTermsFailAtLoad(string id, string duration, string maximum, string buyAgain, string function)
    {
        Assert.Throws<InvalidOperationException>(() => ItemTermsLoader.Parse([Header, [id, duration, maximum, buyAgain, function]]));
    }

    [Fact]
    public void MissingColumnsAndDuplicateDefinitionsFailLoud()
    {
        Assert.Throws<InvalidOperationException>(() => ItemTermsLoader.Parse([["itemId"]]));
        Assert.Throws<InvalidOperationException>(() => ItemTermsLoader.Parse([Header]));
        Assert.Throws<InvalidOperationException>(() => ItemTermsLoader.Parse([Header,
            ["1", "day", "360", "0", "0"], ["1", "count", "10", "0", "0"]]));
    }

    [Fact]
    public void CatalogFunctionRestrictsRepeatAcquisitionIndependentlyOfBuyAgain()
    {
        var term = new ItemTerms(1, "day", 360, 0, 10001).ForStock(new(123, 1, true, 1, 7, 100, 100, 0, 0));
        Assert.True(term.RejectOwned);
        Assert.False(new ItemTerms(1, "day", 360, -1, 0).ForStock(new(123, 1, true, 1, 7, 100, 100, 0, 0)).RejectOwned);
    }

    [Fact]
    public void Cn279AcceptsItsWorkbookPermanentTermWithoutChangingLaterBuilds()
    {
        var terms = new ItemTerms(1, "day", 360, 0, 0);
        var stock = new ShopStockRow(123, 1, true, 1, -1, 100, 100, 0, 0);
        Assert.Equal(PurchaseGrantKind.Permanent, terms.ForStock(stock, Cn279).Kind);
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(stock, Cn324));
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(stock, ItemTermRules.Baseline));
        Assert.Throws<PurchaseRejectedException>(() => new ItemTerms(1, "count", 10, 0, 0)
            .ForStock(stock, Cn279));
    }

    [Theory]
    [InlineData(279)]
    [InlineData(324)]
    public void OlderClientsDoNotApplyTheLaterFunctionOwnershipGate(int build)
    {
        var rules = build == 279 ? Cn279 : Cn324;
        var term = new ItemTerms(1, "day", 360, 0, 10001)
            .ForStock(new(123, 1, true, 1, 7, 100, 100, 0, 0), rules);
        Assert.False(term.RejectOwned);
    }

    // The values profiles/cn-279 and profiles/cn-324 state on their shop rows.
    private static readonly ItemTermRules Cn279 = new([9, 1], RejectRepeatFunction10001: false);
    private static readonly ItemTermRules Cn324 = new([9], RejectRepeatFunction10001: false);
}
