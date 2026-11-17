using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class MysteryStoreOptionsTests
{
    [Fact]
    public void OfferRangesAreBoundedAndCannotOverflow()
    {
        var options = new ShopMysteryStoreOptions { CoinItemId = 100385, Offers = ["1-2147483647"] };
        Assert.Throws<ConfigException>(() => options.Validate());
        options.Offers = ["2147483642-2147483647"];
        Assert.Equal(6, options.Validate().Count);
        options.Offers = ["1-4096", "4097"];
        Assert.Throws<ConfigException>(() => options.Validate());
    }
}
