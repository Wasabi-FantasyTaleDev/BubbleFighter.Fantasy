using BubbleFighter.Fantasy.Tests;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopCatalogOfferTests
{
    [Fact]
    public void SetUsesItsOwnPurchaseKeyPriceAndAvailabilityWithoutChangingGrants()
    {
        var gift = new ShopStockRow(1000726, 207026, false, 1, 7, 1, 1, 0, 0);
        var set = new ShopCardRow(2000108, "Bundle", "lucci", true, false, 42000, 42000, 0,
            "Weapons", "Rifle", gift.ItemId, [gift], 300, 301, 508625);
        var catalog = new ShopCatalog("fixture", [set], []);
        var bytes = WireLengthSupport.Written(w => ShopWire.WriteItemCard(w, set));

        // 0x1140350: 74 fixed bytes, UTF-16 name, and one 26-byte purchase choice.
        Assert.Equal(112, bytes.Length);
        var r = new PacketReader(bytes);
        Assert.Equal(2000108, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal("Bundle", r.ReadWideStringPrefixed());
        Assert.Equal(508625, r.ReadInt32());
        Assert.Equal(300, r.ReadInt32());
        Assert.Equal(301, r.ReadInt32());
        Assert.True(r.ReadBool32()); // Visibility is independent of refund eligibility.
        r.ReadInt64();
        r.ReadInt64();
        ReadSetTerm(ref r);
        Assert.Equal(1u, r.ReadUInt32());
        ReadSetTerm(ref r);
        Assert.Equal(0, r.Remaining);
        var purchase = Assert.IsType<ShopPurchaseOffer>(catalog.FindPurchase(2000108));
        Assert.Equal(42000, purchase.Price);
        Assert.Equal(gift, Assert.Single(purchase.Stocks));
        Assert.Null(catalog.FindPurchase(1000726));
    }

    [Fact]
    public void LoaderKeepsSeparatePricesAndDurationsForStocksOfTheSameItem()
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        var doc = XDocument.Load(SyntheticClientTables.ShopItem2);
        XElement Table(string name) => doc.Root!.Elements(ns + "Worksheet")
            .Single(w => (string?)w.Attribute(ns + "Name") == name).Element(ns + "Table")!;
        XElement Row(params object[] values) => new(ns + "Row",
            values.Select(value => new XElement(ns + "Cell", new XElement(ns + "Data", value))));
        var items = Table("ShopItem");
        items.Elements(ns + "Row").Skip(1).Remove();
        items.Add(Row(100001, "Shoes", 1, "lucci", 0, "Clothes", "Shoes", 400100));
        Table("Stock").Add(Row(1000002, 100001, 1, 1, 7, 720, 600, 0, 0));
        var path = Path.Combine(Path.GetTempPath(), $"shop-offers-{Guid.NewGuid():N}.xml");
        try
        {
            doc.Save(path);
            var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 1_000_000);
            var singles = catalog.Cards.Where(c => c.StockSetId == 0).ToArray();
            Assert.Equal(2, singles.Length);
            Assert.All(singles, card =>
            {
                Assert.Equal(100001, card.CardId);
                Assert.Equal(0, card.CardKind);
                Assert.Equal(400100, card.DisplayOrder);
                Assert.Single(card.PurchaseTerms);
            });
            var first = catalog.FindPurchase(1000001)!;
            var second = catalog.FindPurchase(1000002)!;
            Assert.Equal(120, first.Price);
            Assert.Equal(1, Assert.Single(first.Stocks).Duration);
            Assert.Equal(600, second.Price);
            Assert.Equal(7, Assert.Single(second.Stocks).Duration);
            Assert.Equal(720, singles[1].ShowPrice); // A workbook discount is not an inferred price override.
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ReadSetTerm(ref PacketReader r)
    {
        Assert.Equal(2000108, r.ReadInt32());
        Assert.Equal(2000108, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(42000, r.ReadInt32());
        Assert.Equal(42000, r.ReadInt32());
    }
}
