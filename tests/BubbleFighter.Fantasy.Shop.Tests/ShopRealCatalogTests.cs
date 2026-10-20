using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Loads the real Config/ShopItem2.xml once for all tests in this class, read from the build's own client-data
/// archives under the repository's client-data junction. Missing data fails the fixture; public validation
/// explicitly excludes this class using its LocalClientData category.
/// </summary>
public sealed class RealCatalogFixture : IDisposable
{
    /// <summary>Loads the required local workbook, failing when the client-data tree is absent.</summary>
    public RealCatalogFixture()
    {
        var bytes = LocalClientData.Bytes("kr-1256", "Config/ShopItem2.xml");
        Catalog = ShopCatalogLoader.Load(WorkbookFile.FromBytes("Config/ShopItem2.xml", bytes), 134_217_728);
    }

    /// <summary>Gets the locally loaded catalog used to pin workbook and wire behavior.</summary>
    public ShopCatalog? Catalog { get; }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Pins the 1256 workbook's resolved sale cards: 771 cards (227 item-shop and 544
/// cash-shop), including 284 stock-only cards whose StockSetId is zero. Wire tests
/// below also pin selected workbook values and their encoded representation.
/// </summary>
[Trait("Category", "LocalClientData")]
public sealed class ShopRealCatalogTests : IClassFixture<RealCatalogFixture>
{
    private readonly RealCatalogFixture _fixture;

    /// <summary>Initializes the test class with the shared fixture.</summary>
    /// <param name="fixture">The shared real-workbook fixture.</param>
    public ShopRealCatalogTests(RealCatalogFixture fixture) => _fixture = fixture;

    private ShopCatalog? Catalog => _fixture.Catalog;

    [Fact]
    public void RealCatalogPinsOnSaleCountsAndKnownRows()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);

        Assert.Equal(771, catalog.Cards.Count);
        Assert.Equal(227, catalog.ItemShopCards.Count());
        Assert.Equal(544, catalog.CashShopCards.Count());
        Assert.Equal(284, catalog.Cards.Count(c => c.StockSetId == 0));

        // StockSet 2000001: first on-sale cash row in sheet order.
        var spray = catalog.Find(2000001);
        Assert.NotNull(spray);
        Assert.Equal("\uB7EC\uBE14\uB9AC \uD551\uD06C \uC2A4\uD504\uB808\uC774", spray.Name);
        Assert.Equal("cash", spray.MoneyType);
        Assert.True(spray.IsOnSale);
        Assert.True(spray.Refundable);
        Assert.Equal(4500, spray.ShowPrice);
        Assert.Equal(4500, spray.SellPrice);
        Assert.Equal(0, spray.SellPriceCash);
        Assert.Equal("Accessories", spray.MainCategory);
        Assert.Equal("Spray", spray.SubCategory);
        Assert.Equal(200, spray.MainCategoryId);
        Assert.Equal(206, spray.SubCategoryId);
        Assert.Equal(1000, spray.DisplayOrder);
        Assert.Equal(100081, spray.PrimaryItemId);
        var sprayStock = Assert.Single(spray.Stocks);
        Assert.Equal(1000239, sprayStock.StockId);
        Assert.Equal(100081, sprayStock.ItemId);
        Assert.True(sprayStock.IsOnSale);
        Assert.Equal((ushort)1, sprayStock.Type);
        Assert.Equal(4500, sprayStock.ShowPrice);
        Assert.Equal(4500, sprayStock.SellPrice);
        Assert.Equal(0, sprayStock.SellPriceCash);

        // StockSet 2000107: first on-sale lucci row in sheet order, eight attached stocks.
        var bell = catalog.Find(2000107);
        Assert.NotNull(bell);
        Assert.Equal("X-MAS \uBA5C\uB85C\uB514\uC885 (7\uC77C)", bell.Name);
        Assert.Equal("lucci", bell.MoneyType);
        Assert.True(bell.Refundable);
        Assert.Equal(4500, bell.ShowPrice);
        Assert.Equal(4500, bell.SellPrice);
        Assert.Equal(0, bell.SellPriceCash);
        Assert.Equal("Weapons", bell.MainCategory);
        Assert.Equal("MeleeWeapon", bell.SubCategory);
        Assert.Equal(300, bell.MainCategoryId);
        Assert.Equal(306, bell.SubCategoryId);
        Assert.Equal(508625, bell.DisplayOrder);
        Assert.Equal(207029, bell.PrimaryItemId);
        Assert.Equal(8, bell.Stocks.Count);
        Assert.Equal(1000854, bell.Stocks[0].StockId);
        Assert.Equal(207029, bell.Stocks[0].ItemId);
        Assert.False(bell.Stocks[0].IsOnSale);
        Assert.Equal((ushort)1, bell.Stocks[0].Type);
        Assert.Equal(1, bell.Stocks[0].SellPrice);
        Assert.Equal(1000857, bell.Stocks[1].StockId);
        Assert.Equal(207030, bell.Stocks[1].ItemId);
        Assert.Equal((ushort)1, bell.Stocks[1].Type);
        Assert.Equal(1000875, bell.Stocks[7].StockId);

        // Item-id and stock-id lookups land on the owning set; unknown keys miss.
        Assert.Equal(2000001, catalog.Find(100081)?.StockSetId);
        Assert.Equal(2000107, catalog.Find(207029)?.StockSetId);
        Assert.Null(catalog.FindPurchase(1000854));
        Assert.Null(catalog.Find(-1));

