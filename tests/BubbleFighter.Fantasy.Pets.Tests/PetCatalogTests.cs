using System.Text;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Checks pet workbook identities, skill values and bounded parsing before catalog data reaches pet operations.</summary>
public sealed class PetCatalogTests
{
    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>Derives the pet category from follower items while retaining ordinary items and base growth metadata.</summary>
    [Fact]
    public void CategoryComesFromFollowerIdentityAndCompleteInventoryScopeIsKept()
    {
        var catalog = Load(Workbook());
        Assert.Equal(777, catalog.PetCategory);
        Assert.Equal(new PetItem(301234, 10001, 2, 777), catalog.Items[301234]);
        Assert.Equal(new PetItem(100321, 19, 5, 777), catalog.Items[100321]);
        Assert.Equal(900, catalog.Items[1234].Category);
        Assert.Equal(0, catalog.Items[5678].Category);
        Assert.Equal(4, catalog.Items.Count);
        Assert.Equal(10, catalog.GradeExperienceMaximum(0));
        Assert.Equal(100, catalog.GradeExperienceMaximum(1));
        Assert.Equal(86, catalog.HungerPeriodMinutes);
        Assert.Equal(100, catalog.HungerMaximum);
    }

    /// <summary>Preserves empty buff positions and signed rolls while applying the native integer interpretation of decimal bounds.</summary>
    [Fact]
    public void FourPositionsAndNativeIntegerParsingArePreserved()
    {
        var catalog = Load(Workbook());
        Assert.Equal(new[] { 1, 0, 2, 0 }, catalog.Skills[101].BuffIndices);
        Assert.Equal(9, catalog.Skills[101].Value);
        Assert.Equal(1, catalog.Skills[101].Grade);
        Assert.Equal(1.5m, catalog.Buffs[1].RawMinimum);
        Assert.Equal(1.5m, catalog.Buffs[1].RawMaximum);
        Assert.Equal(1, catalog.Buffs[1].Minimum);
        Assert.Equal(1, catalog.Buffs[1].Maximum);
        Assert.Equal(0.5m, catalog.Buffs[1].Conversion);
        Assert.Equal(-0.01m, catalog.Buffs[2].Conversion);
        Assert.Equal(new PetSkill(101, 1, 0, -2, 0), catalog.RollSkill(101, new MinimumRandom()));
        Assert.Equal(new PetSkill(101, 1, 0, 2, 0), catalog.RollSkill(101, new MaximumRandom()));
    }

