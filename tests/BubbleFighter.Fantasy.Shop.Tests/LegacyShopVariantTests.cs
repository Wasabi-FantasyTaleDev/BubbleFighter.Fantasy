using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the per-build S2C 800 variants resolved by the survey
/// (LEGACY-SHOP-FAMILY-SURVEY): id-400 drops the trailing counted vector and main
/// category, cn-324/cn-279 keep three segments, and cn-279's stock carries a u32
/// type (28 wire bytes).
/// </summary>
public sealed class LegacyShopVariantTests
{
    [Fact]
    public void BundleDurationFilterKeepsPermanentOrLongestWithoutMergingOtherProducts()
    {
        var template = ShopWireTestSupport.Cards[1];
        ShopCardRow Bundle(int key, int item, int days, string currency = "lucci") => template with
        {
            StockSetId = key, MoneyType = currency,
            Stocks = [template.Stocks[0] with { StockId = key, ItemId = item, Duration = days }],
        };
        var cards = new[]
        {
            Bundle(10, 100001, 7), Bundle(11, 100001, 30), Bundle(12, 100001, -1),
            Bundle(13, 100002, 7), Bundle(14, 100002, 30), Bundle(15, 100001, 7, "cash"),
            ShopWireTestSupport.Cards[0],
        };
        var selected = LegacyShopCatalogHandler.LongestDurationCards(cards);
        Assert.Equal(new[] { 12, 14, 15, 100001 }, selected.Select(card => card.CardId));
        Assert.Equal(cards[^1].PrimaryItemId, selected[^1].PrimaryItemId);
        Assert.Single(selected[^1].Stocks);
    }

