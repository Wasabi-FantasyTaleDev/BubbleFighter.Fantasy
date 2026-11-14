using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>
/// Pins the native permanent-conversion flag as its own catalog property: it is read beside, never instead of,
/// Enchant and Modification eligibility, and it is the only property that covers some converted outputs.
/// </summary>
public sealed class UnlimitedConversionCatalogTests
{
    /// <summary>The flag is parsed independently of the two enhancement flags, and a build without the column has none.</summary>
    [Fact]
    public void TheFlagIsReadSeparatelyAndAbsentColumnsReadAsZero()
    {
        var catalog = Load(Workbook(["itemId", "mainCategory", "subCategory", "durationType", "isEnchant", "isModificatable", "isUnlimited"],
            ["1", "Clothes", "Hats", "day", "0", "0", "1"], ["2", "Weapons", "MeleeWeapon", "day", "1", "0", "0"],
            ["3", "Etc", "SienceLab", "count", "0", "0", "0"], ["4", "Etc", "Gift", "day", "0", "0", "1"]));

        var conversionOnly = catalog.Items[1];
        Assert.True(conversionOnly.IsUnlimited);
        Assert.False(conversionOnly.IsEnchant);
        Assert.False(conversionOnly.RequiresArticle);
        Assert.True(conversionOnly.ConversionRequiresArticle);
        Assert.False(catalog.Items[2].IsUnlimited);
        Assert.False(catalog.Items[2].ConversionRequiresArticle);
        Assert.False(catalog.Items[3].ConversionRequiresArticle);
        // A flagged item that is not equipment gets no equipment identity from the flag alone.
        Assert.True(catalog.Items[4].IsUnlimited);
        Assert.False(catalog.Items[4].ConversionRequiresArticle);

        var legacy = Load(Workbook(["itemId", "mainCategory", "subCategory", "durationType"], ["1", "Clothes", "Hats", "day"]));
        Assert.False(legacy.Items[1].IsUnlimited);
    }

