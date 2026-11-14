using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins which reward workbooks validate and which refusal each defect produces, including the order in which the
/// StockSet and Stock worksheets are judged, so the reader's parsing strategy can change without changing a verdict.
/// </summary>
public sealed class LotteryCatalogReaderTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";
    private const string StockHeader = "stockId,itemId,duration,type";
    private const string SetHeader = "stockSetId,Lucci,stockId01,stockId02,stockId03,stockId04,stockId05,stockId06,stockId07,stockId08,stockId09,stockId10";

    private static LotteryItem Timed() => new() { ItemId = 100207, DisplayId = 1000920, Days = 3 };

    private static LotteryItem Bundle() => new()
    {
        DisplayId = 2000001, Components = [new() { ItemId = 100207, DisplayId = 1000920, Days = 3 }, new() { ItemId = 100208, DisplayId = 1000921, Quantity = 5, Stackable = true }],
    };

    private static LotteryItem Lucci() => new() { DisplayId = 2000002, Lucci = 500 };

    private static string Stock(params string[] rows) => Sheet("Stock", StockHeader, rows);

    private static string StockRows => Stock("1000920,100207,3,1", "1000921,100208,5,1", "999,1,1,1");

    private static string Sets(params string[] rows) => Sheet("StockSet", SetHeader, rows);

    /// <summary>Timed items, stack quantities, component bundles and currency sets resolve in either worksheet order.</summary>
    [Fact]
    public void ValidWorkbooksPass()
    {
        var sets = Sets("2000001,0,1000921,1000920,0,0,0,0,0,0,0,0", "2000002,500,,,,,,,,,,");
        Validate(Workbook(StockRows + sets), [Timed(), Bundle(), Lucci()]);
        Validate(Workbook(sets + StockRows), [Timed(), Bundle(), Lucci()]);
        Validate(Workbook(StockRows), [Timed()]);
    }

    /// <summary>Every verdict keeps its message.</summary>
    [Theory]
    [InlineData("{sets}", "no Stock worksheet")]
    [InlineData("<Worksheet ss:Name=\"Stock\"></Worksheet>{sets}", "Stock worksheet has no table")]
    [InlineData("<Worksheet ss:Name=\"Stock\"><Table/></Worksheet>{sets}", "Stock worksheet exceeds its row bound")]
    [InlineData("{stock}", "no StockSet worksheet")]
    [InlineData("{stock}<Worksheet ss:Name=\"StockSet\"></Worksheet>", "StockSet worksheet has no table")]
    [InlineData("{stock}<Worksheet ss:Name=\"StockSet\"><Table/></Worksheet>", "StockSet worksheet exceeds its row bound")]
    [InlineData("{stock}{sets-missing-one}", "A reward StockSet is absent")]
    [InlineData("{stock}{sets-duplicate}", "Duplicate reward StockSet 2000001")]
    [InlineData("{stock}{sets-wrong-lucci}", "Reward StockSet 2000002 does not match")]
    [InlineData("{sets}{stock-duplicate}", "Reward-card Stock 1000920 has malformed or duplicate native terms")]
    [InlineData("{sets}{stock-malformed}", "Reward-card Stock 1000920 has malformed or duplicate native terms")]
    [InlineData("{sets}{stock-wrong-item}", "Reward display 1000920 must resolve")]
    [InlineData("{sets}{stock-no-type}", "lacks native identity or duration columns")]
    // StockSet is judged first even when the Stock worksheet comes first and is also defective.
    [InlineData("{stock-duplicate}{sets-missing-one}", "A reward StockSet is absent")]
    [InlineData("{stock-malformed}", "no StockSet worksheet")]
    public void VerdictsKeepTheirMessages(string sheets, string message)
    {
        var xml = Workbook(sheets
            .Replace("{stock-duplicate}", Stock("1000920,100207,3,1", "1000920,100207,3,1", "1000921,100208,5,1"), StringComparison.Ordinal)
            .Replace("{stock-malformed}", Stock("1000920,x,3,1", "1000921,100208,5,1"), StringComparison.Ordinal)
            .Replace("{stock-wrong-item}", Stock("1000920,100209,3,1", "1000921,100208,5,1"), StringComparison.Ordinal)
            .Replace("{stock-no-type}", Sheet("Stock", "stockId,itemId,duration", ["1000920,100207,3"]), StringComparison.Ordinal)
            .Replace("{stock}", StockRows, StringComparison.Ordinal)
            .Replace("{sets-missing-one}", Sets("2000001,0,1000921,1000920,,,,,,,,"), StringComparison.Ordinal)
            .Replace("{sets-duplicate}", Sets("2000001,0,1000921,1000920,,,,,,,,", "2000001,0,1000921,1000920,,,,,,,,", "2000002,500,,,,,,,,,,"), StringComparison.Ordinal)
            .Replace("{sets-wrong-lucci}", Sets("2000001,0,1000921,1000920,,,,,,,,", "2000002,501,,,,,,,,,,"), StringComparison.Ordinal)
            .Replace("{sets}", Sets("2000001,0,1000921,1000920,,,,,,,,", "2000002,500,,,,,,,,,,"), StringComparison.Ordinal));
        var error = Assert.Throws<ConfigException>(() => Validate(xml, [Timed(), Bundle(), Lucci()]));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Unrequested rows are not judged, so their defects never fail a workbook.</summary>
    [Fact]
    public void UnrequestedRowsAreNotJudged() =>
        Validate(Workbook(Stock("1000920,100207,3,1", "555,x,y,z", "555,1,1,1")), [Timed()]);

    /// <summary>A repeated worksheet, a non-numeric index and malformed XML anywhere keep their exception types.</summary>
    [Fact]
    public void StructuralFailuresKeepTheirTypes()
    {
        Assert.Throws<InvalidOperationException>(() => Validate(Workbook(StockRows + StockRows), [Timed()]));
        Assert.Throws<FormatException>(() => Validate(Workbook(Sheet("Stock", StockHeader, []).Replace("</Table>",
            "<Row><Cell ss:Index=\"x\"/></Row></Table>", StringComparison.Ordinal)), [Timed()]));
        Assert.Throws<System.Xml.XmlException>(() => Validate(Workbook(StockRows) + "<x/>", [Timed()]));
        Assert.Throws<System.Xml.XmlException>(() => Validate(Workbook(StockRows + "<Worksheet>"), [Timed()]));
    }

    /// <summary>The row bound is judged before a row's defect, so rows after a defect still count toward it.</summary>
    [Fact]
    public void RowsAfterADefectStillCountTowardTheBound()
    {
        var sheet = Stock("1000920,x,3,1").Replace("</Table>", string.Concat(Enumerable.Repeat("<Row/>", 65535)) + "</Table>", StringComparison.Ordinal);
        var error = Assert.Throws<ConfigException>(() => Validate(Workbook(sheet), [Timed()]));
        Assert.Contains("Stock worksheet exceeds its row bound", error.Message, StringComparison.Ordinal);
        var malformed = Assert.Throws<ConfigException>(() => Validate(Workbook(sheet.Replace("<Row/></Table>", "</Table>", StringComparison.Ordinal)), [Timed()]));
        Assert.Contains("malformed or duplicate native terms", malformed.Message, StringComparison.Ordinal);
    }

    /// <summary>A workbook over 64 MiB is refused before it is parsed.</summary>
    [Fact]
    public void OversizedWorkbookIsRefused() =>
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(new byte[64 * 1024 * 1024 + 1], [Timed()]));

    /// <summary>
    /// A store with compiled client data keeps that a workbook passed for a set of prizes: the same prizes are not
    /// validated again, any prize field the validation reads makes it another validation, and a refusal is never kept.
    /// </summary>
    [Fact]
    public void APassedValidationIsKeptPerPrizeSetAndARefusalNever()
    {
        var store = new CompilingStore(Encoding.UTF8.GetBytes(Workbook(StockRows)));
        LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [Timed()]);
        LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [Timed()]);
        Assert.Equal(1, store.Compiles);
        Assert.Equal([1], Assert.Single(store.Kept).Value);

        // Three days in the workbook, four configured: other inputs, validated, refused, not kept.
        var other = Timed();
        other.Days = 4;
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [other]));
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [other]));
        Assert.Equal(3, store.Compiles);
        Assert.Single(store.Kept);

        // A component is part of the inputs as well.
        var bundle = Bundle();
        var sets = Sets("2000001,0,1000921,1000920,0,0,0,0,0,0,0,0");
        var bundles = new CompilingStore(Encoding.UTF8.GetBytes(Workbook(StockRows + sets)));
        LotteryCatalog.Validate(bundles, "Config/ShopItem2.xml", [bundle]);
        bundle.Components[1].Quantity = 6;
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(bundles, "Config/ShopItem2.xml", [bundle]));
    }

    /// <summary>The kept fact is one byte and reads back only as a passed validation.</summary>
    [Fact]
    public void TheKeptValidationReadsBackEqual()
    {
        using var section = new MemoryStream();
        LotteryCatalog.ValidatedLottery.Instance.Write(true, section);
        Assert.Equal([1], section.ToArray());
        section.Position = 0;
        Assert.True(LotteryCatalog.ValidatedLottery.Instance.Read(section));
        Assert.Throws<InvalidDataException>(() => LotteryCatalog.ValidatedLottery.Instance.Read(new MemoryStream([0])));
        Assert.ThrowsAny<Exception>(() => LotteryCatalog.ValidatedLottery.Instance.Read(new MemoryStream()));
    }

    /// <summary>The tutorial stages read back equal: both tables, keys in their own letter case and found without it.</summary>
    [Fact]
    public void CompiledTutorialStagesReadBackEqual()
    {
        var stages = new TutorialStages(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["rel_Tutorial02"] = 2, ["Rel_tutorial01"] = 1 },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["rel_Tutorial02"] = 2000123 });
        using var section = new MemoryStream();
        CompiledTutorialStages.Instance.Write(stages, section);
        section.Position = 0;
        var read = CompiledTutorialStages.Instance.Read(section);
        Assert.Equal(section.Length, section.Position);
        Assert.Equal(stages.Numbers.OrderBy(pair => pair.Key, StringComparer.Ordinal), read.Numbers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(stages.Gifts, read.Gifts);
        Assert.Equal(1, read.Numbers["REL_TUTORIAL01"]);

        var empty = new TutorialStages(new Dictionary<string, int>(), new Dictionary<string, int>());
        using var none = new MemoryStream();
        CompiledTutorialStages.Instance.Write(empty, none);
        none.Position = 0;
        Assert.Empty(CompiledTutorialStages.Instance.Read(none).Numbers);
        Assert.ThrowsAny<Exception>(() => CompiledTutorialStages.Instance.Read(new MemoryStream([9, 0, 0, 0, 0, 0, 0, 0])));
        // One table instead of two, and a key that differs from another only in letter case.
        byte[] Section(KeyValuePair<string, int>[][] tables) => MemoryPack.MemoryPackSerializer.Serialize(tables);
        Assert.Throws<InvalidDataException>(() => CompiledTutorialStages.Instance.Read(new MemoryStream(Section([[]]))));
        Assert.Throws<InvalidDataException>(() => CompiledTutorialStages.Instance.Read(
            new MemoryStream(Section([[new("a", 1), new("A", 2)], []]))));
    }

    /// <summary>A store that keeps compiled catalogs in memory by name and inputs, as the build's store keeps them in its file.</summary>
    private sealed class CompilingStore(byte[] workbook) : BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore,
        BubbleFighter.Fantasy.Contracts.ClientData.ICompiledClientData
    {
        internal Dictionary<(string Name, string Inputs), byte[]> Kept { get; } = [];

        internal int Compiles { get; private set; }

        public bool Exists(string path) => true;

        public byte[] Read(string path) => workbook;

        public T Load<T>(BubbleFighter.Fantasy.Contracts.ClientData.ICompiledCatalog<T> catalog, string inputs,
            Func<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore, T> compile)
        {
            if (Kept.TryGetValue((catalog.Name, inputs), out var bytes))
                return catalog.Read(new MemoryStream(bytes));
            Compiles++;
            var value = compile(this);
            using var section = new MemoryStream();
            catalog.Write(value, section);
            Kept[(catalog.Name, inputs)] = section.ToArray();
            return value;
        }
    }

    private static void Validate(string xml, IReadOnlyList<LotteryItem> prizes) =>
        LotteryCatalog.Validate(Encoding.UTF8.GetBytes(xml), prizes);

    private static string Sheet(string name, string header, string[] rows) =>
        $"<Worksheet ss:Name=\"{name}\"><Table>{Row(header)}{string.Concat(rows.Select(Row))}</Table></Worksheet>";

    private static string Row(string values) =>
        "<Row>" + string.Concat(values.Split(',').Select(value => value.Length == 0 ? "<Cell/>" : $"<Cell><Data>{value}</Data></Cell>")) + "</Row>";

    private static string Workbook(string sheets) =>
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\">{sheets}</Workbook>";
}
