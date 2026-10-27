using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the kr-812 rank and ladder bodies in <c>packets/rankings.yaml</c>: each has its exact length, reads
/// back through <see cref="PacketCodec"/> unchanged, and is refused one byte short and one byte long. The
/// ladder page is written out in full hex so a wrong word order or a swapped count fails on the bytes.
/// </summary>
public sealed class RankingsTablePins
{
    private const string Build = "kr-812";

    /// <summary>Every tabled rank and ladder body keeps its length and round-trips.</summary>
    [Fact]
    public void TheRankingBodiesMatchTheirLengthsAndRoundTrip()
    {
        // Packet_gcpReqSeasonLadder, deserializer kr-812 sub_503B90: one byte at +4.
        Check("SeasonLadderRequest", 1, ("unknown_4", (byte)3));
        // Packet_gcpReqSeasonLadderGetRankListNo and Packet_gspSeasonLadderLevelUpNotice, both kr-812 sub_80A910: one word.
        Check("SeasonRankListRequest", 4, ("unknown_4", 4));
        Check("SeasonLadderLevelUpNotice", 4, ("unknown_4", 5));
        // Packet_gspRepSeasonLadderGetRankList, deserializer kr-812 sub_83AF00: a word, a counted word vector,
        // a boolean word, an eight-byte vector, two record vectors and a final word.
        Check("SeasonRankList", 36,
            ("unknown_4", 7), ("unknown_8", new object[] { 1, 2 }), ("unknown_24", true), ("unknown_76", 9));
    }

    /// <summary>The ladder page is a word, four counted vectors and a boolean word; its bytes are pinned exactly.</summary>
    [Fact]
    public void TheLadderPageIsPinnedByteForByte()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, Build, "SeasonRankList", new Dictionary<string, object?>
        {
            ["unknown_4"] = 7,
            ["unknown_8"] = new object[] { 1, 2 },
            ["unknown_24"] = true,
            ["unknown_76"] = 9,
        });
        Assert.Equal("070000000200000001000000020000000100000000000000000000000000000009000000", Convert.ToHexString(body));
        Assert.Equal(36, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, "SeasonRankList", body);
        Assert.Equal(7, Convert.ToInt32(read["unknown_4"]));
        Assert.Equal(2, Assert.IsAssignableFrom<System.Collections.ICollection>(read["unknown_8"]).Count);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, "SeasonRankList", read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, "SeasonRankList", body[..^1]));
    }

    /// <summary>The layouts cover the legacy family; the modern builds state no rank layout yet.</summary>
    [Fact]
    public void TheRankingLayoutsAreBaselineOnly()
    {
        foreach (var structure in new[] { "SeasonLadderRequest", "SeasonRankListRequest", "SeasonLadderLevelUpNotice", "SeasonRankList" })
        {
            Assert.NotEmpty(TestBuilds.Packets.Resolve(Build, structure));
            Assert.NotEmpty(TestBuilds.Packets.Resolve("cn-717", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("kr-1256", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("cn-774", structure));
            Assert.Empty(TestBuilds.Packets.Resolve("tw-454", structure));
        }
    }

    private static void Check(string structure, int length, params (string Name, object? Value)[] fields)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        var body = PacketCodec.Write(TestBuilds.Packets, Build, structure, values);
        Assert.Equal(length, body.Length);
        var read = PacketCodec.Read(TestBuilds.Packets, Build, structure, body);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, Build, structure, read));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, Build, structure, [.. body, 0]));
    }
}
