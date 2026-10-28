using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the legacy seed authority's table bodies and its data-driven critical chance.</summary>
public sealed class SceneSeedAuthorityTests
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    /// <summary>The bodies are entity + two 32-word vectors, 1000 defence bytes and 32 cancellation words in every seeded build.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    public void TableBodiesHaveTheClientExpanderSizes(string build)
    {
        var authority = Authority(build, new Random(1));
        var critical = authority.Critical(1_100_000, 1.0);
        Assert.Equal(4 + 4 + 128 + 4 + 128, critical.Length);
        Assert.Equal(1_100_000, BitConverter.ToInt32(critical, 0));
        Assert.Equal(32, BitConverter.ToInt32(critical, 4));
        Assert.Equal(32, BitConverter.ToInt32(critical, 4 + 4 + 128));
        Assert.Equal(4 + 4 + 1000, authority.Defence(1_100_000).Length);
        Assert.Equal(4 + 4 + 128, authority.Cancellation(1_100_000).Length);
    }

    /// <summary>A certain chance sets exactly the 1000 bits the roll can read in each of the two tables; zero sets none.</summary>
    [Fact]
    public void ChanceBoundsSetOnlyTheReadableBits()
    {
        var authority = Authority("cn-324", new Random(2));
        Assert.Equal(2000, SetBits(authority.Critical(1_000_000, 1.0)));
        Assert.Equal(0, SetBits(authority.Critical(1_000_000, 0.0)));
    }

    /// <summary>The round-start chance sums draw, lowRank and the previous-round row of the build's table.</summary>
    [Fact]
    public void RoundStartChanceFollowsTheTableAndHistory()
    {
        var chances = Chances(Workbook(("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4")));
        Assert.Equal(0.013, chances.RoundStartChance(MatchPreviousRound.None), 6);
        Assert.Equal(0.013, chances.RoundStartChance(MatchPreviousRound.Won), 6);
        Assert.Equal(0.014, chances.RoundStartChance(MatchPreviousRound.Drew), 6);
        Assert.Equal(0.026, chances.RoundStartChance(MatchPreviousRound.Lost), 6);
        Assert.Equal(0.046, chances.RoundStartChance(MatchPreviousRound.LostInRow), 6);
    }

    /// <summary>A missing category or a defence chance with no proven byte scale fails at load.</summary>
    [Fact]
    public void IncompleteOrDefenceTablesAreRejected()
    {
        Assert.Throws<ConfigException>(() => Chances(Workbook(("prevWin", "0.7"))));
        var rows = new[] { "prevWin", "prevDraw", "prevLose", "prevLoseInRow", "win", "draw", "lose", "highRank", "midRank", "lowRank" }
            .Select(name => (name, "1")).ToArray();
        Assert.Throws<ConfigException>(() => Chances(Workbook(rows, defence: "5")));
    }

    /// <summary>A realistic chance still yields some critical bits, and every round draws new ones.</summary>
    [Fact]
    public void EachRequestDrawsFreshBits()
    {
        var authority = Authority("cn-324", new Random(3));
        var first = authority.Critical(1_000_000, 0.013);
        var second = authority.Critical(1_000_000, 0.013);
        Assert.InRange(SetBits(first), 1, 200);
        Assert.NotEqual(first, second);
    }

    private static SceneSeedAuthority Authority(string build, Random random)
    {
        var chances = Chances(Workbook(("prevWin", "0.7"), ("prevDraw", "0.8"), ("prevLose", "2"), ("prevLoseInRow", "4"),
            ("win", "0"), ("draw", "0.2"), ("lose", "1"), ("highRank", "0"), ("midRank", "0.1"), ("lowRank", "0.4")));
        return new SceneSeedAuthority(build + "/relay", build, Tables, 4, 5, 6, chances, 4, random);
    }

    private static int SetBits(byte[] critical)
    {
        var count = 0;
        foreach (var offset in new[] { 8, 4 + 4 + 128 + 4 })
            for (var word = 0; word < 32; word++)
                count += System.Numerics.BitOperations.PopCount(BitConverter.ToUInt32(critical, offset + 4 * word));
        return count;
    }

    private static CriticalHitChances Chances(string workbook) =>
        CriticalHitChances.Load(new Store(Encoding.UTF8.GetBytes(workbook)), "Config/CriticalHitTable.xml", "CriticalHitTable", 1 << 20);

    private static string Workbook(params (string Category, string Attack)[] rows) => Workbook(rows, "0");

    private static string Workbook((string Category, string Attack)[] rows, string defence)
    {
        static string Cell(string text) => $"<Cell><Data ss:Type=\"String\">{text}</Data></Cell>";
        var body = new StringBuilder();
        body.Append("<Row>").Append(Cell("CriticalHitTable:VertTable")).Append("</Row>");
        body.Append("<Row>").Append(Cell("[Field]")).Append(Cell("category")).Append(Cell("crtAttackFactor"))
            .Append(Cell("crtDefenceFactor")).Append("</Row>");
        body.Append("<Row>").Append(Cell("##Previous ROUND")).Append(Cell("##win / lose previous")).Append("</Row>");
        foreach (var (category, attack) in rows)
            body.Append("<Row>").Append(Cell("note")).Append(Cell(category)).Append(Cell(attack)).Append(Cell(defence)).Append("</Row>");
        return $"""
            <?xml version="1.0"?>
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
             <Worksheet ss:Name="CriticalHitTable"><Table>{body}</Table></Worksheet>
            </Workbook>
            """;
    }

    private sealed class Store(byte[] workbook) : IClientDataStore
    {
        public bool Exists(string path) => true;

        public byte[] Read(string path) => workbook;
    }
}