        // Categories: shop-visible mains used by on-sale sets, sheet order.
        Assert.Equal([100, 200, 300, 400, 500, 800, 1000], catalog.Categories.Select(c => c.Id).ToArray());
        // The default selected category has to be one this catalog actually
        // carries: sub_64DAB0 returns on its very first test when the key names
        // no row, which is a tab bar over an empty page and an error nowhere, so
        // without this pin the option and the shipped data could drift apart and
        // the only symptom would be a blank shop on a live client.
        Assert.Contains(
            new ShopOptions().EnterShopMainCategory,
            catalog.Categories.Select(c => c.Id).ToArray());
        var clothes = Assert.Single(catalog.Categories, c => c.Id == 100);
        Assert.Equal("\uC758\uC0C1", clothes.Name);
        Assert.Equal(1004, clothes.DisplayOrder);
        var setClothes = Assert.Single(clothes.Subcategories, c => c.Id == 107);
        Assert.Equal(107, setClothes.Id);
        Assert.Equal(1101, setClothes.DisplayOrder);
        Assert.Equal(1, setClothes.ShopView);
        Assert.Equal(0, setClothes.PageType);
        Assert.Equal("\uC138\uD2B8\uC758\uC0C1", setClothes.Name);

        var weapons = Assert.Single(catalog.Categories, c => c.Id == 300);
        Assert.Equal("\uBB34\uAE30", weapons.Name);
        Assert.Equal(1002, weapons.DisplayOrder);
        Assert.Contains(weapons.Subcategories, s => s.Id == 306 && s.Name == "\uADFC\uC811\uBB34\uAE30" && s.DisplayOrder == 1305);

        // The Characters main only appears with the shopView=0 "Characters" sub,
        // which is not emitted, so its subcategory list stays empty.
        var characters = Assert.Single(catalog.Categories, c => c.Id == 400);
        Assert.Equal("\uCE90\uB9AD\uD130", characters.Name);
        Assert.Empty(characters.Subcategories);
    }

    [Fact]
    public void RealCatalogEnterShopWireConsumesExactly()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);
        var itemCards = catalog.ItemShopCards.ToList();

        var payload = ShopWire.RepEnterShop(0x0102030405060708L, itemCards, catalog.Categories, 500);
        var r = new PacketReader(payload);
        Assert.Equal(1, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(0x0102030405060708L, r.ReadInt64());
        Assert.Equal((uint)itemCards.Count, r.ReadUInt32());

        var first = ReadCard(ref r);
        Assert.Equal(itemCards[0].CardId, first.ItemId);
        Assert.Equal(itemCards[0].CardKind, first.StockSetId);
        Assert.Equal(itemCards[0].MainCategoryId, first.MainCategoryId);
        Assert.Equal(itemCards[0].SubCategoryId, first.SubCategoryId);
        Assert.Equal(itemCards[0].Name, first.Name);
        for (var i = 1; i < itemCards.Count; i++)
            ReadCard(ref r);

        // The i64 between the card vector and the category vector, read at
        // sub_99B9A0 into a1+32.
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal((uint)catalog.Categories.Count, r.ReadUInt32());
        foreach (var category in catalog.Categories)
        {
            Assert.Equal(category.Id, r.ReadInt32());
            Assert.Equal(category.Name, r.ReadWideStringPrefixed());
            Assert.Equal(category.DisplayOrder, r.ReadInt32());
            Assert.Equal((uint)category.Subcategories.Count, r.ReadUInt32());
            foreach (var sub in category.Subcategories)
            {
                Assert.Equal(sub.Id, r.ReadInt32());
                Assert.Equal(sub.Name, r.ReadWideStringPrefixed());
                Assert.Equal(sub.DisplayOrder, r.ReadInt32());
                Assert.Equal(sub.PageType, r.ReadInt32());
            }
        }

        // The counted i32 vector the parser builds at a1+56 (sub_99B9A0 through
        // sub_999C00, a u32 element count and that many i32), then the selected
        // main category at a1+68 (sub_99B9A0), which the reply handler hands to
        // the fill. A reply missing the count reads the category as the count and
        // leaves the selection at zero, which draws tabs and no cards.
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(500, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RealCatalogEnterTCShopWireConsumesExactly()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);
        var cashCards = catalog.CashShopCards.ToList();

        var payload = ShopWire.RepEnterTCShop(7, cashCards);
        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(7L, r.ReadInt64());
        Assert.Equal((uint)cashCards.Count, r.ReadUInt32());

        var first = ReadCard(ref r);
        Assert.Equal(cashCards[0].CardKind, first.StockSetId);
        Assert.Equal(cashCards[0].CardId, first.ItemId);
        Assert.Equal(cashCards[0].Name, first.Name);
        for (var i = 1; i < cashCards.Count; i++)
            ReadCard(ref r);

        Assert.Equal(0, r.Remaining);
    }

    private static (int ItemId, int StockSetId, string Name, int MainCategoryId, int SubCategoryId) ReadCard(ref PacketReader r)
    {
        var itemId = r.ReadInt32();
        var stockSetId = r.ReadInt32();
        // sub_1140350 reads two i32 then the wstring; no third i32 exists.
        var name = r.ReadWideStringPrefixed();
        r.ReadInt32();
        var mainCategoryId = r.ReadInt32();
        var subCategoryId = r.ReadInt32();
        r.ReadBool32();
        r.ReadInt64();
        r.ReadInt64();
        ReadStock(ref r);
        var extra = r.ReadUInt32();
        for (var i = 0; i < extra; i++)
            ReadStock(ref r);
        return (itemId, stockSetId, name, mainCategoryId, subCategoryId);
    }

    private static void ReadStock(ref PacketReader r)
    {
        r.ReadInt32();
        r.ReadInt32();
        r.ReadBool32();
        r.ReadUInt16();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
    }
}
