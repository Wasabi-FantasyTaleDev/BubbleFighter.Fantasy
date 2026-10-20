using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the kr-812 <c>gspRepEnterShop</c> form and the bounded live-catalog
/// selection against a 221-card catalog (35256-byte
/// frame): result 0, the echoed token, the per-card sale-window pair,
/// and a card vector that must fit the legacy frame cap. Emit order follows
/// handler <c>sub_820CA0</c> (open main 1000) and <c>sub_9720E0</c> (default sub
/// 1002 / NewItems).
/// </summary>
public sealed class LegacyEnterShopWireTests
{
    private static ShopCardRow Card(int id, int main, int sub, string name = "card") => new(
        StockSetId: 0, Name: name, MoneyType: "lucci", IsOnSale: true, Refundable: false,
        ShowPrice: 100, SellPrice: 100, SellPriceCash: 0, MainCategory: $"m{main}", SubCategory: $"s{sub}",
        PrimaryItemId: id, Stocks: [], MainCategoryId: main, SubCategoryId: sub, DisplayOrder: id);

    /// <summary>
    /// Display orders match the kr-812 / cn-717 MainCategory sheet: Main 1000 is
    /// first, Clothes 1004 later. Workbook row order alone would put Clothes first.
    /// </summary>
    private static readonly ShopCategoryRow[] Categories =
    [
        new(100, "의상", 1004, [new ShopSubcategoryRow(106, "신발", 1, 1, 0)]),
        new(1000, "메 인", 1000, [new ShopSubcategoryRow(1002, "신상품", 1, 1, 0)]),
    ];

