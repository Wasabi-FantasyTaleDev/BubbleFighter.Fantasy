using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Titles.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Checks both audited native title workbooks supplied locally by the developer.</summary>
[Trait("Category", "LocalClientData")]
public sealed class TitleRealCatalogTests
{
    /// <summary>Real title grants are permanent, with the client stock identity and exact medal debit.</summary>
    [Theory]
    [InlineData("kr-812", 122, false)]
    [InlineData("kr-1256", 510, true)]
    public void NativeCatalogLoadsWithPermanentStockGrants(string build, int count, bool modern)
    {
        var catalog = TitleCatalog.Load(new NativeStore(build), "Config/BF_title.xml", "Config/ShopItem2.xml",
            modern ? TitleCatalogSchema.MedalFlag : TitleCatalogSchema.Legacy,
            134_217_728, 100_000, 256, 16_384);
        Assert.Equal(count, catalog.Titles.Count);
        var title = catalog.Titles[400001];
        Assert.Equal(1200001, title.StockId);
        Assert.Equal(400001, title.GrantTerm.ItemId);
        Assert.Contains(new TitleMedalRequirement(30007, 20), title.Requirements);
        Assert.All(catalog.Titles.Values, row => Assert.Equal(InventoryGrantKind.Permanent, row.GrantTerm.Kind));
        Assert.Equal(3, catalog.Titles.Values.Count(row => row.BonusGrants.Count != 0));
        Assert.DoesNotContain(catalog.Titles.Values, row => row.HasUnsupportedBonuses);
        Assert.True(catalog.Titles[400080].RequiresPartner);
    }

    private sealed class NativeStore(string build) : IClientDataStore
    {
        public bool Exists(string path) => path is "Config/BF_title.xml" or "Config/ShopItem2.xml";
        public byte[] Read(string path) => LocalClientData.Bytes(build, path);
    }
}