    /// <summary>Values other than zero, one or empty are refused while loading, naming the item.</summary>
    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    public void AnInvalidFlagIsRefused(string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(Workbook(
            ["itemId", "mainCategory", "subCategory", "durationType", "isUnlimited"], ["7", "Clothes", "Hats", "day", value])));
        Assert.Contains("ShopItem 7 has invalid isUnlimited", error.Message);
    }

    /// <summary>
    /// The KR1256 workbook flags 756 equipment items; 72 of them (four clothes, 68 weapons) are covered by neither
    /// Enchant nor Modification eligibility, including the four members of native SetItem 2000508.
    /// </summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeConversionTargetsAreAllEquipmentAndExtendArticleCoverage()
    {
        var limits = new EnhancementArticleOptions();
        var catalog = EnhancementCatalog.Load(new NativeStore(), limits.CatalogPath, limits.MaxWorkbookBytes,
            limits.MaxCatalogRows, limits.MaxCatalogColumns, limits.MaxCellCharacters);
        var targets = catalog.Items.Values.Where(item => item.IsUnlimited).ToArray();

        Assert.Equal(756, targets.Length);
        Assert.All(targets, item =>
        {
            Assert.True(item.ConversionRequiresArticle);
            Assert.Equal("day", item.DurationType);
        });
        var uncovered = targets.Where(item => !item.RequiresArticle).ToArray();
        Assert.Equal(4, uncovered.Count(item => item.Kind == ItemEnhancementKind.Clothing));
        Assert.Equal(68, uncovered.Count(item => item.Kind == ItemEnhancementKind.Weapon));
        Assert.Equal(23, uncovered.Count(item => item.SubCategory == "MeleeWeapon"));
        Assert.Equal(45, uncovered.Count(item => item.SubCategory == "SubWeapon"));
        foreach (var id in new[] { 190035, 190036, 190037, 190038 })
            Assert.Contains(uncovered, item => item.Id == id);
        // The native conversion card is a counted material, not a target.
        Assert.False(catalog.Items[190151].IsUnlimited);
        Assert.Equal("count", catalog.Items[190151].DurationType);
    }

    private static EnhancementCatalog Load(byte[] workbook, bool includeSets = false) =>
        EnhancementCatalog.Load(new BytesStore(workbook), "catalog", 1 << 20, 100, 20, 100, includeSets);

    /// <summary>
    /// Set rows keep the first setCnt member columns in order, ignore ids past it, load only on request, and refuse an
    /// empty or repeated member inside the count.
    /// </summary>
    [Fact]
    public void SetsKeepTheirFirstMembersInColumnOrder()
    {
        string[] header = ["itemId", "mainCategory", "subCategory", "durationType", "isUnlimited"];
        string[][] items = [["1", "Clothes", "Hats", "day", "1"], ["2", "Clothes", "Hats", "day", "1"]];
        var catalog = Load(WithSets(header, items, ["setId", "setCnt", "ShopItemID01", "ShopItemID02", "ShopItemID03"],
            ["2000001", "2", "2", "1", "9"], ["#2000002", "1", "1", "", ""], ["2000003", "1", "1", "", ""]), includeSets: true);

        Assert.Equal([2, 1], catalog.Sets[2000001].Members);
        Assert.Equal([1], catalog.Sets[2000003].Members);
        Assert.False(catalog.Sets.ContainsKey(2000002));
        Assert.Empty(Load(WithSets(header, items, ["setId", "setCnt", "ShopItemID01"], ["2000001", "1", "1"])).Sets);
        foreach (var row in new[] { new[] { "2000001", "2", "1", "1", "" }, ["2000001", "2", "1", "", ""], ["2000001", "0", "", "", ""],
            ["2000001", "11", "1", "2", ""] })
            Assert.Throws<InvalidDataException>(() => Load(WithSets(header, items,
                ["setId", "setCnt", "ShopItemID01", "ShopItemID02", "ShopItemID03"], row), includeSets: true));
        Assert.Throws<InvalidDataException>(() => Load(WithSets(header, items, ["setId", "setCnt", "ShopItemID01"],
            ["2000001", "1", "1"], ["2000001", "1", "2"]), includeSets: true));
    }

    /// <summary>
    /// The KR1256 workbook lists 707 sets; 111 consist only of unlimited equipment. The client's own pre-send check
    /// (a non-empty member part location) holds for none of them, so no native client requests a set conversion there.
    /// </summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeSetsLoadAndEveryUnlimitedSetIsEquipment()
    {
        var limits = new EnhancementArticleOptions();
        var catalog = EnhancementCatalog.Load(new NativeStore(), limits.CatalogPath, limits.MaxWorkbookBytes,
            limits.MaxCatalogRows, limits.MaxCatalogColumns, limits.MaxCellCharacters, includeSets: true);
        var convertible = catalog.Sets.Values.Where(set => set.Members.All(member =>
            catalog.Items.TryGetValue(member, out var item) && item.IsUnlimited)).ToArray();

        Assert.Equal(707, catalog.Sets.Count);
        Assert.Equal(111, convertible.Length);
        Assert.All(convertible, set => Assert.All(set.Members, member => Assert.True(catalog.Items[member].ConversionRequiresArticle)));
        Assert.Equal([190035, 190036, 190037, 190038], catalog.Sets[2000508].Members);
        // A row that lists more ids than its setCnt keeps only the counted ones.
        Assert.Equal([102702], catalog.Sets[2002805].Members);
    }

    private static byte[] Workbook(string[] header, params string[][] rows) => Build(header, rows, null);

    private static byte[] WithSets(string[] header, string[][] rows, params string[][] sets) => Build(header, rows, sets);

    private static byte[] Build(string[] header, string[][] rows, string[][]? setRows)
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        XElement Sheet(string name, params string[][] cells) => new(ns + "Worksheet", new XAttribute(ns + "Name", name),
            new XElement(ns + "Table", cells.Select(row => new XElement(ns + "Row", row.Select(cell =>
                new XElement(ns + "Cell", new XElement(ns + "Data", new XAttribute(ns + "Type", "String"), cell)))))));
        return Encoding.UTF8.GetBytes(new XElement(ns + "Workbook",
            Sheet("MainCategory", ["id", "name"], ["1", "Weapons"], ["2", "Clothes"], ["3", "Etc"]),
            Sheet("SubCategory", ["id", "name"], ["1", "Hats"], ["2", "MeleeWeapon"], ["3", "SienceLab"], ["4", "Gift"]),
            Sheet("ShopItem", [header, .. rows]),
            Sheet("ItemAttr", ["index", "value", "category", "grade", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"]),
            Sheet("RandomTable", ["buffIndex", "buffName", "min", "max", "conv"]),
            setRows is null ? null : Sheet("SetItem", setRows)).ToString());
    }

    private sealed class BytesStore(byte[] bytes) : IClientDataStore
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path) => bytes;
    }

    private sealed class NativeStore : IClientDataStore
    {
        public bool Exists(string path) => path == "Config/ShopItem2.xml";
        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);
    }
}