    /// <summary>Accepts a saved zero-weight skill but refuses to select it for a new random roll.</summary>
    [Fact]
    public void ExistingZeroWeightSkillValidatesButCannotBeDrawn()
    {
        var catalog = Load(Workbook());
        catalog.ValidateSkill(new PetSkill(303, 1, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => catalog.RollSkill(303, new Random(1)));
    }

    /// <summary>Rejects saved skills with invalid values, occupied empty positions, unknown selectors or a non-pet category.</summary>
    [Theory]
    [InlineData(101, 2, 0, 0, 0)]
    [InlineData(101, 1, 1, 0, 0)]
    [InlineData(101, 1, 0, 3, 0)]
    [InlineData(404, 1, 0, 0, 0)]
    [InlineData(7, 0, 0, 0, 0)]
    [InlineData(999, 0, 0, 0, 0)]
    public void InvalidPersistedValuesAndNonPetSelectorsFail(int selector, int a, int b, int c, int d)
    {
        var catalog = Load(Workbook());
        Assert.Throws<InvalidDataException>(() => catalog.ValidateSkill(new PetSkill(selector, a, b, c, d)));
    }

    /// <summary>Allows a valid shared-pool skill even when the catalog contains no separate grade-two definition.</summary>
    [Fact]
    public void LaterAcquisitionDoesNotRequireAnInventedGradeTwoDefinition()
    {
        var catalog = Load(Workbook());
        Assert.DoesNotContain(catalog.Skills.Values, skill => skill.Grade == 2);
        catalog.ValidateSkill(catalog.RollSkill(101, new Random(1)));
    }

    /// <summary>Resolves a category from positioned rich-text cell data while ignoring the cell's comment payload.</summary>
    [Fact]
    public void SparseCellsAndRichTextKeepTheirPositionsWithoutReadingCellComments()
    {
        var workbook = Workbook();
        var row = Sheet(workbook, "ShopItem").Element(Ss + "Table")!.Elements(Ss + "Row").ElementAt(1);
        var cells = row.Elements(Ss + "Cell").ToArray();
        cells[1].Remove();
        cells[2].SetAttributeValue(Ss + "Index", 3);
        cells[2].Element(Ss + "Data")!.ReplaceNodes(new XElement("B", "Fol"), new XElement("I", "lowers"));
        cells[2].AddFirst(new XElement(Ss + "Comment", new XElement(Ss + "Data", "wrong-category")));
        Assert.Equal(777, Load(workbook).Items[301234].Category);
    }

    /// <summary>Rejects each workbook when a required catalog header has been replaced with an unrelated name.</summary>
    [Theory]
    [InlineData("ShopItem", "function")]
    [InlineData("SubCategory", "name")]
    [InlineData("ItemAttr", "buffIndex_4")]
    [InlineData("RandomTable", "conv")]
    public void RequiredHeadersCannotDisappear(string sheet, string column)
    {
        var workbook = Workbook();
        var header = Sheet(workbook, sheet).Descendants(Ss + "Row").First();
        header.Elements(Ss + "Cell").Single(cell => cell.Value == column).Element(Ss + "Data")!.Value = "missing";
        Assert.Contains("missing column", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Rejects repeated item, category, skill and buff identities instead of choosing one duplicate row.</summary>
    [Theory]
    [InlineData("ShopItem")]
    [InlineData("SubCategory")]
    [InlineData("ItemAttr")]
    [InlineData("RandomTable")]
    public void DuplicateIdentitiesFail(string sheet)
    {
        var workbook = Workbook();
        var table = Sheet(workbook, sheet).Element(Ss + "Table")!;
        table.Add(new XElement(table.Elements(Ss + "Row").ElementAt(1)));
        Assert.Contains("repeat", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>A name two subcategories share (kr-1256's "Packages") leaves ordinary items uncategorized.</summary>
    [Fact]
    public void RepeatedSubCategoryNameLeavesOrdinaryItemsUncategorized()
    {
        var workbook = Workbook();
        AddRow(workbook, "SubCategory", "901", "Other");
        var catalog = Load(workbook);
        Assert.Equal(0, catalog.Items[1234].Category);
        Assert.Equal(777, catalog.PetCategory);
    }

    /// <summary>A pet whose subcategory name is shared by two ids cannot be assigned a category and fails.</summary>
    [Fact]
    public void PetOnARepeatedSubCategoryNameFails()
    {
        var workbook = Workbook();
        AddRow(workbook, "SubCategory", "778", "Followers");
        Assert.Contains("ambiguous subCategory", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Rejects item categories and skill buff references that have no corresponding catalog definition.</summary>
    [Fact]
    public void DanglingCategoryAndBuffReferencesFail()
    {
        var workbook = Workbook();
        SetCell(workbook, "ShopItem", 1, "subCategory", "Missing");
        Assert.Contains("unknown subCategory", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
        workbook = Workbook();
        SetCell(workbook, "ItemAttr", 2, "buffIndex_3", "999");
        Assert.Contains("missing buff", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Rejects conflicting pet categories and base metadata whose category or named buff does not match.</summary>
    [Fact]
    public void MultiplePetCategoriesAndMismatchedBaseMetadataFail()
    {
        var workbook = Workbook();
        SetCell(workbook, "ShopItem", 3, "function", "10001");
        Assert.Throws<InvalidDataException>(() => Load(workbook));
        workbook = Workbook();
        SetCell(workbook, "ItemAttr", 1, "category", "900");
        Assert.Throws<InvalidDataException>(() => Load(workbook));
        workbook = Workbook();
        SetCell(workbook, "RandomTable", 3, "buffName", "unknown");
        Assert.Throws<InvalidDataException>(() => Load(workbook));
    }

    /// <summary>Rejects inverted or nonnumeric buff bounds and negative skill selection weights.</summary>
    [Fact]
    public void InvalidNumericBoundsAndNegativeWeightFail()
    {
        var workbook = Workbook();
        SetCell(workbook, "RandomTable", 1, "min", "nonnumeric");
        Assert.Throws<InvalidDataException>(() => Load(workbook));
        workbook = Workbook();
        SetCell(workbook, "ItemAttr", 2, "weight", "-1");
        Assert.Throws<InvalidDataException>(() => Load(workbook));
    }

    /// <summary>An inverted buff range in the client's own data (tw-454 buff 75) withdraws only the skills that use it.</summary>
    [Fact]
    public void InvertedBuffRangeWithdrawsItsSkillsFromTheDraw()
    {
        var workbook = Workbook();
        AddRow(workbook, "ItemAttr", "102", "777", "1", "1", "5", "1", "", "", "");
        SetCell(workbook, "RandomTable", 2, "min", "3");
        var catalog = Load(workbook);
        Assert.Equal(5, catalog.Skills[102].Weight);
        Assert.Equal(0, catalog.Skills[101].Weight);
        Assert.Equal(100, catalog.Skills[7].Weight);
    }

    /// <summary>Passes the configured byte limit to client data and independently rejects an oversized returned workbook.</summary>
    [Fact]
    public void StoreReceivesTheByteLimitAndCannotReturnOversizedData()
    {
        var store = new Store(Encoding.UTF8.GetBytes(Workbook().ToString()));
        PetCatalogLoader.Load(store, "Config/ShopItem2.xml", 100_000, 100, 32, 256);
        Assert.Equal(100_000, store.LastLimit);
        Assert.Throws<InvalidDataException>(() => PetCatalogLoader.Load(store, "Config/ShopItem2.xml", 10, 100, 32, 256));
    }

    /// <summary>Applies the catalog row budget to the combined worksheets rather than restarting it for each sheet.</summary>
    [Fact]
    public void RowLimitAppliesAcrossWorksheets()
    {
        Assert.Contains("max_catalog_rows", Assert.Throws<InvalidDataException>(() => Load(Workbook(), maxRows: 6)).Message);
    }

    /// <summary>Rejects oversized or backward sparse cell indices before they can expand or overwrite column positions.</summary>
    [Fact]
    public void SparseIndexIsBoundedBeforeExpansionAndCannotMoveBackwards()
    {
        var workbook = Workbook();
        Sheet(workbook, "ShopItem").Descendants(Ss + "Cell").First().SetAttributeValue(Ss + "Index", int.MaxValue);
        Assert.Contains("max_catalog_columns", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
        workbook = Workbook();
        Sheet(workbook, "ShopItem").Descendants(Ss + "Cell").ElementAt(1).SetAttributeValue(Ss + "Index", 1);
        Assert.Contains("sparse cell index", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Ignores trailing style-only cells while still enforcing the column limit when later cells contain data.</summary>
    [Fact]
    public void LegacyTrailingStyleCellsDoNotExpandTheDataColumns()
    {
        var workbook = Workbook();
        var row = Sheet(workbook, "ShopItem").Descendants(Ss + "Row").ElementAt(1);
        for (var index = 0; index < 40; index++)
            row.Add(new XElement(Ss + "Cell", new XAttribute(Ss + "StyleID", "empty")));
        Assert.Equal(777, Load(workbook).Items[301234].Category);
        row.Add(new XElement(Ss + "Cell", new XElement(Ss + "Data", "actual-value")));
        Assert.Contains("max_catalog_columns", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Rejects oversized cell text and duplicate worksheet names with the corresponding catalog diagnostics.</summary>
    [Fact]
    public void CellLengthAndRepeatedWorksheetsAreRejected()
    {
        var workbook = Workbook();
        SetCell(workbook, "ShopItem", 1, "unused", new string('x', 257));
        Assert.Contains("max_cell_characters", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
        workbook = Workbook();
        workbook.Add(new XElement(Sheet(workbook, "ItemAttr")));
        Assert.Contains("repeats worksheet", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>Rejects XML document type declarations and catalogs missing a required worksheet.</summary>
    [Fact]
    public void DtdAndMissingWorksheetsAreRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("<!DOCTYPE Workbook [<!ENTITY data 'x'>]>" + Workbook());
        Assert.Throws<XmlException>(() => PetCatalogLoader.Load(new Store(bytes), "Config/ShopItem2.xml", 100_000, 100, 32, 256));
        var workbook = Workbook();
        Sheet(workbook, "ItemAttr").Remove();
        Assert.Contains("missing worksheet", Assert.Throws<InvalidDataException>(() => Load(workbook)).Message);
    }

    /// <summary>
    /// The catalog written to the compiled client data and read back is the catalog that was written: every item
    /// with its optional duration fields, every skill with its four references, every buff with its exact decimals.
    /// </summary>
    [Fact]
    public void CompiledCatalogReadsBackEqual()
    {
        var workbook = Workbook();
        AddRow(workbook, "ItemAttr", "102", "777", "1", "1", "5", "1", "", "", "");
        var catalog = Load(workbook);
        using var section = new MemoryStream();
        PackedPetCatalog.Catalog.Write(catalog, section);
        section.Position = 0;
        var read = PackedPetCatalog.Catalog.Read(section);
        Assert.Equal(section.Length, section.Position);

        Assert.Equal(catalog.PetCategory, read.PetCategory);
        Assert.Equal(catalog.Items.OrderBy(pair => pair.Key), read.Items.OrderBy(pair => pair.Key));
        Assert.Equal(catalog.Buffs.OrderBy(pair => pair.Key), read.Buffs.OrderBy(pair => pair.Key));
        Assert.Equal(catalog.Skills.Keys.Order(), read.Skills.Keys.Order());
        foreach (var (selector, skill) in catalog.Skills)
        {
            var other = read.Skills[selector];
            Assert.Equal(skill with { BuffIndices = [] }, other with { BuffIndices = [] });
            Assert.Equal(skill.BuffIndices.AsEnumerable(), other.BuffIndices.AsEnumerable());
        }
        // 1.5 and -0.01 keep their scale, not only their value.
        Assert.Equal(decimal.GetBits(catalog.Buffs[2].Conversion), decimal.GetBits(read.Buffs[2].Conversion));
        Assert.Equal(catalog.HungerMaximum, read.HungerMaximum);

        using var again = new MemoryStream();
        PackedPetCatalog.Catalog.Write(read, again);
        Assert.Equal(section.ToArray(), again.ToArray());
    }

    /// <summary>Compiled bytes that are not a catalog are refused, and one that fails the catalog's own checks too.</summary>
    [Fact]
    public void CompiledCatalogRefusesDamagedBytes()
    {
        using var section = new MemoryStream();
        PackedPetCatalog.Catalog.Write(Load(Workbook()), section);
        var bytes = section.ToArray();
        Assert.ThrowsAny<Exception>(() => PackedPetCatalog.Catalog.Read(new MemoryStream(bytes[..^3])));
        Assert.ThrowsAny<Exception>(() => PackedPetCatalog.Catalog.Read(new MemoryStream([4, 0xFF, 0xFF, 0xFF, 0x7F])));
        byte[] Section(PackedPetCatalog packed) => MemoryPack.MemoryPackSerializer.Serialize(packed);
        // No items, skills or buffs: the constructor's requirement of a base definition still applies.
        Assert.Throws<InvalidDataException>(() => PackedPetCatalog.Catalog.Read(new MemoryStream(Section(new([], [], [], 777)))));
        Assert.Throws<InvalidDataException>(() => PackedPetCatalog.Catalog.Read(new MemoryStream(Section(new(null!, [], [], 777)))));
        var item = new PackedPetItem(1, 2, 3, 4, null, null);
        Assert.Contains("repeats key 1", Assert.Throws<InvalidDataException>(() =>
            PackedPetCatalog.Catalog.Read(new MemoryStream(Section(new([item, item], [], [], 777))))).Message);
    }

    private static PetCatalog Load(XElement workbook, int maxRows = 100) =>
        PetCatalogLoader.Load(new Store(Encoding.UTF8.GetBytes(workbook.ToString())),
            "Config/ShopItem2.xml", 100_000, maxRows, 32, 256);

    private static XElement Workbook() => new(Ss + "Workbook", new XAttribute(XNamespace.Xmlns + "ss", Ss),
        Table("ShopItem", ["itemId", "unused", "subCategory", "parameter", "function"],
            ["301234", "", "Followers", "2", "10001"], ["100321", "", "Followers", "5", "19"],
            ["1234", "", "Other", "0", "0"], ["5678", "", "", "0", "0"]),
        Table("SubCategory", ["id", "name"], ["777", "Followers"], ["900", "Other"]),
        Table("ItemAttr", ["index", "category", "grade", "value", "weight", "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4"],
            ["7", "777", "0", "0", "100", "", "18", "16", ""],
            ["101", "777", "1", "9", "3", "1", "", "2", ""],
            ["303", "777", "1", "1", "0", "1", "", "", ""],
            ["404", "900", "1", "1", "1", "1", "", "", ""]),
        Table("RandomTable", ["buffIndex", "buffName", "randomGroup", "min", "max", "conv"],
            ["1", "testCritical", "1", "1.5", "1.5", "0.5"], ["2", "testReload", "2", "-2", "2", "-0.01"],
            ["16", "gradeExp1", "6", "0", "10", "1"], ["17", "gradeExp2", "7", "0", "100", "1"],
            ["18", "hunger", "8", "86", "100", "1"]));

    private static XElement Table(string name, string[] header, params string[][] rows) =>
        new(Ss + "Worksheet", new XAttribute(Ss + "Name", name), new XElement(Ss + "Table",
            new[] { header }.Concat(rows).Select(row => new XElement(Ss + "Row",
                row.Select(cell => new XElement(Ss + "Cell", new XElement(Ss + "Data", cell)))))));

    private static void AddRow(XElement workbook, string sheet, params string[] cells) =>
        Sheet(workbook, sheet).Element(Ss + "Table")!.Add(new XElement(Ss + "Row",
            cells.Select(cell => new XElement(Ss + "Cell", new XElement(Ss + "Data", cell)))));

    private static XElement Sheet(XElement workbook, string name) =>
        workbook.Elements(Ss + "Worksheet").Single(sheet => (string?)sheet.Attribute(Ss + "Name") == name);

    private static void SetCell(XElement workbook, string sheet, int row, string column, string value)
    {
        var rows = Sheet(workbook, sheet).Element(Ss + "Table")!.Elements(Ss + "Row").ToArray();
        var index = rows[0].Elements(Ss + "Cell").Select((cell, position) => (cell, position))
            .Single(pair => pair.cell.Value == column).position;
        rows[row].Elements(Ss + "Cell").ElementAt(index).Element(Ss + "Data")!.Value = value;
    }

    private sealed class Store(byte[] bytes) : IClientDataStore
    {
        internal int LastLimit { get; private set; }
        /// <summary>Makes the supplied catalog bytes available under any requested client path.</summary>
        public bool Exists(string path) => true;
        /// <summary>Fails unbounded reads so catalog loading must use the overload carrying the configured byte limit.</summary>
        public byte[] Read(string path) => throw new InvalidOperationException("The bounded read overload is required.");
        /// <summary>Records the requested byte limit and returns the supplied bytes so the loader must verify their size.</summary>
        public byte[] Read(string path, int maximumBytes) { LastLimit = maximumBytes; return bytes; }
    }

    private sealed class MinimumRandom : Random
    {
        /// <summary>Selects the inclusive lower bound to make minimum buff rolls deterministic.</summary>
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    private sealed class MaximumRandom : Random
    {
        /// <summary>Selects the last value below the exclusive upper bound to make maximum buff rolls deterministic.</summary>
        public override long NextInt64(long minValue, long maxValue) => maxValue - 1;
    }
}
