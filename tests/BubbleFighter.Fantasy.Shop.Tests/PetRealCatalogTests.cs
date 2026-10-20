using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Loads the real Config/ShopItemPet.xml once for all tests in this class, read from the build's own client-data
/// archives under the repository's client-data junction. Missing data fails the fixture; public validation
/// explicitly excludes this class using its LocalClientData category.
/// </summary>
public sealed class RealPetCatalogFixture : IDisposable
{
    /// <summary>Loads the required local workbook, failing when the client-data tree is absent.</summary>
    public RealPetCatalogFixture()
    {
        var bytes = LocalClientData.Bytes("kr-1256", "Config/ShopItemPet.xml");
        Catalog = PetCatalogLoader.Load(WorkbookFile.FromBytes("Config/ShopItemPet.xml", bytes), 134_217_728);
    }

    /// <summary>Gets the locally loaded catalog used to pin workbook and wire behavior.</summary>
    public PetCatalog? Catalog { get; }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// Pins the pet catalog against the real 1256 ConfigSh pet overlay workbook. The
/// expected values below were read directly from that file: 2177 Stock rows, 618
/// StockSet rows of which 79 are on sale (22 non-cash, 57 cash), 19 MainCategory
/// rows (9 real, 8 shop-visible, 6 used by on-sale sets), 36 SubCategory rows (35
/// real).
/// </summary>
[Trait("Category", "LocalClientData")]
public sealed class PetRealCatalogTests : IClassFixture<RealPetCatalogFixture>
{
    private readonly RealPetCatalogFixture _fixture;

    /// <summary>Initializes the test class with the shared fixture.</summary>
    /// <param name="fixture">The shared real-workbook fixture.</param>
    public PetRealCatalogTests(RealPetCatalogFixture fixture) => _fixture = fixture;

    private PetCatalog? Catalog => _fixture.Catalog;

    [Fact]
    public void RealCatalogPinsOnSaleCountsAndKnownRows()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);

        Assert.Equal(79, catalog.Cards.Count);
        Assert.Equal(22, catalog.ItemShopCards.Count());
        Assert.Equal(57, catalog.CashShopCards.Count());

        // StockSet 2000001: first on-sale cash row in sheet order.
        var spray = catalog.Find(2000001);
        Assert.NotNull(spray);
        Assert.Equal("\uB7EC\uBE14\uB9AC \uD551\uD06C \uC2A4\uD504\uB808\uC774", spray.Name);
        Assert.Equal("cash", spray.MoneyType);
        Assert.True(spray.Refundable);
        Assert.Equal(4500, spray.ShowPrice);
        Assert.Equal(4500, spray.SellPrice);
        Assert.Equal(30000, spray.Lucci);
        Assert.Equal("Accessories", spray.MainCategory);
        Assert.Equal("Spray", spray.SubCategory);
        Assert.Equal(100081, spray.PrimaryItemId);
        var sprayStock = Assert.Single(spray.Stocks);
        Assert.Equal(1000239, sprayStock.StockId);
        Assert.Equal(100081, sprayStock.ItemId);
        Assert.True(sprayStock.IsOnSale);
        Assert.Equal((ushort)1, sprayStock.Type);
        Assert.Equal(4500, sprayStock.ShowPrice);
        Assert.Equal(4500, sprayStock.SellPrice);
        Assert.Equal("\uB7EC\uBE14\uB9AC \uD551\uD06C \uC2A4\uD504\uB808\uC774", sprayStock.ProductName);

        // StockSet 2000008: first on-sale lucci row in sheet order, four attached stocks.
        var set = catalog.Find(2000008);
        Assert.NotNull(set);
        Assert.Equal("\uBC84\uD30C\uC5EC\uACE0 \uAD50\uBCF5 \uC138\uD2B8 (7\uC77C)", set.Name);
        Assert.Equal("lucci", set.MoneyType);
        Assert.True(set.Refundable);
        Assert.Equal(4300, set.ShowPrice);
        Assert.Equal(4300, set.SellPrice);
        Assert.Equal(0, set.Lucci);
        Assert.Equal("Clothes", set.MainCategory);
        Assert.Equal("SetClothes", set.SubCategory);
        Assert.Equal(100035, set.PrimaryItemId);
        Assert.Equal(4, set.Stocks.Count);
        Assert.Equal(1000053, set.Stocks[0].StockId);
        Assert.Equal(100035, set.Stocks[0].ItemId);
        Assert.True(set.Stocks[0].IsOnSale);
        Assert.Equal((ushort)1, set.Stocks[0].Type);
        Assert.Equal(7, set.Stocks[0].Duration);
        Assert.Equal(2600, set.Stocks[0].ShowPrice);
        Assert.Equal(2600, set.Stocks[0].SellPrice);
        Assert.Equal("\uBC84\uD30C\uC5EC\uACE0 \uD2B8\uC708\uD14C\uC77C \uD5E4\uC5B4", set.Stocks[0].ProductName);
        Assert.Equal(1000354, set.Stocks[1].StockId);
        Assert.Equal(1000357, set.Stocks[2].StockId);
        Assert.Equal(1000360, set.Stocks[3].StockId);

