using BubbleFighter.Fantasy.Contracts.Shop;
using System.Globalization;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Absolute-hex pins of the kr-1256 shop reply bodies the raw handlers send, written BEFORE the bodies move to
/// the packet tables: the parity tests prove the table writer equals <see cref="ShopWire"/>, and these pins fix
/// the exact bytes themselves, so a move that drifts both sides together still fails. Fixed inputs reuse the
/// parity fixtures so a difference points at the same row.
/// </summary>
public sealed class ShopReplyHexPinsTests
{
    [Fact]
    public void RepEnterShopEmptyPinsFortyBytes()
    {
        Assert.Equal(40, ShopWire.RepEnterShopEmpty(42L).Length);
        Assert.Equal(
            "01000000"          // result 1
            + "01000000"        // has-data true
            + "2A00000000000000" // token 42
            + "00000000"        // empty card vector
            + "0000000000000000" // the i64 between the vectors
            + "00000000"        // empty category vector
            + "00000000"        // empty counted i32 vector
            + "00000000",       // trailing main category 0
            Convert.ToHexString(ShopWire.RepEnterShopEmpty(42L)));
    }

    [Fact]
    public void RepEnterShopPinsASmallFixedCatalog()
    {
        // Same fixture as EnterShopReplyWritesTheSameBytesAsTheWriter: one card under one main with one sub.
        var card = SampleCard(0);
        var categories = new[]
        {
            new ShopCategoryRow(1000, "Main", 2, [new ShopSubcategoryRow(1002, "Items", 1, 1, 2)]),
        };
        var payload = ShopWire.RepEnterShop(123L, [card], categories, 1000);
        Assert.Equal(200, payload.Length);
        Assert.Equal(
            "01000000"                // result 1
            + "01000000"              // has-data true
            + "7B00000000000000"      // token 123
            + "01000000"              // one card
            + "01093D00"              // card id 4000001 (0x3D0901)
            + "01000000"              // kind 1: StockSet
            + "05000000"              // name character count 5
            + "43006100720064003000"  // "Card0" UTF-16LE
            + "00000000"              // display order 0
            + "64000000"              // main category 100
            + "65000000"              // subcategory 101
            + "01000000"              // on sale
            + "0000000000000000"      // unknown_56
            + "0000000000000000"      // unknown_64
            + "01093D00"              // inline stock: the set's own identity 4000001
            + "01093D00"              // stock item id 4000001, the set laid over its first stock
            + "01000000"              // stock on sale
            + "0900"                  // stock type 9 (u16)
            + "FFFFFFFF"              // duration -1
            + "B0040000"              // show price 1200
            + "E8030000"              // sell price 1000
            + "01000000"              // one purchase term
            + "01093D00" + "01093D00" + "01000000" + "0900" + "FFFFFFFF" + "B0040000" + "E8030000" // the term again
            + "0000000000000000"      // the i64 between the vectors
            + "01000000"              // one main category
            + "E8030000"              // main id 1000
            + "04000000"              // "Main" character count 4
            + "4D00610069006E00"      // "Main" UTF-16LE
            + "02000000"              // display order 2
            + "01000000"              // one subcategory
            + "EA030000"              // sub id 1002
            + "05000000"              // "Items" character count 5
            + "4900740065006D007300"  // "Items" UTF-16LE
            + "01000000"              // display order 1
            + "02000000"              // page type 2
            + "00000000"              // empty counted i32 vector
            + "E8030000",             // trailing main category 1000
            Convert.ToHexString(payload));
    }

    [Fact]
    public void RepEnterTCShopEmptyPinsTwentyBytes()
    {
        Assert.Equal(
            "00000000"          // unknown_0
            + "01000000"        // has-data true
            + "6300000000000000" // token 99
            + "00000000",       // empty card vector
            Convert.ToHexString(ShopWire.RepEnterTCShopEmpty(99L)));
    }

    [Fact]
    public void RepShopBuySuccessPinsTwentyFiveBytes()
    {
        Assert.Equal(
            "01093D00"          // key 4000001
            + "01000000"        // result 1
            + "00000000"        // empty name
            + "00"              // +32 byte 0
            + "00000000"        // +36
            + "00000000"        // +40
            + "00000000",       // +44
            Convert.ToHexString(ShopWire.RepShopBuy(4000001, 1, string.Empty)));
    }

    [Fact]
    public void RepRealCouponUseRefusalPinsTwelveBytes()
    {
        Assert.Equal(
            "FFFFFFFF" + "00000000" + "00000000",
            Convert.ToHexString(ShopWire.RepRealCouponUse(-1)));
    }

    [Fact]
    public void RepCheckCashPinsTwelveBytes()
    {
        Assert.Equal(
            "00000000" + "88130000" + "00000000",
            Convert.ToHexString(ShopWire.RepCheckCash(0, 5000, 0)));
    }

    [Fact]
    public void RepShopItemCardInfoRefusedPinsSeventyEightBytes()
    {
        // result -1 plus the 74-byte default card; the default stock keeps its u16 at 1.
        var payload = ShopWire.RepShopItemCardInfo(-1, null);
        Assert.Equal(78, payload.Length);
        Assert.Equal(
            "FFFFFFFF"               // result -1
            + "00000000"             // card id 0
            + "00000000"             // kind 0
            + "00000000"             // empty name
            + "00000000"             // display order 0
            + "00000000"             // main 0
            + "00000000"             // sub 0
            + "00000000"             // not on sale
            + "0000000000000000"     // unknown_56
            + "0000000000000000"     // unknown_64
            + "00000000" + "00000000" + "00000000" // stock ids and flag
            + "0100"                 // default stock u16 1 (kr-1256 sub_11400D0)
            + "00000000" + "00000000" + "00000000" // duration and prices
            + "00000000",            // empty extra-stock vector
            Convert.ToHexString(payload));
    }

    private static ShopCardRow SampleCard(int index) => new(
        StockSetId: 4000001 + index,
        Name: "Card" + index.ToString(CultureInfo.InvariantCulture),
        MoneyType: "lucci",
        IsOnSale: true,
        Refundable: false,
        ShowPrice: 1200,
        SellPrice: 1000,
        SellPriceCash: 0,
        MainCategory: "Clothes",
        SubCategory: "Tops",
        PrimaryItemId: 0,
        Stocks: [new ShopStockRow(101 + index, 501 + index, true, 9, -1, 1200, 1000, 0, 0)],
        MainCategoryId: 100,
        SubCategoryId: 101,
        DisplayOrder: index);
}
