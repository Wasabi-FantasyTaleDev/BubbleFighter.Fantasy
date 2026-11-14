using System.Text;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Titles.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Validates native title joins and bounded XML parsing without distributing client assets.</summary>
public sealed class TitleCatalogTests
{
    /// <summary>Both audited schemas preserve stock grants and exact medal debits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSchemasJoinTitleStockAndMedalCost(bool modern)
    {
        var sample = new Sample(modern);
        var catalog = sample.Load();
        var title = Assert.Single(catalog.Titles).Value;
        Assert.Equal(400001, title.TitleId);
        Assert.Equal(1200001, title.StockId);
        Assert.Equal(new InventoryGrantTerm(400901, InventoryGrantKind.Permanent, 1, 360, false), title.GrantTerm);
        Assert.Equal(new TitleMedalRequirement(30007, 20), Assert.Single(title.Requirements));
        Assert.Equal(new TitleMedalDefinition(30007, 3, 7), Assert.Single(catalog.Medals).Value);
        Assert.True(title.UsesMedals);
        Assert.False(title.Concealed);
        Assert.False(title.HasUnsupportedConditions);
        Assert.False(title.HasUnsupportedBonuses);
        Assert.False(title.RequiresPartner);
    }

    /// <summary>The native modern flag controls medal use independently of ordinary title type.</summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void ModernMedalFlagUsesNativeBooleanValues(string value, bool expected)
    {
        var sample = new Sample(true);
        sample.Definitions[0][5] = value;
        Assert.Equal(expected, sample.Load().Titles[400001].UsesMedals);
    }

    /// <summary>Invalid boolean cells fail rather than silently enabling title claims.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void InvalidBooleanFails(int column)
    {
        var sample = new Sample(true);
        sample.Definitions[0][column] = "2";
        Assert.Throws<InvalidDataException>(() => sample.Load());
    }

    /// <summary>The configured schema must match the workbook's actual eligibility column.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MismatchedSchemaFails(bool modern)
    {
        var sample = new Sample(modern);
        var opposite = modern ? TitleCatalogSchema.Legacy : TitleCatalogSchema.MedalFlag;
        Assert.Throws<InvalidDataException>(() => sample.Load(schema: opposite));
    }

    /// <summary>Acquisition-only conditions remain separate from partner restrictions on equipment.</summary>
    [Fact]
    public void NativeDateIsParsedWithoutPartnerRestriction()
    {
        var sample = new Sample(true);
        sample.Definitions[0][4] = "2012/03/28/16:35~2012/03/28/16:50";
        var title = sample.Load().Titles[400001];
        Assert.False(title.HasUnsupportedConditions);
        Assert.NotNull(title.Window);
        Assert.Equal(sample.Definitions[0][4], title.AcquisitionInterval);
        Assert.False(title.RequiresPartner);
    }

    /// <summary>The known partner path and modern conditional type cannot enter ordinary acquisition.</summary>
    [Theory]
    [InlineData(false, "11", true, false)]
    [InlineData(true, "3", false, true)]
    public void UnsupportedNativeTypesRetainTheirEligibility(bool modern, string type, bool partner, bool medals)
    {
        var sample = new Sample(modern);
        sample.Definitions[0][2] = type;
        var title = sample.Load().Titles[400001];
        Assert.True(title.HasUnsupportedConditions);
        Assert.Equal(partner, title.RequiresPartner);
        Assert.Equal(medals, title.UsesMedals);
    }

    /// <summary>The specialized partner title remains identifiable when its modern type changes.</summary>
    [Fact]
    public void PartnerIdentityIsIndependentOfModernType()
    {
        var sample = new Sample(true);
        sample.Definitions[0][0] = sample.Requirements[0][0] = "400080";
        Assert.True(sample.Load().Titles[400080].RequiresPartner);
    }

    /// <summary>A medal bonus without a native quantity remains closed instead of inventing a credit amount.</summary>
    [Fact]
    public void MedalBonusWithoutQuantityIsExplicitlyUnsupported()
    {
        var sample = new Sample(false);
        sample.Bonuses.Add(["400001", "0", "30007"]);
        Assert.True(sample.Load().Titles[400001].HasUnsupportedBonuses);
    }

    /// <summary>A native bonus set resolves every stock into item duration terms without relying on sale availability.</summary>
    [Fact]
    public void BonusStockSetResolvesCompleteGrantTerms()
    {
        var sample = new Sample(false);
        sample.Bonuses.Add(["400001", "2", "2001124"]);
        sample.StockSets.Add(["2001124", "0", "1200100", "1200101"]);
        sample.Stocks.Add(["1200100", "400950", "1", "7"]);
        sample.Stocks.Add(["1200101", "400951", "1", "30"]);
        sample.Items.Add(["400950", "day", "360"]);
        sample.Items.Add(["400951", "day", "360"]);
        var title = sample.Load().Titles[400001];
        Assert.False(title.HasUnsupportedBonuses);
        Assert.Equal(new[]
        {
            new InventoryGrantTerm(400950, InventoryGrantKind.Days, 7, 360, false),
            new InventoryGrantTerm(400951, InventoryGrantKind.Days, 30, 360, false),
        }, title.BonusGrants);
        sample.StockSets[0][3] = "999999";
        Assert.Throws<InvalidDataException>(() => sample.Load());
    }

