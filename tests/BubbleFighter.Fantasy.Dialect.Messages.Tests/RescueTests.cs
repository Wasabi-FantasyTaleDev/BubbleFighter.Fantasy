using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins rescue list framing and native start, failure, success and progress payload widths.</summary>
public sealed class RescueTests
{
    /// <summary>Preserves the six scalar words, prefixed string vector and four-byte bool of one mission.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void MissionListHasNativeWordAndStringFraming(string build)
    {
        var body = RescueBinding.EncodePrepared(TestBuilds.All.Tables, build,
            [new(1, 25000, 30000, ["killmonster", "1", "4", "ms_citizen"])]);
        Assert.Equal(98, body.Length);
        var reader = new PacketReader(body);
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(1U, reader.ReadUInt32());
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(25000U, reader.ReadUInt32());
        Assert.Equal(30000U, reader.ReadUInt32());
        Assert.Equal(4U, reader.ReadUInt32());
        Assert.Equal("killmonster", reader.ReadWideStringPrefixed());
        Assert.Equal("1", reader.ReadWideStringPrefixed());
        Assert.Equal("4", reader.ReadWideStringPrefixed());
        Assert.Equal("ms_citizen", reader.ReadWideStringPrefixed());
        Assert.Equal(0U, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Rejects short and trailing progress bodies instead of guessing missing fields.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void ProgressHasExactlyThreeWords(string build)
    {
        var body = Convert.FromHexString("020000006600000000000000");
        Assert.Equal(new RoundRescueProgressReported(2, 102), RescueBinding.ReadProgress(TestBuilds.All.Tables, build, body));
        Assert.Throws<PacketFormatException>(() => RescueBinding.ReadProgress(TestBuilds.All.Tables, build, body.AsSpan(0, 8)));
        Assert.Throws<PacketFormatException>(() => RescueBinding.ReadProgress(TestBuilds.All.Tables, build, new byte[16]));
        body[8] = 1;
        Assert.Throws<PacketFormatException>(() => RescueBinding.ReadProgress(TestBuilds.All.Tables, build, body));
    }

    /// <summary>Uses a next index for failure and a zero status followed by the completed index for success.</summary>
    [Fact]
    public void LifecyclePacketsHavePinnedWidths()
    {
        var tables = TestBuilds.All.Tables;
        Assert.Equal("02000000", Convert.ToHexString(ScalarGolden.Write(tables.Resolve("cn-324", "PveRescueMissionStarted"), new() { ["mission_index"] = 2 })));
        Assert.Equal("03000000", Convert.ToHexString(ScalarGolden.Write(tables.Resolve("cn-324", "PveRescueMissionTimedOut"), new() { ["next_mission_index"] = 3 })));
        Assert.Equal("0000000002000000", Convert.ToHexString(ScalarGolden.Write(tables.Resolve("cn-324", "PveRescueMissionSucceeded"), new() { ["mission_index"] = 2 })));
    }
}
