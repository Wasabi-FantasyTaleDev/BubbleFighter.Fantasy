using BubbleFighter.Fantasy.Tests;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Checks legacy workbook prices and localized categories without weakening modern validation.</summary>
public sealed class LegacyShopCatalogTests
{
    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    [Fact]
    public void ServerDaySaleRequiresNativeItemAndUniqueIdentityAndRespectsVersionScope()
    {
        WithLegacyWorkbook(document =>
        {
            Table(document, "Stock").Elements(Ss + "Row").ElementAt(2).Remove();
            var items = Table(document, "ShopItem");
            foreach (var column in new[] { "durationType", "maxDuration" })
                items.Elements(Ss + "Row").First().Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", column)));
            var item = items.Elements(Ss + "Row").ElementAt(2);
            foreach (var value in new[] { "1", "cash", "0", "Clothes", "Shoes", "1", "day", "360" })
                item.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", value)));
        }, path =>
        {
            var offer = new BubbleFighter.Fantasy.Shop.ShopSupplementOffer
            {
                ItemId = 200001, StockId = 1999201, DurationDays = 30, Price = 1000,
                MoneyType = "client", ClientProfiles = ["cn-324"],
            };
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null, [offer], "cn-324");
            var purchase = catalog.FindPurchase(1999201)!;
            Assert.Equal("cash", purchase.MoneyType);
            Assert.Equal(1000, purchase.Price);
            Assert.Equal(200001, Assert.Single(purchase.Stocks).ItemId);
            Assert.Equal(30, purchase.Stocks[0].Duration);
            Assert.Null(ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null, [offer], "kr-812").FindPurchase(1999201));
            offer.StockId = 1000001;
            Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null, [offer], "cn-324"));
            offer.StockId = 1999201;
            offer.DurationDays = 361;
            Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null, [offer], "cn-324"));
        });
    }

    [Fact]
    public void OptionalSupplementSkipsAnItemWithoutNativeSaleTerms()
    {
        WithLegacyWorkbook(document => Table(document, "Stock").Elements(Ss + "Row").ElementAt(2).Remove(), path =>
        {
            var diagnostics = new List<string>();
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, diagnostics.Add,
                [new() { ItemId = 200001, Price = 1000, MoneyType = "client", SkipIfMissing = true }]);
            Assert.Null(catalog.FindPurchase(3000001));
            Assert.Contains(diagnostics, message => message.Contains("item 200001", StringComparison.Ordinal) && message.Contains("skipped", StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null,
                [new() { ItemId = 200001, Price = 1000, MoneyType = "client" }]));
        });
    }

    [Fact]
    public void SupplementalSetPreservesAllGrantStocksWithoutOpeningIndividualSales()
    {
        WithLegacyWorkbook(document =>
        {
            var sets = Table(document, "StockSet");
            sets.Elements(Ss + "Row").First().Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", "stockId02")));
            var row = sets.Elements(Ss + "Row").ElementAt(2);
            row.Elements(Ss + "Cell").ElementAt(2).Element(Ss + "Data")!.Value = "0";
            row.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", "0")));
            row.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", "1000001")));
            foreach (var stock in Table(document, "Stock").Elements(Ss + "Row").Skip(1))
                stock.Elements(Ss + "Cell").ElementAt(2).Element(Ss + "Data")!.Value = "0";
        }, path =>
        {
            var diagnostics = new List<string>();
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, diagnostics.Add,
                [new() { StockSetId = 4000001, Price = 1000, MoneyType = "client" },
                 new() { StockSetId = 9999999, Price = 1000, SkipIfMissing = true },
                 new() { ItemId = 9999999, Price = 1000, SkipIfMissing = true }]);
            var purchase = catalog.FindPurchase(4000001)!;
            Assert.Equal(1000, purchase.Price);
            Assert.Equal("cash", purchase.MoneyType);
            Assert.Equal(new[] { 200001, 100001 }, purchase.Stocks.Select(stock => stock.ItemId));
            Assert.All(purchase.Stocks, stock => Assert.Equal(1, stock.Duration));
            Assert.Null(catalog.FindPurchase(3000001));
            Assert.Null(catalog.FindPurchase(1000001));
            var card = Assert.Single(catalog.Cards, card => card.StockSetId == 4000001);
            Assert.True(card.SupplementalPriority);
            Assert.Equal(1000, Assert.Single(card.PurchaseTerms).SellPrice);
            Assert.Equal(2, diagnostics.Count(message => message.Contains("skipped", StringComparison.Ordinal)));
            Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null,
                [new() { StockSetId = 4000001, Price = 1000, MoneyType = "lucci" }]));
        });
    }

    [Fact]
    public void SupplementalOfferRestoresLongestUnavailableTermWithoutChangingExistingSales()
    {
        WithLegacyWorkbook(document =>
        {
            var table = Table(document, "Stock");
            var original = table.Elements(Ss + "Row").ElementAt(1);
            original.Elements(Ss + "Cell").ElementAt(2).Element(Ss + "Data")!.Value = "0";
            var permanent = new XElement(original);
            permanent.Elements(Ss + "Cell").ElementAt(0).Element(Ss + "Data")!.Value = "1000002";
            permanent.Elements(Ss + "Cell").ElementAt(3).Element(Ss + "Data")!.Value = "9";
            permanent.Elements(Ss + "Cell").ElementAt(4).Element(Ss + "Data")!.Value = "-1";
            table.Add(permanent);
            var item = Table(document, "ShopItem").Elements(Ss + "Row").ElementAt(1);
            foreach (var value in new[] { "1", "lucci", "0", "Clothes", "Shoes", "0" })
                item.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", value)));
            var existingSale = Table(document, "ShopItem").Elements(Ss + "Row").ElementAt(2);
            foreach (var value in new[] { "1", "cash", "0", "Clothes", "Shoes", "1" })
                existingSale.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", value)));
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null,
                [new() { ItemId = 100001, Price = 1000, MoneyType = "lucci" }, new() { ItemId = 200001, Price = 1, MoneyType = "lucci" }]);
            Assert.Null(catalog.FindPurchase(1000001));
            var purchase = catalog.FindPurchase(1000002)!;
            Assert.Equal(1000, purchase.Price);
            Assert.Equal("lucci", purchase.MoneyType);
            Assert.Equal(-1, Assert.Single(purchase.Stocks).Duration);
            Assert.Equal(100001, Assert.Single(purchase.Stocks).ItemId);
            var bounded = ShopWire.SelectBoundedCards(catalog.Cards, 10000, maxCards: 1);
            Assert.Equal(100001, Assert.Single(bounded.Selected).PrimaryItemId);
            Assert.Equal(0, catalog.FindPurchase(4000001)!.Price);
            Assert.Equal(0, catalog.FindPurchase(3000001)!.Price);
            Assert.Equal("cash", catalog.FindPurchase(3000001)!.MoneyType);
            Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, null, null,
                [new() { ItemId = 100001, Price = 1000, MoneyType = "cash" }]));
        });
    }

    [Fact]
    public void LegacyWorkbookUsesSellPriceForCashAndNameKrForDisplay()
    {
        WithLegacyWorkbook(document =>
        {
            var stockRows = Table(document, "Stock").Elements(Ss + "Row").ToArray();
            stockRows[2].Elements(Ss + "Cell").ElementAt(6).Element(Ss + "Data")!.Value = "4500";
            var setRows = Table(document, "StockSet").Elements(Ss + "Row").ToArray();
            setRows[2].Elements(Ss + "Cell").ElementAt(4).Element(Ss + "Data")!.Value = "4500";
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy);
            Assert.Equal(2, catalog.Cards.Count);
            var cash = Assert.Single(catalog.CashShopCards);
            Assert.Equal(4500, cash.SellPrice);
            Assert.Equal(0, cash.SellPriceCash);
            Assert.Equal(4500, catalog.FindPurchase(4000001)!.Price);
            var granted = Assert.Single(catalog.FindPurchase(4000001)!.Stocks);
            Assert.Equal(3000001, granted.StockId);
            Assert.Equal(200001, granted.ItemId);
            Assert.Equal(1, granted.Duration);
            Assert.Equal(granted, Assert.Single(cash.Stocks));
            // A catalog term describes the set's price, never the item to grant.
            var stock = Assert.Single(cash.PurchaseTerms);
            Assert.Equal(4000001, stock.StockId);
            Assert.Equal(4000001, stock.ItemId);
            Assert.Equal(4500, stock.SellPrice);
            Assert.Equal(0, stock.SellPriceCash);
            Assert.Equal(0, stock.SecondCash);
            Assert.Equal(0, stock.Duration);
            var clothes = Assert.Single(catalog.Categories, category => category.Id == 1);
            Assert.Equal("Legacy category", clothes.Name);
            Assert.Equal("Legacy subcategory", Assert.Single(clothes.Subcategories).Name);
        });
    }

    [Fact]
    public void LegacyColumnsAreNotAcceptedByTheDefaultModernSchema()
    {
        WithLegacyWorkbook(_ => { }, path =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000));
            Assert.Contains("sellPriceCash", error.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("Stock", "sellPrice")]
    [InlineData("StockSet", "sellPrice")]
    [InlineData("MainCategory", "nameKr")]
    public void LegacySchemaStillRejectsMissingRequiredColumns(string sheet, string column)
    {
        WithLegacyWorkbook(document => RemoveColumn(Table(document, sheet), column), path =>
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy));
            Assert.Contains(column, error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void UnknownSchemaFailsBeforeReadingTheWorkbook()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ShopCatalogLoader.Load(ShopWorkbooks.Empty("not-read.xml"), 1_000_000, (ShopCatalogSchema)99));
    }

    [Fact]
    public void IdenticalLegacyStocksDoNotDuplicateOffers()
    {
        WithLegacyWorkbook(document =>
        {
            var table = Table(document, "Stock");
            table.Add(new XElement(table.Elements(Ss + "Row").ElementAt(1)));
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy);
            Assert.Equal(2, catalog.Cards.Count);
            Assert.Single(catalog.FindPurchase(2000001)!.Stocks);
        });
    }

    [Fact]
    public void ConflictingLegacyStocksRemainAnError()
    {
        WithLegacyWorkbook(document =>
        {
            var table = Table(document, "Stock");
            var row = new XElement(table.Elements(Ss + "Row").ElementAt(1));
            row.Elements(Ss + "Cell").ElementAt(6).Element(Ss + "Data")!.Value = "999";
            table.Add(row);
        }, path =>
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy));
            Assert.Contains("duplicate stockId", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MissingLegacySubCategoryResolvesToZeroWithDiagnostic()
    {
        var diagnostics = new List<string>();
        WithLegacyWorkbook(document =>
        {
            var rows = Table(document, "StockSet").Elements(Ss + "Row").ToArray();
            var header = rows[0].Elements(Ss + "Cell").ToArray();
            var index = Array.FindIndex(header, cell => cell.Element(Ss + "Data")!.Value == "subCategory");
            rows[1].Elements(Ss + "Cell").ElementAt(index).Element(Ss + "Data")!.Value = "Nope";
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, diagnostics.Add);
            var set = catalog.Cards.Single(card => card.StockSetId == 2000001);
            Assert.Equal(0, set.SubCategoryId);
            Assert.NotEqual(0, set.MainCategoryId);
            Assert.Contains(diagnostics, line => line.Contains("SubCategory name 'Nope'", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void MissingLegacyStockSlotDropsAnUnpricedSetWithDiagnostic()
    {
        var diagnostics = new List<string>();
        WithLegacyWorkbook(document =>
        {
            var rows = Table(document, "StockSet").Elements(Ss + "Row").ToArray();
            var header = rows[0].Elements(Ss + "Cell").ToArray();
            var index = Array.FindIndex(header, cell => cell.Element(Ss + "Data")!.Value == "stockId01");
            rows[2].Elements(Ss + "Cell").ElementAt(index).Element(Ss + "Data")!.Value = "9999999";
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, diagnostics.Add);
            var survivor = Assert.Single(catalog.Cards);
            Assert.Equal(2000001, survivor.StockSetId);
            Assert.Contains(diagnostics, line => line.Contains("stockId 9999999", StringComparison.Ordinal));
            Assert.Contains(diagnostics, line => line.Contains("no resolvable stock slots", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void LegacyDiagnosticsAreBoundedToDistinctPairs()
    {
        var diagnostics = new List<string>();
        WithLegacyWorkbook(document =>
        {
            var rows = Table(document, "StockSet").Elements(Ss + "Row").ToArray();
            var header = rows[0].Elements(Ss + "Cell").ToArray();
            var subIndex = Array.FindIndex(header, cell => cell.Element(Ss + "Data")!.Value == "subCategory");
            rows[1].Elements(Ss + "Cell").ElementAt(subIndex).Element(Ss + "Data")!.Value = "Nope";
            rows[2].Elements(Ss + "Cell").ElementAt(subIndex).Element(Ss + "Data")!.Value = "Nope";
        }, path =>
        {
            ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy, diagnostics.Add);
            Assert.Equal(1, diagnostics.Count(line => line.Contains("SubCategory name 'Nope'", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// Gifts cover every Stock and every resolvable StockSet, on sale or not, and read a set's Lucci from its "Lucci"
    /// column whatever its case (id-400's workbook capitalizes it); a set with an unknown stock is not a gift.
    /// </summary>
    [Fact]
    public void GiftsIncludeOffSaleStocksAndLucciSets()
    {
        static void AddRow(XElement table, params string[] values) =>
            table.Add(new XElement(Ss + "Row", values.Select(value => new XElement(Ss + "Cell", new XElement(Ss + "Data", value)))));
        WithLegacyWorkbook(document =>
        {
            AddRow(Table(document, "Stock"), "1001622", "100001", "0", "1", "200", "0", "0");
            var sets = Table(document, "StockSet");
            sets.Elements(Ss + "Row").First().Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", "Lucci")));
            AddRow(sets, "2000910", "5000 Lucci", "0", "1", "1", "lucci", "1", "Clothes", "Shoes", "0", "0", "5000");
            AddRow(sets, "2000998", "Broken", "0", "1", "1", "lucci", "1", "Clothes", "Shoes", "1999999", "0", "0");
        }, path =>
        {
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000, ShopCatalogSchema.Legacy);
            Assert.Null(catalog.FindPurchase(1001622));
            var stock = Assert.Single(catalog.Gifts[1001622].Stocks);
            Assert.False(stock.IsOnSale);
            Assert.Equal(200, stock.Duration);
            var lucci = catalog.Gifts[2000910];
            Assert.Equal(5000, lucci.Lucci);
            Assert.Empty(lucci.Stocks);
            Assert.Equal(1000001, Assert.Single(catalog.Gifts[2000999].Stocks).StockId);
            Assert.Equal(0, catalog.Gifts[2000999].Lucci);
            Assert.False(catalog.Gifts.ContainsKey(2000998));
        });
    }

    private static void WithLegacyWorkbook(Action<XDocument> change, Action<string> check)
    {
        var document = XDocument.Load(SyntheticClientTables.ShopItem2WithoutTerms);
        RemoveColumn(Table(document, "Stock"), "secondCash");
        RemoveColumn(Table(document, "Stock"), "sellPriceCash");
        RemoveColumn(Table(document, "StockSet"), "sellPriceCash");
        foreach (var (sheet, text) in new[] { ("MainCategory", "Legacy category"), ("SubCategory", "Legacy subcategory") })
        {
            var table = Table(document, sheet);
            var header = table.Elements(Ss + "Row").First().Elements(Ss + "Cell").ToArray();
            var index = Array.FindIndex(header, cell => cell.Element(Ss + "Data")!.Value == "displayName");
            header[index].Element(Ss + "Data")!.Value = "nameKr";
            table.Elements(Ss + "Row").ElementAt(1).Elements(Ss + "Cell")
                .ElementAt(index).Element(Ss + "Data")!.Value = text;
        }
        change(document);
        var path = Path.Combine(Path.GetTempPath(), $"legacy-shop-{Guid.NewGuid():N}.xml");
        try
        {
            document.Save(path);
            check(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static XElement Table(XDocument document, string name) => document.Root!.Elements(Ss + "Worksheet")
        .Single(sheet => (string?)sheet.Attribute(Ss + "Name") == name).Element(Ss + "Table")!;

    private static void RemoveColumn(XElement table, string column)
    {
        var rows = table.Elements(Ss + "Row").ToArray();
        var index = Array.FindIndex(rows[0].Elements(Ss + "Cell").ToArray(), cell => cell.Element(Ss + "Data")!.Value == column);
        Assert.True(index >= 0);
        foreach (var row in rows)
            row.Elements(Ss + "Cell").ElementAtOrDefault(index)?.Remove();
    }
}
