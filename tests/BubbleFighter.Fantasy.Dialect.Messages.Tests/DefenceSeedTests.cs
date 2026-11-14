using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the defence table distribution and the local-roll resolution it reproduces.</summary>
public sealed class DefenceSeedTests
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>Every percent of the roll appears floor or ceil of 1000/count times, and only those percents appear.</summary>
    [Theory]
    [InlineData(2, 8)]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    [InlineData(5, 12)]
    [InlineData(0, 3)]
    public void TableHoldsEachPercentEquallyOften(int low, int high)
    {
        var range = new DefencePercentRange(low, high);
        var table = DefenceSeedTable.Build(range, Seed(1));
        Assert.Equal(DefenceSeedTable.Length, table.Length);
        var counts = table.GroupBy(value => (int)value).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(Enumerable.Range(range.First, range.Count), counts.Keys.Order());
        var floor = DefenceSeedTable.Length / range.Count;
        Assert.All(counts.Values, count => Assert.InRange(count, floor, floor + 1));
    }

    /// <summary>The local roll returns lo when both ends agree, lo..hi-1 when hi exceeds lo, and hi+1..lo otherwise.</summary>
    [Fact]
    public void RangeMatchesTheClientRandomEndpoints()
    {
        Assert.Equal((4, 1), (new DefencePercentRange(4, 4).First, new DefencePercentRange(4, 4).Count));
        Assert.Equal((2, 6), (new DefencePercentRange(2, 8).First, new DefencePercentRange(2, 8).Count));
        Assert.Equal((5, 3), (new DefencePercentRange(7, 4).First, new DefencePercentRange(7, 4).Count));
        var descending = DefenceSeedTable.Build(new DefencePercentRange(7, 4), Seed(2));
        Assert.Equal([5, 6, 7], descending.Distinct().Order().Select(value => (int)value));
    }

    /// <summary>The same seed gives the same bytes; another seed reorders them.</summary>
    [Fact]
    public void TableIsAFunctionOfItsSeed()
    {
        var range = new DefencePercentRange(2, 8);
        Assert.Equal(DefenceSeedTable.Build(range, Seed(3)), DefenceSeedTable.Build(range, Seed(3)));
        Assert.NotEqual(DefenceSeedTable.Build(range, Seed(3)), DefenceSeedTable.Build(range, Seed(4)));
    }

    /// <summary>A roll whose percents do not fit one byte has no native table.</summary>
    [Fact]
    public void PercentsOutsideAByteAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => DefenceSeedTable.Build(new DefencePercentRange(-1, 3), Seed(5)));
        Assert.Throws<InvalidOperationException>(() => DefenceSeedTable.Build(new DefencePercentRange(250, 300), Seed(5)));
    }

    /// <summary>
    /// Ordinary rows sum over distinct worn items; an empty or unknown worn set rolls zero. The legacy product keeps
    /// the float's error: 0.02f is 0.0199999995529651, so (double)0.02f * 100.0 truncates to 1, not 2.
    /// </summary>
    [Fact]
    public void OrdinaryRowsSumOverTheWornSet()
    {
        var catalog = Catalog("kr-812");
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(null));
        Assert.Equal(new DefencePercentRange(1, 7), catalog.Range(Worn(100072)));
        Assert.Equal(new DefencePercentRange(3, 10), catalog.Range(Worn(100072, 100204)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(999999)));
        Assert.Equal(new DefencePercentRange(2, 8), Catalog("kr-1256").Range(Worn(100072)));
    }

    /// <summary>The RC pair joins once the worn count reaches the threshold, which defaults to five and an item can assign.</summary>
    [Fact]
    public void RcPairNeedsItsCount()
    {
        var catalog = Catalog("kr-812");
        Assert.Equal(new DefencePercentRange(4, 11), catalog.Range(Worn(100203, 100204, 100205, 100206)));
        Assert.Equal(new DefencePercentRange(9, 17), catalog.Range(Worn(100203, 100204, 100205, 100206, 100207)));
        // 100208 assigns a threshold of 2, so two counted items already unlock the pair.
        Assert.Equal(new DefencePercentRange(5, 5), catalog.Range(Worn(100206, 100208)));
    }

    /// <summary>KR and BB bonuses need an owner item as well as their count; kr-1256 tests neither owner.</summary>
    [Fact]
    public void SetBonusesNeedTheirOwnerItem()
    {
        var legacy = Catalog("kr-812");
        Assert.Equal(new DefencePercentRange(0, 0), legacy.Range(Worn(100230, 100231)));
        Assert.Equal(new DefencePercentRange(2, 0), legacy.Range(Worn(100230, 100231, 100237)));
        Assert.Equal(new DefencePercentRange(1, 5), legacy.Range(Worn(100240, 100241)));
        Assert.Equal(new DefencePercentRange(0, 0), Catalog("kr-1256").Range(Worn(100230, 100231, 100237)));
        Assert.Equal(new DefencePercentRange(0, 0), Catalog("kr-1256").Range(Worn(100240, 100241)));
    }

    /// <summary>cn-324 has its own owner lists: 190044 and 100712 unlock nothing there.</summary>
    [Fact]
    public void Cn324StatesItsOwnOwners()
    {
        var catalog = Catalog("cn-324");
        Assert.Equal(new DefencePercentRange(2, 0), catalog.Range(Worn(100230, 100231, 100237)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(100230, 100231, 190044)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(100241, 100712)));
    }

    /// <summary>Characters whose rows the client skips add nothing; another character's rows apply first.</summary>
    [Fact]
    public void SkippedCharactersApplyNoRows()
    {
        var catalog = Catalog("kr-812");
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(300006)));
        Assert.Equal(new DefencePercentRange(0, 1), catalog.Range(Worn(300011)));
    }

    /// <summary>
    /// 0.29 is 0.28999999165534973 as a float: the legacy double product truncates to 28, the modern single product
    /// rounds to 29.0f first.
    /// </summary>
    [Fact]
    public void EndpointPrecisionFollowsTheBuild()
    {
        Assert.Equal(new DefencePercentRange(28, 28), Catalog("kr-812").Range(Worn(100300)));
        Assert.Equal(new DefencePercentRange(29, 29), Catalog("kr-1256").Range(Worn(100300)));
    }

    /// <summary>The CN2014 consumers preserve the double product and apply their own RC, KR and BB owner rules.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void Cn2014DefenceUsesItsAuditedPrecisionAndSets(string build)
    {
        var catalog = Catalog(build);
        Assert.Equal(new DefencePercentRange(28, 28), catalog.Range(Worn(100300)));
        Assert.Equal(new DefencePercentRange(1, 7), catalog.Range(Worn(100072)));
        Assert.Equal(new DefencePercentRange(4, 11), catalog.Range(Worn(100203, 100204, 100205, 100206)));
        Assert.Equal(new DefencePercentRange(9, 17), catalog.Range(Worn(100203, 100204, 100205, 100206, 100207)));
        Assert.Equal(new DefencePercentRange(5, 5), catalog.Range(Worn(100206, 100208)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(100230, 100231)));
        Assert.Equal(new DefencePercentRange(2, 0), catalog.Range(Worn(100230, 100231, 100237)));
        Assert.Equal(new DefencePercentRange(2, 0), catalog.Range(Worn(100230, 100231, 190044)));
        Assert.Equal(new DefencePercentRange(1, 5), catalog.Range(Worn(100240, 100241)));
        Assert.Equal(new DefencePercentRange(1, 5), catalog.Range(Worn(100241, 100712)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(300006)));
    }

    /// <summary>The CN and TW modern clients keep KR/BB owner bonuses and single endpoints independently of kr-1256.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernDefenceUsesItsOwnSetFamilies(string build)
    {
        var catalog = Catalog(build);
        Assert.Equal(new DefencePercentRange(29, 29), catalog.Range(Worn(100300)));
        Assert.Equal(new DefencePercentRange(2, 8), catalog.Range(Worn(100072)));
        Assert.Equal(new DefencePercentRange(5, 5), catalog.Range(Worn(100206, 100208)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(100230, 100231)));
        Assert.Equal(new DefencePercentRange(3, 0), catalog.Range(Worn(100230, 100231, 100237)));
        Assert.Equal(new DefencePercentRange(3, 0), catalog.Range(Worn(100230, 100231, 190044)));
        Assert.Equal(new DefencePercentRange(2, 5), catalog.Range(Worn(100240, 100241)));
        Assert.Equal(new DefencePercentRange(2, 5), catalog.Range(Worn(100241, 100712)));
        Assert.Equal(new DefencePercentRange(0, 0), catalog.Range(Worn(300006)));
        Assert.Equal(new DefencePercentRange(1, 2), catalog.Range(Worn(300011)));
    }

    /// <summary>Each build preserves the x87 endpoint product, including the software device creation fallback.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void LegacyEndpointPrecisionFollowsItsOwnDeviceFlags(string build)
    {
        Assert.Equal(new DefencePercentRange(28, 28), Catalog(build).Range(Worn(100300)));
        Assert.Equal(new DefencePercentRange(1, 7), Catalog(build).Range(Worn(100072)));
    }

    /// <summary>Every representable range retains the target CDF to within one entry of its finite table.</summary>
    [Fact]
    public void QuantizationErrorIsBoundedAcrossAllByteRanges()
    {
        for (var count = 1; count <= 256; count++)
        {
            var range = new DefencePercentRange(0, count);
            var table = DefenceSeedTable.Build(range, Seed((byte)(count - 1)));
            var cumulative = 0;
            for (var percent = 0; percent < count; percent++)
            {
                cumulative += table.Count(value => value == percent);
                Assert.InRange(Math.Abs(cumulative / 1000.0 - (percent + 1.0) / count), 0, 0.001 + 1e-12);
            }
        }
    }

    /// <summary>A channel-bound or non-numeric defence row fails when the catalog loads, never as a silent zero.</summary>
    [Fact]
    public void UnprovenInputsFailAtLoad()
    {
        Assert.Throws<ConfigException>(() => Load("kr-812", Workbook(("100072", "damagedDecRatioMin", "0.02", "3"))));
        Assert.Throws<ConfigException>(() => Load("kr-812", Workbook(("100072", "damagedDecRatioMax", "high", "0"))));
    }

    /// <summary>Rows of a family the build does not register are ignored: kr-1256 never sums the KR or BB pairs.</summary>
    [Fact]
    public void UnregisteredFamiliesAddNothing()
    {
        var modern = Load("kr-1256", Workbook(("100241", "BBdamagedDecRatioMin", "0.4", "0"),
            ("100241", "BBdamagedDecRatioMax", "0.4", "0"), ("100240", "BBequipValue", "1", "0")));
        Assert.Equal(new DefencePercentRange(0, 0), modern.Range(Worn(100240, 100241)));
    }

    /// <summary>The bytes depend on the round and the actor only, and their percents match the actor's roll.</summary>
    [Fact]
    public void TableBytesDependOnRoundAndActorOnly()
    {
        var catalog = Catalog("kr-812");
        var equipment = Worn(100072);
        var table = catalog.Table(41, 7, 1_000_000, equipment);
        Assert.Equal(table, catalog.Table(41, 7, 1_000_000, Worn(100072)));
        Assert.NotEqual(table, catalog.Table(41, 8, 1_000_000, equipment));
        Assert.NotEqual(table, catalog.Table(41, 7, 1_100_000, equipment));
        Assert.Equal(Enumerable.Range(1, 6), table.Distinct().Order().Select(value => (int)value));
    }

    internal static DefenceSeedCatalog Catalog(string build) => Load(build, Workbook(Rows));

    internal static string ItemWorkbook() => Workbook(Rows);

    internal static MatchSeedEquipment Worn(params int[] items) => new(1, items);

    private static DefenceSeedCatalog Load(string build, string workbook) =>
        DefenceSeedCatalog.Load(new Store(Encoding.UTF8.GetBytes(workbook)), Tables, build, "Config/ShopItem2.xml", 1 << 20,
            "defence_workbook_max_bytes");

    private static byte[] Seed(byte value) => Enumerable.Repeat(value, 32).ToArray();

    // Shipped-shaped rows: 100072 is an ordinary item, 100203-100207 the RC set (100206 carries the pair and the
    // threshold; the pair is raised from the shipped 0.01 so it survives truncation), 100208 a fixture threshold
    // override, 100230/100231 KR counts, 100240/100241 BB counts. Expected percents come from a float32 reference.
    private static readonly (string Item, string Name, string Value, string Channel)[] Rows =
    [
        ("100072", "damagedDecRatioMin", "0.02", "0"), ("100072", "damagedDecRatioMax", "0.08", "0"),
        ("100203", "damagedDecRatioMin", "0.02", "0"), ("100203", "damagedDecRatioMax", "0.06", "0"),
        ("100203", "RCequipValue", "1", "0"),
        ("100204", "damagedDecRatioMin", "0.02", "0"), ("100204", "damagedDecRatioMax", "0.03", "0"),
        ("100204", "RCequipValue", "1", "0"),
        ("100205", "damagedDecRatioMin", "0.01", "0"), ("100205", "damagedDecRatioMax", "0.03", "0"),
        ("100205", "RCequipValue", "1", "0"),
        ("100206", "RCdamagedDecRatioMin", "0.05", "0"), ("100206", "RCdamagedDecRatioMax", "0.05", "0"),
        ("100206", "RCequipValue", "1", "0"), ("100206", "RCequipCheckValue", "5", "0"),
        ("100207", "RCequipValue", "1", "0"),
        ("100208", "RCequipValue", "1", "0"), ("100208", "RCequipCheckValue", "2", ""),
        ("100230", "KRequipValue", "1", "0"), ("100230", "KRequipCheckValue", "2", "0"),
        ("100231", "KRequipValue", "1", "0"), ("100231", "KRdamagedDecRatioMin", "0.03", "0"),
        ("100240", "BBequipValue", "1", "0"), ("100240", "BBequipCheckValue", "2", "0"),
        ("100241", "BBequipValue", "1", "0"), ("100241", "BBdamagedDecRatioMin", "0.02", "0"),
        ("100241", "BBdamagedDecRatioMax", "0.05", "0"),
        ("100300", "damagedDecRatioMin", "0.29", "0"), ("100300", "damagedDecRatioMax", "0.29", "0"),
        ("300006", "damagedDecRatioMin", "0.5", "0"), ("300006", "damagedDecRatioMax", "0.5", "0"),
        ("300011", "damagedDecRatioMin", "0.01", "0"), ("300011", "damagedDecRatioMax", "0.02", "0"),
        ("100072", "criticalCancelRatio", "0.5", "0"),
    ];

    internal static string Workbook(params (string Item, string Name, string Value, string Channel)[] rows)
    {
        static string Cell(string text) => $"<Cell><Data ss:Type=\"String\">{text}</Data></Cell>";
        var body = new StringBuilder();
        body.Append("<Row>").Append(Cell("shopItemId")).Append(Cell("buffName")).Append(Cell("value"))
            .Append(Cell("channel")).Append(Cell("buffGroup")).Append("</Row>");
        foreach (var (item, name, value, channel) in rows)
            body.Append("<Row>").Append(Cell(item)).Append(Cell(name)).Append(Cell(value)).Append(Cell(channel))
                .Append(Cell("0")).Append("</Row>");
        return $"""
            <?xml version="1.0"?>
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
             <Worksheet ss:Name="ShopItemSpec"><Table>{body}</Table></Worksheet>
            </Workbook>
            """;
    }

    private sealed class Store(byte[] workbook) : IClientDataStore
    {
        public bool Exists(string path) => true;

        public byte[] Read(string path) => workbook;
    }
}
