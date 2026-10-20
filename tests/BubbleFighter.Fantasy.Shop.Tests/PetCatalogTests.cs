using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class PetCatalogTests
{
    private static WorkbookFile FixturePath =>
        ShopWorkbooks.ShopItemPet;

    [Fact]
    public void LoadFixturePinsTwoOnSaleSetsAndDropsOffSale()
    {
        var catalog = PetCatalogLoader.Load(FixturePath, 1_000_000);
        Assert.Equal(2, catalog.Cards.Count);
        Assert.Single(catalog.ItemShopCards);
        Assert.Single(catalog.CashShopCards);
        Assert.Equal(2, catalog.Categories.Count);
        Assert.NotNull(catalog.Find(4000001));
        Assert.NotNull(catalog.Find(3000001));
        Assert.NotNull(catalog.Find(200001));
        Assert.Null(catalog.Find(4000999));
        Assert.Null(catalog.Find(-1));
    }

    [Fact]
    public void KoreanDisplayStringsAreKeptVerbatim()
    {
        var catalog = PetCatalogLoader.Load(FixturePath, 1_000_000);
        var lucciCard = catalog.Find(4000001);
        Assert.NotNull(lucciCard);
        Assert.Equal("\uD3AB \uC138\uD2B8", lucciCard.Name);
        Assert.Equal(500, lucciCard.Lucci);
        var stock = Assert.Single(lucciCard.Stocks);
        Assert.Equal("\uD3AB \uC2A4\uB2C8\uCEE4\uC988", stock.ProductName);
        var clothes = Assert.Single(catalog.Categories, c => c.Id == 1);
        Assert.Equal("\uC758 \uC0C1", clothes.Name);
        var sub = Assert.Single(clothes.Subcategories);
        Assert.Equal("\uC2E0\uBC1C", sub.Name);
    }

    [Fact]
    public void SheetValuesAreCarriedWithoutRemapping()
    {
        var catalog = PetCatalogLoader.Load(FixturePath, 1_000_000);
        var lucciCard = catalog.Find(4000001);
        Assert.NotNull(lucciCard);
        Assert.Equal("lucci", lucciCard.MoneyType);
        Assert.True(lucciCard.Refundable);
        Assert.Equal(120, lucciCard.ShowPrice);
        Assert.Equal(120, lucciCard.SellPrice);
        var onSaleStock = Assert.Single(lucciCard.Stocks);
        Assert.Equal((ushort)1, onSaleStock.Type);
        Assert.True(onSaleStock.IsOnSale);
        var cashCard = catalog.Find(4000002);
        Assert.NotNull(cashCard);
        Assert.Equal("cash", cashCard.MoneyType);
        Assert.False(cashCard.Refundable);
        // Stock.type 9 is a real pet workbook value and must not be remapped.
        var offStock = Assert.Single(cashCard.Stocks);
        Assert.Equal((ushort)9, offStock.Type);
        Assert.False(offStock.IsOnSale);
        Assert.Equal(7, offStock.Duration);
        Assert.Equal(10, offStock.ShowPrice);
        Assert.Equal(20, offStock.SellPrice);
    }

    [Fact]
    public void CategoryPlaceholderRowsAreSkippedAndSubsLinkByName()
    {
        var catalog = PetCatalogLoader.Load(FixturePath, 1_000_000);
        Assert.Equal([1, 2], catalog.Categories.Select(c => c.Id).ToArray());
        var clothes = Assert.Single(catalog.Categories, c => c.Id == 1);
        Assert.Equal(10, clothes.DisplayOrder);
        var shoes = Assert.Single(clothes.Subcategories);
        Assert.Equal(10, shoes.Id);
        Assert.Equal("\uC2E0\uBC1C", shoes.Name);
        Assert.Equal(1, shoes.ShopView);
        var accessories = Assert.Single(catalog.Categories, c => c.Id == 2);
        var spray = Assert.Single(accessories.Subcategories);
        Assert.Equal(11, spray.Id);
        Assert.Equal("\uC2A4\uD504\uB808\uC774", spray.Name);
    }

    [Fact]
    public void EmptyWorkbookFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PetCatalogLoader.Load(ShopWorkbooks.Empty(), 1_000_000));
        Assert.Contains("is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedFileFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PetCatalogLoader.Load(FixturePath, 32));
        Assert.Contains("max_shop_item_bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCatalogFailsLoud()
    {
        var path = WriteTempWorkbook(StockRows(), SetRows(), MainRows(), SubRows(), ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("Pet catalog", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Stock sheet has no data rows", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateStockIdFailsLoud()
    {
        var path = WriteTempWorkbook(
            StockRow(3000001, 200001) + StockRow(3000001, 200002),
            SetRow(4000001, "Clothes", "Shoes", 3000001),
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("duplicate stockId 3000001", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateOnSaleStockSetIdFailsLoud()
    {
        var row = SetRow(4000001, "Clothes", "Shoes", 3000001);
        var path = WriteTempWorkbook(
            StockRow(3000001, 200001),
            row + row,
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("duplicate on-sale stockSetId 4000001", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DanglingStockReferenceFailsLoud()
    {
        var path = WriteTempWorkbook(
            StockRow(3000001, 200001),
            SetRow(4000001, "Clothes", "Shoes", 9999999),
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("references missing stockId 9999999", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DanglingSubCategoryReferenceFailsLoud()
    {
        var path = WriteTempWorkbook(
            StockRow(3000001, 200001),
            SetRow(4000001, "Clothes", "Nowhere", 3000001),
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains(
                "references missing subCategory 'Nowhere'",
                ex.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DanglingMainCategoryReferenceFailsLoud()
    {
        var path = WriteTempWorkbook(
            StockRow(3000001, 200001),
            SetRow(4000001, "Nowhere", "Shoes", 3000001),
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains(
                "references missing mainCategory 'Nowhere'",
                ex.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StockTypeOutsideU16FailsLoud()
    {
        var stockRow =
            "   <Row>\n"
            + "    <Cell><Data ss:Type=\"Number\">3000001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">200001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">65536</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">name</Data></Cell>\n"
            + "   </Row>\n";
        var path = WriteTempWorkbook(
            stockRow,
            SetRow(4000001, "Clothes", "Shoes", 3000001),
            MainRows(),
            SubRows(),
            ItemRows());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("Stock.type 65536 does not fit u16", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingProductNameColumnFailsLoud()
    {
        // Schema drift guard: the pet workbook names the display column with Korean
        // text; a workbook without it must fail loud instead of mis-aligning.
        var stockRow =
            "   <Row>\n"
            + "    <Cell><Data ss:Type=\"Number\">3000001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">200001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "   </Row>\n";
        var path = Path.Combine(Path.GetTempPath(), $"shoppet_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Workbook(
            stockRow,
            SetRow(4000001, "Clothes", "Shoes", 3000001),
            MainRows(),
            SubRows(),
            ItemRows()).Replace(
                "    <Cell><Data ss:Type=\"String\">\uC0C1\uD488 \uC774\uB984</Data></Cell>\n",
                string.Empty,
                StringComparison.Ordinal));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PetCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("missing column", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string StockRow(int stockId, int itemId) =>
        "   <Row>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{stockId}</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{itemId}</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">name</Data></Cell>\n"
        + "   </Row>\n";

    private static string SetRow(int setId, string main, string sub, int stockId) =>
        "   <Row>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{setId}</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Test set</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">lucci</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"String\">{main}</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"String\">{sub}</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{stockId}</Data></Cell>\n"
        + "   </Row>\n";

    private static string MainRows() =>
        "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">note text, not a number</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "   </Row>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Clothes</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Main display</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">10</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "   </Row>\n";

    private static string SubRows() =>
        "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "   </Row>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">10</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Shoes</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Sub display</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "   </Row>\n";

    private static string ItemRows() =>
        "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">200001</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Test item</Data></Cell>\n"
        + "   </Row>\n";

    private static string StockRows() => string.Empty;

    private static string SetRows() => string.Empty;

    private static string WriteTempWorkbook(
        string stockRows, string setRows, string mainRows, string subRows, string itemRows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shoppet_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Workbook(stockRows, setRows, mainRows, subRows, itemRows));
        return path;
    }

    private static string Workbook(
        string stockRows, string setRows, string mainRows, string subRows, string itemRows) =>
        "<?xml version=\"1.0\"?>\n"
        + "<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"\n"
        + " xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">\n"
        + " <Worksheet ss:Name=\"Stock\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">stockId</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">itemId</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">isOnSale</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">type</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">duration</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">showPrice</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">sellPrice</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">\uC0C1\uD488 \uC774\uB984</Data></Cell>\n"
        + "   </Row>\n"
        + stockRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"StockSet\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">stockSetId</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">stockSetName</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">isOnSale</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">showPrice</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">sellPrice</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">moneyType</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">refundable</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">mainCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">subCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">lucci</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">stockId01</Data></Cell>\n"
        + "   </Row>\n"
        + setRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"ShopItem\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">itemId</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">itemName</Data></Cell>\n"
        + "   </Row>\n"
        + itemRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"MainCategory\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">id</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">name</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">nameKr</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayOrder</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">shopView</Data></Cell>\n"
        + "   </Row>\n"
        + mainRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"SubCategory\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">id</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">name</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">nameKr</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayOrder</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">shopView</Data></Cell>\n"
        + "   </Row>\n"
        + subRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + "</Workbook>\n";
}
