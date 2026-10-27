using System.Text;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Byte-level audit of the id-400 enter-shop reply against readers
/// <c>sub_799330</c> / <c>sub_45FB30</c> / <c>sub_798560</c> and the main-tab
/// fill <c>sub_8E4BB0</c> (default sub id) / <c>sub_8E4C00</c> (pageType at +40,
/// mode 2 compares card+44 to the selected main).
/// </summary>
public sealed class Id400EnterShopWireDumpTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the dump fixture.</summary>
    /// <param name="output">xUnit output for the audited offsets.</param>
    public Id400EnterShopWireDumpTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "LocalClientData")]
    public void Id400EnterShopWireExposesDefaultSubPageType2AndMatchingCardMains()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "shop-tables-survey", "id400", "xml", "Config", "ShopItem2.xml"));
        if (!File.Exists(path))
            path = Path.GetFullPath("artifacts/shop-tables-survey/id400/xml/Config/ShopItem2.xml");
        Assert.True(File.Exists(path), $"id-400 ShopItem2.xml missing at {path}");

        var catalog = ShopCatalogLoader.Load(ShopWorkbooks.At(path), 134_217_728, ShopCatalogSchema.Legacy, "name", _ => { });
        var merged = ShopWire.MergeMainClickDefaults(
            ShopWire.EnsureEnterShopLandingMain(catalog.Categories, catalog.ShopVisibleMains),
            catalog.ShopVisibleSubcategories,
            catalog.MainDefaultSubcategoryIds);
        var (cards, _) = ShopWire.SelectBoundedCards(catalog.Cards, 65000);
        var payload = ShopWire.RepEnterShopLegacy(LegacyLayouts.Of("id-400"), 0, cards, merged);

        _output.WriteLine($"payloadLen={payload.Length} cards={cards.Count} mains={merged.Count}");

        // 5822 was the MergeMainClickDefaults deploy without Main/1000. Landing main +
        // NewItems (sub_7A9A90 open tab / sub_8E1F40 click gate) is 5878.
        Assert.Equal(5878, payload.Length);
        Assert.Contains(merged, c => c.Id == ShopWire.LegacyEnterShopMainCategory);
        Assert.Contains(
            merged.First(c => c.Id == ShopWire.LegacyEnterShopMainCategory).Subcategories,
            s => s.Id == ShopWire.LegacyEnterShopDefaultSubCategory);

        var o = 0;
        Assert.Equal(0, ReadI32(payload, ref o));
        Assert.Equal(1, ReadI32(payload, ref o));
        Assert.Equal(0L, BitConverter.ToInt64(payload.AsSpan(o, 8)));
        o += 8;
        var cardCount = ReadI32(payload, ref o);
        Assert.Equal(cards.Count, cardCount);

        var mainsSeen = new HashSet<int>();
        for (var i = 0; i < cardCount; i++)
        {
            var cardId = ReadI32(payload, ref o);
            var kind = ReadI32(payload, ref o);
            var name = ReadWStr(payload, ref o);
            var disp = ReadI32(payload, ref o);
            var main = ReadI32(payload, ref o);
            var sub = ReadI32(payload, ref o);
            var onSale = ReadI32(payload, ref o);
            o += 16;
            var stockId = ReadI32(payload, ref o);
            o += 4 + 4 + 2 + 12;
            var termCount = ReadI32(payload, ref o);
            for (var t = 0; t < termCount; t++)
                o += 4 + 4 + 4 + 2 + 12;
            mainsSeen.Add(main);
            if (i < 3 || main is 100 or 200 or 300 or 400)
                _output.WriteLine($"card[{i}] id={cardId} kind={kind} main={main} sub={sub} onSale={onSale} stock={stockId} disp={disp} '{name}'");
        }

        Assert.Equal(0L, BitConverter.ToInt64(payload.AsSpan(o, 8)));
        o += 8;
        var catCount = ReadI32(payload, ref o);
        Assert.Equal(merged.Count, catCount);

        // sub_8E4BB0 looks up dialog+740 = shopDefaultSubCategory or main id.
        var expectedDefaults = new Dictionary<int, int>();
        foreach (var main in merged)
        {
            if (catalog.MainDefaultSubcategoryIds.TryGetValue(main.Id, out var named))
                expectedDefaults[main.Id] = named;
            else
                expectedDefaults[main.Id] = main.Id;
        }

        for (var c = 0; c < catCount; c++)
        {
            var mainId = ReadI32(payload, ref o);
            var mainName = ReadWStr(payload, ref o);
            var mainDisp = ReadI32(payload, ref o);
            var subCount = ReadI32(payload, ref o);
            var defaultId = expectedDefaults[mainId];
            var foundDefault = false;
            var defaultPageType = -1;
            _output.WriteLine($"MAIN {mainId} '{mainName}' disp={mainDisp} subs={subCount} expectDefault={defaultId}");
            for (var s = 0; s < subCount; s++)
            {
                var subOffset = o;
                var sid = ReadI32(payload, ref o);
                var sname = ReadWStr(payload, ref o);
                var sdisp = ReadI32(payload, ref o);
                // sub_798560 stores this trailing i32 at subcategory +40 (pageType).
                var pageType = ReadI32(payload, ref o);
                _output.WriteLine($"  sub id={sid} pt={pageType} disp={sdisp} wireOff={subOffset} '{sname}'");
                Assert.Equal(2, pageType);
                if (sid == defaultId)
                {
                    foundDefault = true;
                    defaultPageType = pageType;
                }
            }

            Assert.True(foundDefault,
                $"main {mainId} missing default sub {defaultId} that sub_8E4BB0 looks up");
            Assert.Equal(2, defaultPageType);
        }

        Assert.Equal(payload.Length, o);

        // Clothes/weapons/character must be selectable with on-sale cards.
        Assert.Contains(300, mainsSeen);
        Assert.Contains(200, mainsSeen);
        Assert.Contains(400, mainsSeen);
        Assert.Contains(100, mainsSeen);
        // Main/1000 is in the category tree (for tab-click) but has no on-sale cards.
        Assert.DoesNotContain(ShopWire.LegacyEnterShopMainCategory, mainsSeen);
    }

    private static int ReadI32(byte[] b, ref int o)
    {
        var v = BitConverter.ToInt32(b.AsSpan(o, 4));
        o += 4;
        return v;
    }

    private static string ReadWStr(byte[] b, ref int o)
    {
        var n = ReadI32(b, ref o);
        var s = Encoding.Unicode.GetString(b, o, n * 2);
        o += n * 2;
        return s;
    }
}