        // Item-id and stock-id lookups land on the owning set; unknown keys miss.
        Assert.Equal(2000001, catalog.Find(100081)?.StockSetId);
        Assert.Equal(2000001, catalog.Find(1000239)?.StockSetId);
        Assert.Equal(2000008, catalog.Find(100035)?.StockSetId);
        Assert.Equal(2000008, catalog.Find(1000360)?.StockSetId);
        Assert.Null(catalog.Find(-1));
    }

    [Fact]
    public void RealCatalogPinsCategoryLinkage()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);

        // MainCategory sheet order, restricted to shop-visible mains used by on-sale sets.
        Assert.Equal([100, 200, 300, 400, 500, 1000], catalog.Categories.Select(c => c.Id).ToArray());

        var clothes = Assert.Single(catalog.Categories, c => c.Id == 100);
        Assert.Equal("\uC758 \uC0C1", clothes.Name);
        Assert.Equal(1004, clothes.DisplayOrder);
        var setClothes = Assert.Single(clothes.Subcategories);
        Assert.Equal(107, setClothes.Id);
        Assert.Equal("\uC138\uD2B8\uC758\uC0C1", setClothes.Name);
        Assert.Equal(1101, setClothes.DisplayOrder);
        Assert.Equal(1, setClothes.ShopView);

        var accessories = Assert.Single(catalog.Categories, c => c.Id == 200);
        Assert.Equal("\uC7A5 \uC2DD", accessories.Name);
        Assert.Equal(1005, accessories.DisplayOrder);
        // Subcategories follow the card order of the referencing sets.
        Assert.Equal([206, 202], accessories.Subcategories.Select(s => s.Id).ToArray());
        Assert.Equal("\uC2A4\uD504\uB808\uC774", accessories.Subcategories[0].Name);
        Assert.Equal("\uBA38\uB9AC\uC7A5\uC2DD", accessories.Subcategories[1].Name);

        var weapons = Assert.Single(catalog.Categories, c => c.Id == 300);
        Assert.Equal("\uBB34 \uAE30", weapons.Name);
        Assert.Equal(1002, weapons.DisplayOrder);
        Assert.Equal([306, 302, 303, 304, 305], weapons.Subcategories.Select(s => s.Id).ToArray());
        Assert.Equal("\uADFC\uC811\uBB34\uAE30", weapons.Subcategories[0].Name);
        Assert.Equal("\uBCF4\uC870\uBB34\uAE30", weapons.Subcategories[4].Name);

        // The Characters main is only referenced through its shopView=0 sub, which
        // is not emitted, so its subcategory list stays empty.
        var characters = Assert.Single(catalog.Categories, c => c.Id == 400);
        Assert.Equal("\uCE90\uB9AD\uD130", characters.Name);
        Assert.Equal(1003, characters.DisplayOrder);
        Assert.Empty(characters.Subcategories);

        var etc = Assert.Single(catalog.Categories, c => c.Id == 500);
        Assert.Equal("\uAE30 \uD0C0", etc.Name);
        Assert.Equal(1006, etc.DisplayOrder);
        Assert.Equal([501, 503], etc.Subcategories.Select(s => s.Id).ToArray());
        Assert.Equal("\uC804\uCCB4_\uAE30\uD0C0", etc.Subcategories[0].Name);
        Assert.Equal("\uCEE4\uD50C", etc.Subcategories[1].Name);

        var main = Assert.Single(catalog.Categories, c => c.Id == 1000);
        Assert.Equal("\uBA54 \uC778", main.Name);
        Assert.Equal(1000, main.DisplayOrder);
        var packages = Assert.Single(main.Subcategories);
        Assert.Equal(1003, packages.Id);
        Assert.Equal("\uD328\uD0A4\uC9C0", packages.Name);
        Assert.Equal(1003, packages.DisplayOrder);
    }

    [Fact]
    public void RealCatalogStockTypesAreKeptVerbatim()
    {
        var catalog = Catalog;
        Assert.NotNull(catalog);

        // The pet workbook carries Stock.type values 1 and 9; both must survive the
        // load without remapping.
        var types = catalog.Cards
            .SelectMany(c => c.Stocks)
            .Select(s => (int)s.Type)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();
        Assert.Equal([1, 9], types);

        // moneyType stays the verbatim sheet enum with only the two known values.
        var moneyTypes = catalog.Cards.Select(c => c.MoneyType).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal(["cash", "lucci"], moneyTypes);
    }
}
