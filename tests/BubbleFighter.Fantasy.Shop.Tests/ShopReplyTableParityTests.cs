using BubbleFighter.Fantasy.Contracts.Shop;
using System.Globalization;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins every kr-1256 shop reply table to the hand-written writer that is live today: the body
/// <see cref="PacketCodec.Write(PacketTables, string, string, IReadOnlyDictionary{string, object?}, int)"/>
/// produces for the table values must be the exact bytes <see cref="ShopWire"/> writes, and the
/// length of each reply is pinned. The writers and their tables therefore cannot drift apart while
/// the reply is described in <c>packets/shop.yaml</c> and the code path stays unchanged.
/// </summary>
public sealed class ShopReplyTableParityTests
{
    private static readonly PacketTables Tables = LoadTables();

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 5000, 0)]
    [InlineData(1, 123, 7)]
    public void CheckCashReplyWritesTheSameBytesAsTheWriter(int unknown0, int cash, int unknown8)
    {
        var values = new Dictionary<string, object?>
        {
            ["unknown_0"] = unknown0,
            ["cash"] = cash,
            ["unknown_8"] = unknown8,
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "CheckCashReply", values);

        Assert.Equal(12, body.Length);
        Assert.Equal(ShopWire.RepCheckCash(unknown0, cash, unknown8), body);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RealCouponUseReplyWritesTheSameBytesAsTheWriter(int result)
    {
        var values = new Dictionary<string, object?>
        {
            ["result"] = result,
            ["unknown_8"] = 0,
            ["unknown_12"] = 0,
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "RealCouponUseReply", values);

        Assert.Equal(12, body.Length);
        Assert.Equal(ShopWire.RepRealCouponUse(result), body);
    }

    [Theory]
    [InlineData(4000001, 1, "")]
    [InlineData(12345, 0, "")]
    [InlineData(7, 0, "abc")]
    public void ShopPurchaseReplyForKr1256WritesTheSameBytesAsTheWriter(int key, int result, string name)
    {
        var values = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["result"] = result,
            ["unknown_8"] = name,
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "ShopPurchaseReply", values);

        // 4 key + 4 result + 4 string count + 2 per character + 1 byte + three 4-byte words.
        Assert.Equal(25 + (2 * name.Length), body.Length);
        Assert.Equal(ShopWire.RepShopBuy(key, result, name), body);
    }

    [Fact]
    public void ShopItemCardInfoRefusedReplyWritesTheSameBytesAsTheWriter()
    {
        var values = new Dictionary<string, object?>
        {
            ["result"] = -1,
            ["stock"] = EmptyStock(),
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "ShopItemCardInfoReply", values);

        // 4 result + the 74-byte default card.
        Assert.Equal(78, body.Length);
        Assert.Equal(ShopWire.RepShopItemCardInfo(-1, null), body);
    }

    [Fact]
    public void ShopItemCardInfoReplyWritesTheSameBytesAsTheWriter()
    {
        var card = SampleCard(0);
        var values = new Dictionary<string, object?>
        {
            ["result"] = 1,
            ["card"] = CardValues(card),
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "ShopItemCardInfoReply", values);

        // 4 result + 74-byte card skeleton + 2 per name character + 26 per stock.
        Assert.Equal(78 + (2 * card.Name.Length) + (26 * card.Stocks.Count), body.Length);
        Assert.Equal(ShopWire.RepShopItemCardInfo(1, card), body);
    }

    [Fact]
    public void CashShopReplyEmptyWritesTheSameBytesAsTheEmptyPin()
    {
        var values = new Dictionary<string, object?>
        {
            ["unknown_0"] = 0,
            ["has_data"] = true,
            ["token"] = 99L,
            ["cards"] = new List<Dictionary<string, object?>>(),
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "CashShopReply", values);

        // 4 unknown_0 + 4 has-data + 8 token + 4 empty card count.
        Assert.Equal(20, body.Length);
        Assert.Equal(ShopWire.RepEnterTCShopEmpty(99L), body);
    }

    [Fact]
    public void CashShopReplyWritesTheSameBytesAsTheWriter()
    {
        var cards = Enumerable.Range(0, 3).Select(SampleCard).ToList();
        var values = new Dictionary<string, object?>
        {
            ["unknown_0"] = 0,
            ["has_data"] = true,
            ["token"] = 1234567L,
            ["cards"] = cards.Select(CardValues).ToList(),
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "CashShopReply", values);

        // 4 unknown_0 + 4 has-data + 8 token + 4 card count + 3 * (74 + 2 * 5 + 26) card bytes.
        Assert.Equal(350, body.Length);
        Assert.Equal(ShopWire.RepEnterTCShop(1234567L, cards), body);
    }

    [Fact]
    public void EnterShopReplyEmptyWritesTheSameBytesAsTheEmptyPin()
    {
        var values = new Dictionary<string, object?>
        {
            ["result"] = 1,
            ["has_data"] = true,
            ["token"] = 42L,
            ["cards"] = new List<Dictionary<string, object?>>(),
            ["unknown_32"] = 0L,
            ["categories"] = new List<Dictionary<string, object?>>(),
            ["unknown_56"] = new List<object?>(),
            ["main_category"] = 0,
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "EnterShopReply", values);

        // 4 result + 4 has-data + 8 token + 4 card count + 8 unknown_32 + 4 category count
        // + 4 unknown_56 count + 4 main category.
        Assert.Equal(40, body.Length);
        Assert.Equal(ShopWire.RepEnterShopEmpty(42L), body);
    }

    [Fact]
    public void EnterShopReplyWritesTheSameBytesAsTheWriter()
    {
        var card = SampleCard(0);
        var categories = new[]
        {
            new ShopCategoryRow(1000, "Main", 2, [new ShopSubcategoryRow(1002, "Items", 1, 1, 2)]),
        };
        var values = new Dictionary<string, object?>
        {
            ["result"] = 1,
            ["has_data"] = true,
            ["token"] = 123L,
            ["cards"] = new[] { CardValues(card) },
            ["unknown_32"] = 0L,
            ["categories"] = categories.Select(CategoryValues).ToList(),
            ["unknown_56"] = new List<object?>(),
            ["main_category"] = 1000,
        };

        var body = PacketCodec.Write(Tables, "kr-1256", "EnterShopReply", values);

        // 4 result + 4 has-data + 8 token + 4 card count + 110 card + 8 unknown_32
        // + 4 category count + 50 main category + 4 subcategory count + 10 sub name
        // + 4 unknown_56 count + 4 main category.
        Assert.Equal(200, body.Length);
        Assert.Equal(ShopWire.RepEnterShop(123L, [card], categories, 1000), body);
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

    private static Dictionary<string, object?> StockValues(ShopStockRow stock) => new()
    {
        ["unknown_4"] = stock.StockId,
        ["unknown_8"] = stock.ItemId,
        ["unknown_12"] = stock.IsOnSale,
        ["unknown_14"] = stock.Type,
        ["unknown_16"] = stock.Duration,
        ["unknown_20"] = stock.ShowPrice,
        ["unknown_24"] = stock.SellPrice,
    };

    private static Dictionary<string, object?> EmptyStock() => new() { ["unknown_14"] = (ushort)1 };

    private static Dictionary<string, object?> CardValues(ShopCardRow card)
    {
        // The writers emit the card's purchase terms, which for a StockSet card are the set's own
        // identity and prices laid over its first stock, never the raw Stocks list.
        var terms = card.PurchaseTerms;
        return new Dictionary<string, object?>
        {
            ["unknown_4"] = card.CardId,
            ["unknown_8"] = card.CardKind,
            ["unknown_12"] = card.Name,
            ["unknown_36"] = card.DisplayOrder,
            ["unknown_40"] = card.MainCategoryId,
            ["unknown_44"] = card.SubCategoryId,
            ["unknown_48"] = card.IsOnSale,
            ["stock"] = terms.Count == 0 ? EmptyStock() : StockValues(terms[0]),
            ["extra_stocks"] = terms.Select(StockValues).ToList(),
        };
    }

    private static Dictionary<string, object?> CategoryValues(ShopCategoryRow category) => new()
    {
        ["unknown_4"] = category.Id,
        ["unknown_8"] = category.Name,
        ["unknown_32"] = category.DisplayOrder,
        ["subcategories"] = category.Subcategories.Select(sub => new Dictionary<string, object?>
        {
            ["unknown_4"] = sub.Id,
            ["unknown_8"] = sub.Name,
            ["unknown_32"] = sub.DisplayOrder,
            ["page_type"] = sub.PageType,
        }).ToList(),
    };

    private static PacketTables LoadTables()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return PacketTables.Load(Path.Combine(dir.FullName, "packets"));
        throw new InvalidOperationException("Repository root not found above the test output.");
    }
}