    [Fact]
    public void RepEnterShop812PinsTheCaptureForm()
    {
        var cards = new[] { Card(100001, 100, 106) };
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, cards, Categories, mainCategory: ShopWire.LegacyEnterShopMainCategory);
        // result 0, has-data 1, token 0, one card, the i64 separator, the two
        // categories with their subs, the empty counted vector, main 1000.
        Assert.Equal(0, BitConverter.ToInt32(payload.AsSpan(0, 4)));
        Assert.Equal(1, BitConverter.ToInt32(payload.AsSpan(4, 4)));
        // The card's two i64s carry the far-apart sale window. Card start
        // is 20 (result, has-data, token, count) + 4 + 4 + 12 (the 4-char name) +
        // 16 (display order and the two category ids) + 4 (the bool) = 56.
        var from = BitConverter.DoubleToInt64Bits(ShopWire.LegacySaleFrom);
        Assert.Equal(from, BitConverter.ToInt64(payload.AsSpan(56, 8)));
        var tail = Convert.ToHexString(payload.AsSpan(payload.Length - 4, 4));
        Assert.Equal("E8030000", tail);
        // Byte-length pin: one empty-stock card + the two Korean-named categories.
        // Layout unchanged by the emit-order / card-cap selection work.
        Assert.Equal(206, payload.Length);
    }

    [Fact]
    public void RepEnterShop812WritesLandingMainCategoryFirst()
    {
        // sub_96DB90 creates tab widgets in packet order; workbook sheet order puts
        // Clothes (100) before Main (1000). displayOrder sort puts Main at index 0.
        var ordered = ShopWire.OrderEnterShopCategories(Categories);
        Assert.Equal(ShopWire.LegacyEnterShopMainCategory, ordered[0].Id);
        Assert.Equal(100, ordered[1].Id);

        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, [Card(100001, 100, 106)], Categories, ShopWire.LegacyEnterShopMainCategory);
        // After result/has-data/token/card-vector/i64 separator, the category count
        // and first main id: card body ends at a stable offset for this one-card fixture.
        // Locate the category vector by scanning for count=2 followed by main 1000.
        var found = false;
        for (var i = 0; i + 8 <= payload.Length; i++)
        {
            if (BitConverter.ToInt32(payload.AsSpan(i, 4)) != 2)
                continue;
            if (BitConverter.ToInt32(payload.AsSpan(i + 4, 4)) != ShopWire.LegacyEnterShopMainCategory)
                continue;
            found = true;
            break;
        }

        Assert.True(found);
        Assert.Equal(206, payload.Length);
    }

    [Fact]
    public void SelectBoundedCardsPrefersLandingMainAndDefaultSub()
    {
        // Many cheap main-100 cards would fill a tight budget first under ascending
        // main order and leave main 1000 (the landing tab) truncated. Emit order
        // puts 1000 / default sub 1002 ahead so the open page stays populated.
        var cards = new List<ShopCardRow>();
        for (var i = 0; i < 100; i++)
            cards.Add(Card(1000 + i, main: 100, sub: 106));
        for (var i = 0; i < 10; i++)
            cards.Add(Card(1500 + i, main: 1000, sub: 1001));
        for (var i = 0; i < 40; i++)
            cards.Add(Card(2000 + i, main: 1000, sub: ShopWire.LegacyEnterShopDefaultSubCategory));
        for (var i = 0; i < 10; i++)
            cards.Add(Card(3000 + i, main: 500, sub: 501));

        var (selected, dropped) = ShopWire.SelectBoundedCards(cards, budget: 6000);
        Assert.True(selected.Count < cards.Count);
        Assert.Equal(cards.Count - selected.Count, dropped);
        Assert.Contains(selected, card => card.MainCategoryId == ShopWire.LegacyEnterShopMainCategory);
        Assert.Equal(ShopWire.LegacyEnterShopMainCategory, selected[0].MainCategoryId);
        Assert.Equal(ShopWire.LegacyEnterShopDefaultSubCategory, selected[0].SubCategoryId);
        Assert.Equal(2000, selected[0].PrimaryItemId);
        // HotItems (1001) under main 1000 still precedes main 500.
        var firstDefaultSub = selected.ToList().FindIndex(c =>
            c.MainCategoryId == ShopWire.LegacyEnterShopMainCategory
            && c.SubCategoryId == ShopWire.LegacyEnterShopDefaultSubCategory);
        var firstOtherSub = selected.ToList().FindIndex(c =>
            c.MainCategoryId == ShopWire.LegacyEnterShopMainCategory
            && c.SubCategoryId == 1001);
        Assert.True(firstDefaultSub >= 0);
        Assert.True(firstOtherSub < 0 || firstDefaultSub < firstOtherSub);
        // Main 500 follows 1000 and still precedes numeric mains such as 100.
        var first500 = selected.ToList().FindIndex(c => c.MainCategoryId == 500);
        var first100 = selected.ToList().FindIndex(c => c.MainCategoryId == 100);
        Assert.True(first500 >= 0);
        Assert.True(first100 < 0 || first500 < first100);
        var encoded = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, selected, Categories, ShopWire.LegacyEnterShopMainCategory);
        Assert.True(encoded.Length < 65535);
    }

    [Fact]
    public void SelectBoundedCardsHonoursTheCaptureCardCap()
    {
        var cards = new List<ShopCardRow>();
        for (var i = 0; i < ShopWire.LegacyEnterShopCardCap + 50; i++)
            cards.Add(Card(10_000 + i, main: ShopWire.LegacyEnterShopMainCategory, sub: ShopWire.LegacyEnterShopDefaultSubCategory));

        var (selected, dropped) = ShopWire.SelectBoundedCards(cards, budget: 1_000_000);
        Assert.Equal(ShopWire.LegacyEnterShopCardCap, selected.Count);
        Assert.Equal(50, dropped);
        var (all, omitted) = ShopWire.SelectBoundedCards(cards, budget: 1_000_000, maxCards: 0);
        Assert.Equal(cards.Count, all.Count);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void RepEnterShop812FixturePinsStableByteLength()
    {
        // Shared two-card fixture used by the per-build variant pins. Length is
        // independent of SelectBoundedCards; keep it pinned so a WriteCards /
        // WriteCategories drift fails here rather than only in relative diffs.
        Assert.Equal(298, ShopWireTestSupport.EnterShop812().Length);
    }

    [Fact]
    public void TheLandingMainWithoutCardsEchoesTheFirstTreeMainThatHasCards()
    {
        // The workbook has no card under main 1000, so echoing the request's 1000 opens an
        // empty grid (sub_9720E0 matches card main to the echoed main). Tree order is 1000, 100.
        var cards = new[] { Card(100001, 100, 106) };
        var main = ShopWire.ResolveEnterShopMainCategory(ShopWire.LegacyEnterShopMainCategory, cards, Categories);
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, cards, Categories, main);

        Assert.Equal(100, BitConverter.ToInt32(payload.AsSpan(payload.Length - 4, 4)));
        // The tree itself is unchanged: the same length as the 1000-echo form.
        Assert.Equal(206, payload.Length);
    }

    [Fact]
    public void ARequestedMainThatHasCardsIsEchoedUnchanged()
    {
        var cards = new[] { Card(100001, 100, 106), Card(200001, 1000, 1002) };
        var main = ShopWire.ResolveEnterShopMainCategory(ShopWire.LegacyEnterShopMainCategory, cards, Categories);
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("kr-812"), 0, cards, Categories, main);

        Assert.Equal(1000, BitConverter.ToInt32(payload.AsSpan(payload.Length - 4, 4)));
        Assert.Equal(100, ShopWire.ResolveEnterShopMainCategory(100, cards, Categories));
    }
}
