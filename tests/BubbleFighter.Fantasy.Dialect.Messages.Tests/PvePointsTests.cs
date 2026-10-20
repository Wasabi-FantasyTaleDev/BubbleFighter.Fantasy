using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native cooperative score-unit, delta and ranking widths, excluding in-memory vtables.</summary>
public sealed class PvePointsTests
{
    /// <summary>The native report and point delta retain their widths across audited readers.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-406")]
    public void PointPacketsHavePinnedWidths(string build)
    {
        var tables = TestBuilds.All.Tables;
        var report = ScalarGolden.Write(tables.Resolve(build, "PvePangUnit"), new() { ["unit_index"] = 15 });
        Assert.Equal("0F000000", Convert.ToHexString(report));
        var delta = ScalarGolden.Write(tables.Resolve(build, "PveGamePoint"), new() { ["seat"] = 1, ["amount"] = 25 });
        Assert.Equal("010000000000000019000000", Convert.ToHexString(delta));
    }

    /// <summary>A rank row serializes five words, even though its native object occupies six.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void RankRowsExcludeTheNativeVtableAndKeepBothVectorPrefixes(string build)
    {
        var body = PvePointsBinding.EncodeRanks(TestBuilds.All.Tables, build, [new(1, 25, 1)]);
        Assert.Equal(32, body.Length);
        Assert.Equal("0000000000000000010000000100000019000000000000000000000001000000", Convert.ToHexString(body));
        Assert.Throws<InvalidOperationException>(() => PvePointsBinding.EncodeRanks(TestBuilds.All.Tables, build, [new(-4, 25, 1)]));
        Assert.Equal("000000000000000000000000",
            Convert.ToHexString(PvePointsBinding.EncodeRanks(TestBuilds.All.Tables, build, [])));
        Assert.Throws<InvalidOperationException>(() => PvePointsBinding.EncodeRanks(TestBuilds.All.Tables, build,
            Enumerable.Repeat(new RoundPvePointRow(0, 1, 1), 33).ToArray()));
    }
}
