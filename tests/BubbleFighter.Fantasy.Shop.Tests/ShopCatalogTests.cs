using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopCatalogTests
{
    private static WorkbookFile FixturePath =>
        ShopWorkbooks.ShopItem2;

    [Fact]
    public void LoadFixturePinsTwoOnSaleSetsAndDropsOffSale()
    {
        var catalog = ShopCatalogLoader.Load(FixturePath, 1_000_000);
        Assert.Equal(2, catalog.Cards.Count);
        Assert.Single(catalog.ItemShopCards);
        Assert.Single(catalog.CashShopCards);
        Assert.Equal(2, catalog.Categories.Count);
        Assert.NotNull(catalog.Find(2000001));
        Assert.NotNull(catalog.Find(100001));
        Assert.Null(catalog.FindPurchase(1000001));
        Assert.Null(catalog.Find(2000999));
    }

    [Fact]
    public void EmptyWorkbookFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ShopCatalogLoader.Load(ShopWorkbooks.Empty(), 1_000_000));
        Assert.Contains("is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedFileFailsLoud()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ShopCatalogLoader.Load(FixturePath, 32));
        Assert.Contains("max_shop_item_bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterShopCatalogIsLargerThanEmptyForm()
    {
        var catalog = ShopCatalogLoader.Load(FixturePath, 1_000_000);
        var payload = ShopWire.RepEnterShop(0, catalog.ItemShopCards.ToList(), catalog.Categories, 500);
        Assert.True(payload.Length > ShopWire.RepEnterShopEmptyBytes);
        var r = new PacketReader(payload);
        Assert.Equal(1, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        // sub_1140350 reads exactly two i32 before the wstring, at +4 and +8,
        // then the wstring at +12. A third i32 here used to shift every
        // following field by four bytes and crash the client.
        Assert.Equal("Round sneakers", r.ReadWideStringPrefixed());
        // 0x651A80 sorts the display-order field independently of the stock price.
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(10, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(120, r.ReadInt32());
        Assert.Equal(120, r.ReadInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(120, r.ReadInt32());
        Assert.Equal(120, r.ReadInt32());
        // The i64 between the card vector and the category vector, read at
        // sub_99B9A0 into a1+32.
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(2u, r.ReadUInt32());
    }

    [Fact]
    public void EnterTCShopCatalogPinsCashCard()
    {
        var catalog = ShopCatalogLoader.Load(FixturePath, 1_000_000);
        var payload = ShopWire.RepEnterTCShop(7, catalog.CashShopCards.ToList());
        Assert.True(payload.Length > ShopWire.RepEnterTCShopEmptyBytes);
        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(7L, r.ReadInt64());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(4000001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal("Cash spray", r.ReadWideStringPrefixed());
    }

    [Fact]
    public void CardInfoSuccessPinsResultOneAndName()
    {
        var catalog = ShopCatalogLoader.Load(FixturePath, 1_000_000);
        var card = catalog.Find(100001);
        Assert.NotNull(card);
        var payload = ShopWire.RepShopItemCardInfo(1, card);
        Assert.True(payload.Length > ShopWire.RepShopItemCardInfoRefusedBytes);
        var r = new PacketReader(payload);
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(2000001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal("Round sneakers", r.ReadWideStringPrefixed());
    }

    [Fact]
    public void BuySuccessPinsResultOneAndName()
    {
        var payload = ShopWire.RepShopBuy(100001, 1, "Round sneakers");
        Assert.True(payload.Length > ShopWire.RepShopBuyRefusedBytes);
        var r = new PacketReader(payload);
        Assert.Equal(100001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal("Round sneakers", r.ReadWideStringPrefixed());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void CheckCashPinsConfiguredBalance()
    {
        var payload = ShopWire.RepCheckCash(0, 4500, 0);
        Assert.Equal(12, payload.Length);
        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(4500, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void ItemCardWithTwoStocksPinsExtraStockVector()
    {
        var first = new ShopStockRow(3000001, 200001, true, 1, 1, 4500, 0, 4500, 0);
        var second = new ShopStockRow(3000002, 200002, false, 9, 7, 10, 20, 30, 40);
        var card = new ShopCardRow(
            0, "Two stocks", "cash", true, false, 10, 20, 30, "Accessories", "Spray", 200001,
            [first, second], 200, 205);

        var payload = WireLengthSupport.Written(w => ShopWire.WriteItemCard(w, card));
        Assert.Equal(146, payload.Length);

        var r = new PacketReader(payload);
        Assert.Equal(200001, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal("Two stocks", r.ReadWideStringPrefixed());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(200, r.ReadInt32());
        Assert.Equal(205, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(0L, r.ReadInt64());
        Assert.Equal(3000001, r.ReadInt32());
        Assert.Equal(200001, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(4500, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(3000001, r.ReadInt32());
        Assert.Equal(200001, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(4500, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(3000002, r.ReadInt32());
        Assert.Equal(200002, r.ReadInt32());
        Assert.False(r.ReadBool32());
        Assert.Equal((ushort)9, r.ReadUInt16());
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(10, r.ReadInt32());
        Assert.Equal(20, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void CategoryPlaceholderRowsAreSkippedAndSubsLinkByName()
    {
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001),
            MainCategoryRows(withPlaceholder: true),
            SubCategoryRows(withPlaceholder: true));
        try
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000);
            var card = Assert.Single(catalog.Cards);
            Assert.Equal(2000001, card.StockSetId);
            Assert.Equal(100, card.MainCategoryId);
            Assert.Equal(102, card.SubCategoryId);
            var category = Assert.Single(catalog.Categories);
            Assert.Equal(100, category.Id);
            Assert.Equal("Main display", category.Name);
            var sub = Assert.Single(category.Subcategories);
            Assert.Equal(102, sub.Id);
            Assert.Equal("Sub display", sub.Name);
            Assert.Equal(1, sub.ShopView);
            Assert.Equal(0, sub.PageType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CategoryWireUsesPageTypeInsteadOfVisibility()
    {
        var catalog = ShopCatalogLoader.Load(FixturePath, 1_000_000);
        var payload = ShopWire.RepEnterShop(0, [], catalog.Categories, 1);
        var r = new PacketReader(payload);
        r.ReadInt32();
        r.ReadBool32();
        r.ReadInt64();
        Assert.Equal(0u, r.ReadUInt32());
        r.ReadInt64();
        Assert.Equal(2u, r.ReadUInt32());
        foreach (var category in catalog.Categories)
        {
            Assert.Equal(category.Id, r.ReadInt32());
            Assert.Equal(category.Name, r.ReadWideStringPrefixed());
            Assert.Equal(category.DisplayOrder, r.ReadInt32());
            Assert.Equal(1u, r.ReadUInt32());
            var sub = Assert.Single(category.Subcategories);
            Assert.Equal(sub.Id, r.ReadInt32());
            Assert.Equal(sub.Name, r.ReadWideStringPrefixed());
            Assert.Equal(sub.DisplayOrder, r.ReadInt32());
            Assert.Equal(1, sub.ShopView);
            Assert.Equal(sub.Id == 10 ? 0 : 2, r.ReadInt32());
        }
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData("Missing", "Shoes", "MainCategory")]
    [InlineData("Clothes", "Missing", "SubCategory")]
    public void DanglingCategoryReferenceFailsLoud(string main, string sub, string sheet)
    {
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            OnSaleSetRow(2000001, main, sub, 1000001),
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains($"references missing {sheet} 'Missing'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedPageTypeFailsLoud()
    {
        var subs = SubCategoryRows(withPlaceholder: false)
            .Replace(">0</Data>", ">3</Data>", StringComparison.Ordinal);
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001),
            MainCategoryRows(withPlaceholder: false), subs);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("unsupported pageType 3", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateCategoryNamesResolveToFirstSheetRow()
    {
        var subs = SubCategoryRows(withPlaceholder: false);
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001),
            MainCategoryRows(withPlaceholder: false),
            subs + subs.Replace(">102</Data>", ">103</Data>", StringComparison.Ordinal));
        try
        {
            var card = Assert.Single(ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000).Cards);
            Assert.Equal(102, card.SubCategoryId);
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
            StockRow(1000001, 100001),
            "   <Row>\n"
            + "    <Cell><Data ss:Type=\"Number\">2000001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">Broken set</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">lucci</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">Clothes</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">Shoes</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">9999999</Data></Cell>\n"
            + "   </Row>\n",
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("references missing stockId 9999999", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateOnSaleStockSetIdFailsLoud()
    {
        var row = OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001);
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            row + row,
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("duplicate on-sale stockSetId 2000001", ex.Message, StringComparison.Ordinal);
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
            StockRow(1000001, 100001) + StockRow(1000001, 100002),
            OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001),
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("duplicate stockId 1000001", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CellNoteRichTextDoesNotBreakValueParsing()
    {
        // Real-workbook regression: a Cell can carry a note (<Comment> with rich-text
        // <ss:Data>) after the value. The note's Data must not be read as the value.
        var stockRow =
            "   <Row>\n"
            + "    <Cell><Data ss:Type=\"Number\">1000001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">100001</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell ss:StyleID=\"s134\"><Data ss:Type=\"Number\">120</Data><Comment\n"
            + "      ss:Author=\"note author\"><ss:Data xmlns=\"http://www.w3.org/TR/REC-html40\"><B><Font>note"
            + " text</Font></B></ss:Data></Comment></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
            + "   </Row>\n";
        var path = WriteTempWorkbook(
            stockRow,
            OnSaleSetRow(2000001, "Clothes", "Shoes", 1000001),
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000);
            var card = Assert.Single(catalog.Cards);
            var stock = Assert.Single(card.Stocks);
            Assert.Equal(120, stock.SellPrice);
            Assert.Equal(1000001, stock.StockId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RichTextCellValuePinsConcatenatedText()
    {
        // Real-workbook regression: one StockSet cell carries its value as rich-text
        // <ss:Data> with child runs; the cell value is the concatenated text.
        var setRow =
            "   <Row>\n"
            + "    <Cell><Data ss:Type=\"Number\">2000001</Data></Cell>\n"
            + "    <Cell ss:StyleID=\"s233\"><ss:Data ss:Type=\"String\"\n"
            + "      xmlns=\"http://www.w3.org/TR/REC-html40\">Old<I> </I><Font>TV set</Font></ss:Data><NamedCell\n"
            + "      ss:Name=\"_FilterDatabase\"/></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">lucci</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">Clothes</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"String\">Shoes</Data></Cell>\n"
            + "    <Cell><Data ss:Type=\"Number\">1000001</Data></Cell>\n"
            + "   </Row>\n";
        var path = WriteTempWorkbook(
            StockRow(1000001, 100001),
            setRow,
            MainCategoryRows(withPlaceholder: false),
            SubCategoryRows(withPlaceholder: false));
        try
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000);
            var card = Assert.Single(catalog.Cards);
            Assert.Equal("OldTV set", card.Name);
            // The whitespace-only <I> run is dropped by the reader's whitespace
            // skipping; only cells in loader-consumed columns matter, and those are
            // plain text or numbers in the real workbook.
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
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "   </Row>\n";

    private static string OnSaleSetRow(int setId, string main, string sub, int stockId) =>
        "   <Row>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{setId}</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Test set</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">120</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">lucci</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"String\">{main}</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"String\">{sub}</Data></Cell>\n"
        + $"    <Cell><Data ss:Type=\"Number\">{stockId}</Data></Cell>\n"
        + "   </Row>\n";

    private static string MainCategoryRows(bool withPlaceholder) =>
        (withPlaceholder
            ? "   <Row>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"String\">note text, not a number</Data></Cell>\n"
              + "   </Row>\n"
            : string.Empty)
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">100</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Clothes</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Main display</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "   </Row>\n";

    private static string SubCategoryRows(bool withPlaceholder) =>
        (withPlaceholder
            ? "   <Row>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
              + "   </Row>\n"
            : string.Empty)
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">102</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Shoes</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Sub display</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">2</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">1</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"Number\">0</Data></Cell>\n"
        + "   </Row>\n";

    private static string WriteTempWorkbook(string stockRows, string setRows, string mainRows, string subRows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shop2_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Workbook(stockRows, setRows, mainRows, subRows));
        return path;
    }

    private static string Workbook(string stockRows, string setRows, string mainRows, string subRows) =>
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
        + "    <Cell><Data ss:Type=\"String\">sellPriceCash</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">secondCash</Data></Cell>\n"
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
        + "    <Cell><Data ss:Type=\"String\">sellPriceCash</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">moneyType</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">refundable</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">mainCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">subCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">stockId01</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayOrder</Data></Cell>\n"
        + "   </Row>\n"
        + setRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"ShopItem\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">itemId</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">itemName</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">isOnSale</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">moneyType</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">refundable</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">mainCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">subCategory</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayOrder</Data></Cell>\n"
        + "   </Row>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"Number\">100001</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">Test item</Data></Cell>\n"
        + "   </Row>\n"
        + "  </Table>\n"
        + " </Worksheet>\n"
        + " <Worksheet ss:Name=\"MainCategory\">\n"
        + "  <Table>\n"
        + "   <Row>\n"
        + "    <Cell><Data ss:Type=\"String\">id</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">name</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayName</Data></Cell>\n"
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
        + "    <Cell><Data ss:Type=\"String\">displayName</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">displayOrder</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">shopView</Data></Cell>\n"
        + "    <Cell><Data ss:Type=\"String\">pageType</Data></Cell>\n"
        + "   </Row>\n"
        + subRows
        + "  </Table>\n"
        + " </Worksheet>\n"
        + "</Workbook>\n";
}