    /// <summary>Each title must resolve to exactly one stock and one item.</summary>
    [Theory]
    [InlineData("missing-stock")]
    [InlineData("duplicate-stock")]
    [InlineData("missing-item")]
    [InlineData("duplicate-item")]
    [InlineData("duplicate-title")]
    public void BrokenGrantJoinFails(string mutation)
    {
        var sample = new Sample(false);
        switch (mutation)
        {
            case "missing-stock": sample.Stocks.Clear(); break;
            case "duplicate-stock": sample.Stocks.Add((string[])sample.Stocks[0].Clone()); break;
            case "missing-item": sample.Items.Clear(); break;
            case "duplicate-item": sample.Items.Add((string[])sample.Items[0].Clone()); break;
            case "duplicate-title": sample.Definitions.Add((string[])sample.Definitions[0].Clone()); break;
        }
        Assert.Throws<InvalidDataException>(() => sample.Load());
    }

    /// <summary>An unrelated duplicate sale row in the KR812 stock table does not invalidate title data.</summary>
    [Fact]
    public void DuplicateUnreferencedStockIsOutsideTitleJoin()
    {
        var sample = new Sample(false);
        sample.Stocks.Add(["1002234", "100700", "1", "7"]);
        sample.Stocks.Add(["1002234", "100701", "1", "7"]);
        Assert.Single(sample.Load().Titles);
    }

    /// <summary>Medal balances are keyed by the client's mode-times-ten-thousand combined identity.</summary>
    [Theory]
    [InlineData("duplicate-definition")]
    [InlineData("wrong-components")]
    [InlineData("unknown-requirement")]
    [InlineData("duplicate-requirement")]
    [InlineData("negative-cost")]
    [InlineData("zero-cost")]
    [InlineData("missing-title")]
    public void InvalidMedalRelationFails(string mutation)
    {
        var sample = new Sample(false);
        switch (mutation)
        {
            case "duplicate-definition": sample.Medals.Add((string[])sample.Medals[0].Clone()); break;
            case "wrong-components": sample.Medals[0][0] = "4"; break;
            case "unknown-requirement": sample.Requirements[0][1] = "30008"; break;
            case "duplicate-requirement": sample.Requirements.Add((string[])sample.Requirements[0].Clone()); break;
            case "negative-cost": sample.Requirements[0][2] = "-1"; break;
            case "zero-cost": sample.Requirements[0][2] = "0"; break;
            case "missing-title": sample.Requirements[0][0] = "400002"; break;
        }
        Assert.Throws<InvalidDataException>(() => sample.Load());
    }

    /// <summary>Native all-zero placeholders are not interpreted as spendable medal conditions.</summary>
    [Fact]
    public void ZeroMedalPlaceholderIsIgnored()
    {
        var sample = new Sample(false);
        sample.Requirements.Clear();
        sample.Requirements.Add(["400001", "0", "0"]);
        sample.Medals.Add(["0", "0", "0"]);
        Assert.Empty(sample.Load().Titles[400001].Requirements);
    }

