using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Pins which catalog layout each legacy build ends up with through its base in packets/builds.yaml.</summary>
public sealed class LegacyLayoutInheritanceTests
{
    [Theory]
    [InlineData("cn-735", "cn-717")]
    [InlineData("cn-404", "id-400")]
    [InlineData("cn-406", "id-400")]
    [InlineData("cn-706", "id-1809")]
    public void BuildsShareTheirBasesLayout(string build, string sameAs) =>
        Assert.Equal(LegacyLayouts.Of(sameAs), LegacyLayouts.Of(build));

    [Fact]
    public void Cn717RetainsOrderedTabsAndTheWorkbookDefaultSubcategories()
    {
        Assert.Equal(new EnterShopReplyLayout(true, false, LegacyCategoryTree.OrderedDefaults, false,
            LegacyCatalogTrailer.IdsMain), LegacyLayouts.Of("cn-717"));
        var categories = new[]
        {
            new BubbleFighter.Fantasy.Contracts.Shop.ShopCategoryRow(400, "Characters", 1003,
                [new(402, "Characters", 1308, 1, 0)]),
            new BubbleFighter.Fantasy.Contracts.Shop.ShopCategoryRow(1000, "Main", 1000, []),
        };
        var merged = ShopWire.MergeMainClickDefaults(categories,
            [new(401, "All_Characters", 1307, 1, 2), new(1002, "NewItems", 1001, 1, 1)],
            new Dictionary<int, int> { [400] = 401, [1000] = 1002 });
        Assert.Equal(new[] { 401, 402 }, merged[0].Subcategories.Select(sub => sub.Id));
        Assert.Equal(2, merged[0].Subcategories[0].PageType);
        Assert.Equal(1, Assert.Single(merged[1].Subcategories).PageType);
        var body = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-717"), 0, [], merged);
        Assert.Equal(212, body.Length);
        Assert.Equal(2, BitConverter.ToInt32(body, 28));
        Assert.Equal(1000, BitConverter.ToInt32(body, 32));
        Assert.Equal(1000, BitConverter.ToInt32(body, body.Length - 4));
    }

    [Fact]
    public void TheBaselineAndTheCn279DifferencesAreStated()
    {
        Assert.Equal(new EnterShopReplyLayout(true, false, LegacyCategoryTree.Ordered, false, LegacyCatalogTrailer.IdsMain),
            LegacyLayouts.Of("kr-812"));
        Assert.Equal(new EnterShopReplyLayout(false, true, LegacyCategoryTree.LandingDefaults, false, LegacyCatalogTrailer.None),
            LegacyLayouts.Of("cn-279"));
    }

    /// <summary>Pins the default All page and distinct native filters for two CN2011 subcategories.</summary>
    [Fact]
    public void Cn279RestoresMainDefaultsWithoutOverwritingSubcategoryFilters()
    {
        var categories = new[]
        {
            new BubbleFighter.Fantasy.Contracts.Shop.ShopCategoryRow(500, "Weapons", 1,
                [new(501, "Primary", 1, 1, 0), new(502, "Secondary", 2, 1, 0)]),
        };
        var merged = ShopWire.MergeMainClickDefaults(categories,
            [new(500, "All", 0, 1, 2)], new Dictionary<int, int>());
        var main = Assert.Single(merged);
        Assert.Equal(new[] { 500, 501, 502 }, main.Subcategories.Select(sub => sub.Id));
        var body = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-279"), 0, [], merged);
        // cn-279 sub_66AE10 falls back to the main id; cn-279 sub_66AB50 filters modes 2/0 by main/sub respectively.
        Assert.Equal(136, body.Length);
        Assert.Equal(500, BitConverter.ToInt32(body, 20));
        Assert.Equal(3, BitConverter.ToInt32(body, 46));
        Assert.Equal(2, BitConverter.ToInt32(body, 68));
        Assert.Equal(0, BitConverter.ToInt32(body, 98));
        Assert.Equal(0, BitConverter.ToInt32(body, 132));
    }

    [Fact]
    public void TheCn406CatalogReplyIsEmptyThirtyTwoBytesWithNoTrailer()
    {
        // cn-406 inherits id-400's catalog (token, u16 stocks, landing_defaults, main page types, no
        // trailer): the empty body is result, has-data, the echoed token, an empty card vector, the
        // i64 between the vectors and an empty category vector, and nothing after it.
        Assert.Equal(LegacyLayouts.Of("id-400"), LegacyLayouts.Of("cn-406"));
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-406"), 0, [], []);
        Assert.Equal(32, payload.Length);
        Assert.Equal(
            "00000000" + "01000000" + "0000000000000000" + "00000000"
            + "0000000000000000" + "00000000",
            Convert.ToHexString(payload));
        var withToken = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-406"), 0x1122334455667788L, [], []);
        Assert.Equal("8877665544332211", Convert.ToHexString(withToken.AsSpan(8, 8)));
    }
}
