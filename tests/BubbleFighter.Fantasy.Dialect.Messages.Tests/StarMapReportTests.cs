using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the star points of the host's map report (kr-812 sub_50C840, kr-1256 sub_6DA5D0).</summary>
public sealed class StarMapReportTests
{
    /// <summary>The report's star vectors become star points; an entry of another kind is skipped.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("kr-1256")]
    [InlineData("tw-454")]
    public void MapReportStarVectorsBecomeStarPoints(string build)
    {
        var tables = TestBuilds.All.Tables;
        Dictionary<string, object?> Point(float x) => new() { ["x"] = x, ["y"] = 120f, ["z"] = 1700f };
        var fields = new Dictionary<string, object?>
        {
            ["star_kinds"] = new[] { 1, 2, 7 }, ["star_positions"] = new[] { Point(1), Point(2), Point(3) },
            ["star_first_ms"] = new[] { 1, 197000, 5 }, ["star_period_ms"] = new[] { 98500, 999999, 5 },
        };
        var body = PacketCodec.Write(tables, build, "SupplyMapReport", fields);
        var stars = StarBinding.ReadStarPoints(SupplyBinding.ReadReportValues(tables, build, body, 128), body.Length);
        Assert.NotNull(stars);
        Assert.Equal(new[] { new RoundStarPoint(1, 120, 1700, 1, 1, 98500), new RoundStarPoint(2, 120, 1700, 2, 197000, 999999) },
            stars.Points);
        Assert.Empty(SupplyBinding.ReadReport(tables, build, body, 128).Points);
        fields["star_period_ms"] = new[] { 98500, 999999 };
        body = PacketCodec.Write(tables, build, "SupplyMapReport", fields);
        Assert.Throws<PacketFormatException>(() =>
            StarBinding.ReadStarPoints(SupplyBinding.ReadReportValues(tables, build, body, 128), body.Length));
    }

    /// <summary>
    /// On the modern rooms the star binding reads the clients' own star counts from the six-byte optimized score report
    /// by the build's kinds (kr-1256: 21 and 24 taken, 22 and 25 dropped); another kind is no star report.
    /// </summary>
    [Theory]
    [InlineData(21, RoundStarChange.SmallGained)]
    [InlineData(24, RoundStarChange.BigGained)]
    [InlineData(22, RoundStarChange.SmallLost)]
    [InlineData(25, RoundStarChange.BigLost)]
    public void Kr1256OptimizedReportsCarryStarChanges(int kind, RoundStarChange change)
    {
        var tables = TestBuilds.All.Tables;
        var score = PlayValues.Of(tables, "kr-1256").Score;
        Assert.Equal(new RoundStarReported(1, change) { NamedReporterSlot = 1 },
            StarBinding.ReadStarReport(tables, "kr-1256", score, [(byte)kind, 0xE0, 0xC8, 0x10, 0x00, 1]));
        Assert.Null(StarBinding.ReadStarReport(tables, "kr-1256", score, [2, 0xE0, 0xC8, 0x10, 0x00, 1]));
    }

    /// <summary>Other layouts retain their original report byte without acquiring KR1256's affected-seat semantics.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UnlistedBuildDoesNotAcquireNamedStarReporter(string build)
    {
        var score = PlayValues.Of(TestBuilds.Packets, build).Score;
        var report = StarBinding.ReadStarReport(TestBuilds.Packets, build, score,
            [(byte)score.StarSmallLost, 0x40, 0x42, 0x0F, 0, 1]);
        Assert.NotNull(report);
        Assert.Equal(1, report.ReporterSlot);
        Assert.Null(report.NamedReporterSlot);
    }

    /// <summary>A report layout that names no star vectors has no star points.</summary>
    [Fact]
    public void ReportWithoutStarVectorsHasNoStarPoints()
    {
        Assert.DoesNotContain(TestBuilds.All.Tables.Resolve("cn-324", "SupplyMapReport"), field => field.Name == "star_kinds");
        Assert.Null(StarBinding.ReadStarPoints([], 0));
    }
}