    /// <summary>Configured XML bounds fail before exposing a partially loaded catalogue.</summary>
    [Theory]
    [InlineData("bytes")]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("cell")]
    public void WorkbookLimitsAreEnforced(string limit)
    {
        var sample = new Sample(false);
        Assert.Throws<InvalidDataException>(() => sample.Load(maxBytes: limit == "bytes" ? 1 : 1_000_000,
            maxRows: limit == "rows" ? 1 : 100, maxColumns: limit == "columns" ? 3 : 32,
            maxCellCharacters: limit == "cell" ? 3 : 1000));
    }

    /// <summary>Sparse indices cannot request huge row or nonempty column allocations.</summary>
    [Theory]
    [InlineData("row")]
    [InlineData("cell")]
    [InlineData("non-increasing")]
    public void SparseIndicesAreBoundedAndIncreasing(string target)
    {
        var sample = new Sample(false);
        var document = sample.TitleDocument();
        var row = document.Descendants(Sample.Ns + "Row").ElementAt(2);
        if (target == "row") row.SetAttributeValue(Sample.Ns + "Index", int.MaxValue);
        else row.Elements().ElementAt(target == "cell" ? 0 : 1)
            .SetAttributeValue(Sample.Ns + "Index", target == "cell" ? int.MaxValue : 1);
        Assert.Throws<InvalidDataException>(() => sample.Load(titleOverride: document));
    }

    /// <summary>Empty cells retain their original column positions instead of shifting subsequent values.</summary>
    [Fact]
    public void EmptyAndSparseCellsDoNotShiftMedalFlag()
    {
        var sample = new Sample(true);
        var document = sample.TitleDocument();
        var row = document.Descendants(Sample.Ns + "Row").ElementAt(2);
        var cells = row.Elements().ToArray();
        cells[3].Remove();
        cells[4].SetAttributeValue(Sample.Ns + "Index", 5);
        var title = sample.Load(titleOverride: document).Titles[400001];
        Assert.False(title.Concealed);
        Assert.True(title.UsesMedals);
    }

    /// <summary>Missing required sheets and DTD declarations are rejected by the XML boundary.</summary>
    [Fact]
    public void MissingSheetAndDtdAreRejected()
    {
        var sample = new Sample(false);
        var document = sample.TitleDocument();
        document.Root!.Elements().Last().Remove();
        Assert.Throws<InvalidDataException>(() => sample.Load(titleOverride: document));
        document = sample.TitleDocument();
        document.AddFirst(new XDocumentType("Workbook", null, null, "<!ENTITY value 'ignored'>"));
        Assert.Throws<XmlException>(() => sample.Load(titleOverride: document));
    }

    private sealed class Sample(bool modern)
    {
        internal static readonly XNamespace Ns = "urn:schemas-microsoft-com:office:spreadsheet";
        internal List<string[]> Definitions { get; } = [modern
            ? ["400001", "1200001", "0", "0", "0", "1"]
            : ["400001", "1200001", "0", "0", "0"]];
        internal List<string[]> Requirements { get; } = [["400001", "30007", "20"]];
        internal List<string[]> Medals { get; } = [["3", "7", "30007"]];
        internal List<string[]> Bonuses { get; } = [];
        internal List<string[]> StockSets { get; } = [];
        internal List<string[]> Stocks { get; } = [["1200001", "400901", "9", "-1"]];
        internal List<string[]> Items { get; } = [["400901", "day", "360"]];

        internal TitleCatalog Load(TitleCatalogSchema? schema = null, int maxBytes = 1_000_000,
            int maxRows = 100, int maxColumns = 32, int maxCellCharacters = 1000, XDocument? titleOverride = null)
        {
            var store = new WorkbookStore(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Config/BF_title.xml"] = Encode(titleOverride ?? TitleDocument()),
                ["Config/ShopItem2.xml"] = Encode(new XDocument(new XElement(Ns + "Workbook",
                    Sheet("Stock", ["stockId", "itemId", "type", "duration"], Stocks, false),
                    Sheet("ShopItem", ["itemId", "durationType", "maxDuration"], Items, false),
                    Sheet("StockSet", ["stockSetId", "lucci", "stockId01", "stockId02"], StockSets, false)))),
            });
            return TitleCatalog.Load(store, "Config/BF_title.xml", "Config/ShopItem2.xml",
                schema ?? (modern ? TitleCatalogSchema.MedalFlag : TitleCatalogSchema.Legacy),
                maxBytes, maxRows, maxColumns, maxCellCharacters);
        }

        internal XDocument TitleDocument() => new(new XElement(Ns + "Workbook",
            Sheet("title_dec", modern ? ["title_id", "Stock_id", "type", "conceal", "gettitle_date", "use_medal"]
                : ["title_id", "Stock_id", "type", "conceal", "gettitle_date"], Definitions, true),
            Sheet("title", ["title_id", "medal_id", "count"], Requirements, true),
            Sheet("medal", ["mode", "medalid", "Medal_id"], Medals, true),
            Sheet("bonus", ["title_id", "bonustype", "bonusnum"], Bonuses, true)));

        private static XElement Sheet(string name, string[] header, IEnumerable<string[]> rows, bool description) =>
            new(Ns + "Worksheet", new XAttribute(Ns + "Name", name), new XElement(Ns + "Table",
                description ? Row(["Native description"]) : null, Row(header), rows.Select(Row)));
        private static XElement Row(string[] values) => new(Ns + "Row", values.Select(value =>
            new XElement(Ns + "Cell", new XElement(Ns + "Data", new XAttribute(Ns + "Type", "String"), value))));
        private static byte[] Encode(XDocument document) => Encoding.UTF8.GetBytes(document.ToString());
    }

    private sealed class WorkbookStore(IReadOnlyDictionary<string, byte[]> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);
        public byte[] Read(string path) => files.TryGetValue(path, out var bytes) ? bytes
            : throw new FileNotFoundException("The synthetic title workbook is missing.", path);
    }
}
