using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native score envelope widths independently of which event kinds the runtime admits.</summary>
public sealed class ScoreReportTableTests
{
    [Theory]
    [InlineData("kr-812", 16)]
    [InlineData("cn-717", 16)]
    [InlineData("id-1809", 16)]
    [InlineData("cn-324", 32)]
    [InlineData("id-400", 32)]
    [InlineData("cn-406", 32)]
    [InlineData("cn-279", 20)]
    [InlineData("cn-706", 16)]
    public void NativeReportShapeConsumesExactlyItsOwnBody(string build, int length)
    {
        var body = Enumerable.Range(1, length).Select(value => (byte)value).ToArray();
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundScoreReport", body);
        Assert.Equal(length == 16 ? 1 : 0x04030201, Convert.ToInt32(fields["kind"]));
        Assert.Equal(length == 16 ? 0x05040302 : 0x08070605, fields["unknown_8"]);
        if (length == 16)
        {
            Assert.Equal((byte)11, fields["reporter_slot"]);
            Assert.Equal((ushort)0x0E0D, fields["unknown_20"]);
            Assert.Equal((ushort)0x100F, fields["unknown_22"]);
        }
        if (build == "cn-324")
        {
            Assert.Equal(0x14131211, fields["reporter_slot"]);
            Assert.Equal(0x1C1B1A19, fields["combo"]);
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundScoreReport", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundScoreReport", [.. body, 0]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void OptimizedReportKeepsSixBytesAndReporter(string build)
    {
        var body = Convert.FromHexString("027856341203");
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundOptimizedScoreReport", body);
        Assert.Equal((byte)2, fields["kind"]);
        Assert.Equal(0x12345678, fields["unknown_8"]);
        Assert.Equal((byte)3, fields["reporter_slot"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundOptimizedScoreReport", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoundOptimizedScoreReport", [.. body, 0]));
    }
}