    [Fact]
    public void SingleItemDurationFilterUsesPermanentTermAndItsPrice()
    {
        var card = ShopWireTestSupport.Cards[0];
        var first = card.Stocks[0];
        card = card with { Stocks = [first with { Duration = 30 }, first with { StockId = 1999999, Type = 9, Duration = -1, SellPrice = 5000 }] };
        var result = Assert.Single(LegacyShopCatalogHandler.LongestDurationCards([card]));
        Assert.Equal(-1, Assert.Single(result.Stocks).Duration);
        Assert.Equal(5000, result.SellPrice);
        Assert.True(ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-324"), 0, [result], []).Length < ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-324"), 0, [card], []).Length);
    }

    [Fact]
    public void CategorySwitchKeepsClothingChoicesAndExplicitItemsOverrideSwitches()
    {
        var template = ShopWireTestSupport.Cards[0];
        ShopCardRow Item(int id, string category) => template with
        {
            PrimaryItemId = id, GrantCategories = [category],
            Stocks = [template.Stocks[0] with { ItemId = id, Duration = 7 },
                template.Stocks[0] with { ItemId = id, StockId = 1999000 + id, Duration = 30 }],
        };
        var cards = new[] { Item(1, "guns"), Item(2, "clothes"), Item(3, "grenades"), Item(4, "hammers") };
        var policy = new BubbleFighter.Fantasy.Shop.ShopOptions { PreferLongestClothes = false };
        var selected = LegacyShopCatalogHandler.LongestDurationCards(cards, policy);
        Assert.Equal(new[] { 1, 2, 1, 1 }, selected.Select(card => card.Stocks.Count));
        policy.PreferLongestDuration = false;
        policy.LongestTermItemIds = [2];
        selected = LegacyShopCatalogHandler.LongestDurationCards(cards, policy);
        Assert.Equal(new[] { 2, 1, 2, 2 }, selected.Select(card => card.Stocks.Count));
        Assert.Equal(30, Assert.Single(selected[1].Stocks).Duration);
        var packages = new[] { cards[1] with { StockSetId = 2100001, Stocks = [cards[1].Stocks[0]] },
            cards[1] with { StockSetId = 2100002, Stocks = [cards[1].Stocks[1]] } };
        Assert.Single(LegacyShopCatalogHandler.LongestDurationCards(packages, new()));
        Assert.Equal(2, LegacyShopCatalogHandler.LongestDurationCards(packages, new() { PreferLongestPackages = false }).Count);
    }

    [Fact]
    public void CatalogBudgetNeverIncludesAnOversizedFirstCard()
    {
        var (cards, dropped) = ShopWire.SelectBoundedCards(ShopWireTestSupport.Cards, 1);
        Assert.Empty(cards);
        Assert.Equal(ShopWireTestSupport.Cards.Length, dropped);
        Assert.Throws<ArgumentOutOfRangeException>(() => ShopWire.SelectBoundedCards(ShopWireTestSupport.Cards, -1));
    }

    [Fact]
    public void RepEnterShop400KeepsTheInlineStock()
    {
        var payload = ShopWireTestSupport.EnterShop400();
        // sub_45FB30: after the 52-byte card head, this+18 is an inline stock
        // (stock id 1000001), and only then sub_460B20 reads the vector count.
        Assert.Equal(1000001, BitConverter.ToInt32(payload.AsSpan(72, 4)));
        Assert.Equal(1, BitConverter.ToInt32(payload.AsSpan(98, 4)));
        // 400 drops the trailing counted vector and main-category i32 (8 bytes).
        Assert.Equal(ShopWireTestSupport.EnterShop812().Length - 8, payload.Length);
        // Fixture pageType is 0. id-400 writes 2 so a main-tab click lists that main's cards.
        Assert.Equal(2, BitConverter.ToInt32(payload.AsSpan(payload.Length - 4, 4)));
    }

    [Fact]
    public void RepEnterShop1809AddsTheTrailingCountedVector()
    {
        var payload = ShopWireTestSupport.EnterShop1809();
        // sub_7BEFF0 reads the id-400 field list, then a counted u32 vector:
        // the body grows by the empty vector's 4-byte count and ends on it.
        Assert.Equal(ShopWireTestSupport.EnterShop400().Length + 4, payload.Length);
        Assert.Equal(294, payload.Length);
        Assert.Equal(0, BitConverter.ToInt32(payload.AsSpan(payload.Length - 4, 4)));
        // The tab filter id-1809 sub_90DD50 branches on pageType as id-400 sub_8E4C00
        // does, so the sub record carries id-400's pageType 2 (fixture row: 0).
        Assert.Equal(2, BitConverter.ToInt32(payload.AsSpan(payload.Length - 8, 4)));
    }

    [Fact]
    public void MergeMainClickDefaultsReattachesAllOfMainAndNamedDefault()
    {
        // Card-linked tree has only Shoes. The click filter looks up sub id 100
        // (All_Clothes) or the sheet default All_Characters (401).
        var categories = new[]
        {
            new ShopCategoryRow(100, "Clothes", 1,
                [new ShopSubcategoryRow(106, "Shoes", 1107, 1, 0)]),
            new ShopCategoryRow(400, "Characters", 2,
                [new ShopSubcategoryRow(402, "Characters", 1308, 1, 0)]),
        };
        var shopVisible = new[]
        {
            new ShopSubcategoryRow(100, "All_Clothes", 1100, 1, 2),
            new ShopSubcategoryRow(106, "Shoes", 1107, 1, 0),
            new ShopSubcategoryRow(401, "All_Characters", 1307, 1, 2),
            new ShopSubcategoryRow(402, "Characters", 1308, 1, 0),
        };
        var defaults = new Dictionary<int, int> { [400] = 401 };
        var merged = ShopWire.MergeMainClickDefaults(categories, shopVisible, defaults);
        Assert.Equal([100, 106], merged[0].Subcategories.Select(s => s.Id).ToArray());
        Assert.Equal([401, 402], merged[1].Subcategories.Select(s => s.Id).ToArray());

        var cards = new[] { ShopWireTestSupport.Cards[0] };
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("id-400"), 0, cards, merged);
        // Byte-length pin: one card + two mains (All_Clothes/Shoes, All_Characters/Characters),
        // every subcategory pageType forced to 2. Layout: sub_795AD0 / sub_63B5B0 / sub_798560.
        Assert.Equal(350, payload.Length);
        Assert.Equal(2, BitConverter.ToInt32(payload.AsSpan(payload.Length - 4, 4)));
    }

    [Fact]
    public void EnsureEnterShopLandingMainInjectsMainWhenCardLinkedTreeOmitsIt()
    {
        // id-400 sub_7A9A90 / cn-279 sub_66B390 / cn-324 sub_7087A0 open on 1000;
        // sub_8E1F40 / sub_66B830 / sub_7F82B0 need that id in the tab list.
        var cardLinked = new[]
        {
            new ShopCategoryRow(100, "Clothes", 1004,
                [new ShopSubcategoryRow(107, "SetClothes", 1101, 1, 0)]),
        };
        var shopVisibleMains = new[]
        {
            new ShopCategoryRow(100, "Clothes", 1004, []),
            new ShopCategoryRow(ShopWire.LegacyEnterShopMainCategory, "Main", 1000, []),
        };
        var withLanding = ShopWire.EnsureEnterShopLandingMain(cardLinked, shopVisibleMains);
        Assert.Equal(ShopWire.LegacyEnterShopMainCategory, withLanding[0].Id);
        Assert.Equal(100, withLanding[1].Id);

        var shopVisible = new[]
        {
            new ShopSubcategoryRow(100, "All_Clothes", 1100, 1, 2),
            new ShopSubcategoryRow(107, "SetClothes", 1101, 1, 0),
            new ShopSubcategoryRow(ShopWire.LegacyEnterShopDefaultSubCategory, "NewItems", 1001, 1, 1),
        };
        var defaults = new Dictionary<int, int>
        {
            [ShopWire.LegacyEnterShopMainCategory] = ShopWire.LegacyEnterShopDefaultSubCategory,
        };
        var merged = ShopWire.MergeMainClickDefaults(withLanding, shopVisible, defaults);
        Assert.Equal(
            [ShopWire.LegacyEnterShopDefaultSubCategory],
            merged[0].Subcategories.Select(s => s.Id).ToArray());
        Assert.Equal([100, 107], merged[1].Subcategories.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void RepEnterShop279And324GrowByInjectedLandingMainBytes()
    {
        // Empty-sub Main/1000 stub: i32 id + wide name "Main" + i32 displayOrder +
        // sub count 0. Pins the category-tree growth when the click-gate main is
        // reattached (cn-279 sub_66B830 / cn-324 sub_7F82B0).
        var cardLinked = new[]
        {
            new ShopCategoryRow(100, "Clothes", 1004,
                [new ShopSubcategoryRow(107, "SetClothes", 1101, 1, 0)]),
        };
        var shopVisibleMains = new[]
        {
            new ShopCategoryRow(100, "Clothes", 1004, []),
            new ShopCategoryRow(ShopWire.LegacyEnterShopMainCategory, "Main", 1000, []),
        };
        var withLanding = ShopWire.EnsureEnterShopLandingMain(cardLinked, shopVisibleMains);
        var without279 = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-279"), 0, [], cardLinked);
        var with279 = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-279"), 0, [], withLanding);
        var without324 = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-324"), 0, [], cardLinked);
        var with324 = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("cn-324"), 0, [], withLanding);
        // WideStringPrefixed("Main") = i32 len(4) + 4*WCHAR = 12; plus id/order/subCount = 12.
        const int emptyMainStubBytes = 24;
        Assert.Equal(without279.Length + emptyMainStubBytes, with279.Length);
        Assert.Equal(without324.Length + emptyMainStubBytes, with324.Length);
        Assert.Equal(2, BitConverter.ToInt32(with279.AsSpan(with279.Length - 4, 4)));
        Assert.Equal(0, BitConverter.ToInt32(with324.AsSpan(with324.Length - 4, 4)));
    }

    [Fact]
    public void LegacyShopBuyIsEightBytes()
    {
        var payload = ShopWire.RepLegacyShopBuy(0x0F45EE, 1);
        Assert.Equal(8, payload.Length);
        Assert.Equal(0x0F45EE, BitConverter.ToInt32(payload.AsSpan(0, 4)));
        Assert.Equal(1, BitConverter.ToInt32(payload.AsSpan(4, 4)));
        var request = LegacyShopBuyRequest.Parse(new byte[] { 0xEE, 0x45, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00 });
        Assert.Equal(0x0F45EE, request.Key);
        Assert.Equal(0, request.Unknown4);
    }

    [Fact]
    public void RepEnterShop324DropsHeaderAndTail()
    {
        var payload = ShopWireTestSupport.EnterShop324();
        // result(4) then the card count directly - no has-data, no token.
        Assert.Equal(0, BitConverter.ToInt32(payload.AsSpan(0, 4)));
        Assert.Equal(2, BitConverter.ToInt32(payload.AsSpan(4, 4)));
        // 4 + two cards (108 + 106) + i64 + the category tree (48).
        Assert.Equal(278, payload.Length);
    }

    [Fact]
    public void RepEnterShop279WritesTwentyEightByteStocks()
    {
        var payload = ShopWireTestSupport.EnterShop279();
        // One card, one term: the stock spans 28 bytes (u32 type).
        
        Assert.Equal(0, BitConverter.ToInt32(payload.AsSpan(0, 4)));
        var stockId = BitConverter.ToInt32(payload.AsSpan(60, 4));
        Assert.Equal(1000001, stockId); // card head is 52 bytes, stock starts at 8+52=60
        // The cn-324 form with every 26-byte stock widened to 28: 278 + 2 per card.
        Assert.Equal(286, payload.Length);
    }
}
