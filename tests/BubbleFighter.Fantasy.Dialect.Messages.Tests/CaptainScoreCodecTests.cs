using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins each audited Captain report's kind, width, player references and full-width seat checks.</summary>
public sealed class CaptainScoreCodecTests
{
    /// <summary>Each native producer's complete report names the same victim and scorer for shared ownership checks.</summary>
    [Theory]
    [InlineData("kr-812", 27, 16, "reporter_slot")]
    [InlineData("cn-717", 27, 16, "reporter_slot")]
    [InlineData("id-400", 27, 32, "unknown_20")]
    [InlineData("cn-404", 27, 32, "unknown_20")]
    [InlineData("cn-408", 27, 32, "unknown_20")]
    [InlineData("id-1809", 27, 16, "reporter_slot")]
    [InlineData("cn-706", 27, 16, "reporter_slot")]
    [InlineData("kr-1256", 75, 16, "reporter_slot")]
    [InlineData("cn-774", 27, 16, "reporter_slot")]
    [InlineData("tw-454", 27, 16, "reporter_slot")]
    public void NativeReportPinsKindWidthAndOwnership(string build, int kind, int length, string killerField)
    {
        var codec = new CaptainScoreCodec(TestBuilds.Packets, build);
        var values = Fields(kind, killerField);
        var body = Write(build, values);
        Assert.Equal(length, body.Length);
        var report = Assert.IsType<BubbleFighter.Fantasy.Contracts.Match.RoundFinishingBlowReported>(codec.Read(body));
        Assert.Equal(2, report.Victim);
        Assert.Equal(3, report.ReporterSlot);
        Assert.True(report.CaptainKill);
        Assert.True(report.RequiresReporterOwnership);
        Assert.True(report.HasVictimReference);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[1]).ToArray() })
            Assert.Throws<PacketFormatException>(() => codec.Read(malformed));

        values["kind"] = kind == 75 ? 27 : 75;
        Assert.Null(codec.Read(Write(build, values)));
        values["kind"] = kind;
        values[killerField] = 4;
        Assert.Null(codec.Read(Write(build, values)));
        values[killerField] = 3;
        values["unknown_8"] = 1_200_001;
        Assert.Null(codec.Read(Write(build, values)));
        values["unknown_8"] = 1_300_000;
        values["unknown_16"] = 3;
        Assert.Null(codec.Read(Write(build, values)));
    }

    /// <summary>A wide seat cannot wrap to a matching byte; negative, high-bit and out-of-range words are rejected.</summary>
    [Theory]
    [InlineData("id-400")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    public void WideSeatWordsCannotTruncateIntoValidPlayers(string build)
    {
        var codec = new CaptainScoreCodec(TestBuilds.Packets, build);
        foreach (var invalid in new[] { -1, 258, 65538, int.MinValue })
        {
            var victim = Fields(27, "unknown_20");
            victim["unknown_16"] = invalid;
            Assert.Null(codec.Read(Write(build, victim)));
            var killer = Fields(27, "unknown_20");
            killer["unknown_20"] = invalid;
            Assert.Null(codec.Read(Write(build, killer)));
        }
    }

    private static Dictionary<string, object?> Fields(int kind, string killerField) => new()
    {
        ["kind"] = kind, ["unknown_8"] = 1_200_000, ["unknown_12"] = 1_300_000,
        ["unknown_16"] = 2, [killerField] = 3,
    };

    private static byte[] Write(string build, IReadOnlyDictionary<string, object?> fields) =>
        PacketCodec.Write(TestBuilds.Packets, build, "RoundScoreReport", fields);
}
