using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks the grade-up attributes and the set classes against the original KR1256 workbook.</summary>
[Trait("Category", "LocalClientData")]
public sealed class NativeAdvancementCatalogTests
{
    private static EnhancementCatalog Load()
    {
        var limits = new EnhancementArticleOptions();
        return EnhancementCatalog.Load(new NativeStore(), limits.CatalogPath, limits.MaxWorkbookBytes,
            limits.MaxCatalogRows, limits.MaxCatalogColumns, limits.MaxCellCharacters, includeSets: true);
    }

    /// <summary>ItemAttr 100, 157 and 158 carry fixed whole values: three buffs, and the pang distance buff for the two L4 grades.</summary>
    [Fact]
    public void TheNativeGradeUpAttributesHaveFixedValues()
    {
        var catalog = Load();
        Assert.Equal([3, 3, 3], catalog.AdvancementValues(100));
        Assert.Equal([3, 3, 3, 4], catalog.AdvancementValues(157));
        Assert.Equal([3, 3, 3, 4], catalog.AdvancementValues(158));
        Assert.Throws<InvalidDataException>(() => catalog.AdvancementValues(99));
    }

    /// <summary>The set classes follow the largest member isAdvancedable, and a complete policy loads over real sets and cards.</summary>
    [Fact]
    public void NativeSetsFallIntoTheClientsFourClasses()
    {
        var catalog = Load();
        var classes = catalog.Sets.Values.GroupBy(set => AdvancementPolicy.ClassOf(catalog, set)).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal([0, 1, 2, 3, 4], classes.Keys.Order());
        Assert.Equal(238, classes[1]);
        Assert.Equal(113, classes[3]);
        var options = new ItemAdvancementOptions
        {
            Enabled = true, TabCardItemId = 190247,
            Rules =
            [
                new() { CardItemId = 190247, SourceTier = 3, CardQuantity = 1 },
                new() { CardItemId = 190558, SourceTier = 4, CardQuantity = 1 },
                new() { CardItemId = 190570, SourceTier = 5, CardQuantity = 1 },
            ],
        };
        var policy = new AdvancementPolicy(options, catalog, new("kr-1256", catalog.Items.Keys.ToArray()));
        Assert.Equal([1, 2, 3], policy.Configuration.Groups.Select(group => group.Key));
    }

    private sealed class NativeStore : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/ShopItem2.xml";
        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);
    }
}
