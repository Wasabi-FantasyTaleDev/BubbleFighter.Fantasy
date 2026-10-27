using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native modern result rows and tails, including populated fields that differ from the Korean baseline.</summary>
public sealed class NativeModernScoreTablesTests
{
    private const string RowHead = "6500000066000000010000004E0002"
        + "0100000002000000030000000400000005000000060000000700000008000000090000000A000000"
        + "0B0000000C0000000D0000000E0000000F0000001000000011000000120000001300000014000000"
        + "15000000160000001700000018000000190000001A0000001B0000001C000000"
        + "010000006700000001000000010000005600010000005300"
        + "C9000000CA000000CB000000CC000000CD000000CE000000CF000000D00000002D01";
    private const string RowEnd = "BD020000BE020000BF020000C0020000C1020000C2020000C3020000C40200002103000022030000";

    /// <summary>Every native row field keeps its own destination and the complete body ends at the final word.</summary>
    [Theory]
    [InlineData("cn-774", 258)]
    [InlineData("tw-454", 251)]
    public void PopulatedRowsPinNativeFieldOrderAndWidths(string build, int length)
    {
        // cn-774 sub_490F80 and tw-454 sub_47E640: distinct native strides, both with 28 stat words.
        var cn = build == "cn-774";
        var bytes = Convert.FromHexString(RowHead + (cn
            ? "91010000920100009301000094010000950100000B0CF501F601F701590200000D"
            : "9101000092010000930100000B0CF501F601F701590200000D0E") + RowEnd);
        Assert.Equal(length, bytes.Length);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "ScoreRow", bytes);
        Assert.Equal(101, values[cn ? "unknown_500" : "unknown_492"]);
        Assert.Equal(102, values["user_number"]);
        Assert.Equal("N", values["nickname"]);
        Assert.Equal((byte)2, values["team"]);
        var stats = new[] { "level", "unknown_60", "unknown_64", "unknown_68", "unknown_72", "unknown_76",
            "unknown_80", "unknown_84", "unknown_88", "unknown_92", "unknown_96", "unknown_100", "unknown_104",
            "unknown_108", "unknown_116", "unknown_120", "unknown_124", "unknown_128", "unknown_312",
            "unknown_132", "unknown_136", "unknown_140", "unknown_364", "exp_level_start", "exp_total",
            "exp_next_level", "exp_gained", "lucci_gained" };
        for (var i = 0; i < stats.Length; i++)
            Assert.Equal(i + 1, values[stats[i]]);
        Assert.Equal(true, values["unknown_388"]);
        Assert.Equal(103, values[cn ? "unknown_436" : "unknown_428"]);
        Assert.Equal(new object?[] { "V" }, Assert.IsType<object?[]>(values[cn ? "unknown_464" : "unknown_456"]));
        Assert.Equal("S", values[cn ? "unknown_440" : "unknown_432"]);
        var firstWords = cn ? new[] { 156, 476, 480, 392, 280, 284, 328, 408 } : [156, 468, 472, 392, 280, 284, 328, 408];
        for (var i = 0; i < firstWords.Length; i++)
            Assert.Equal(201 + i, values["unknown_" + firstWords[i]]);
        Assert.Equal((ushort)301, values["unknown_412"]);
        var nextWords = cn ? new[] { 416, 420, 296, 300, 304 } : [296, 300, 304];
        for (var i = 0; i < nextWords.Length; i++)
            Assert.Equal(401 + i, values["unknown_" + nextWords[i]]);
        Assert.Equal((byte)11, values["unknown_308"]);
        Assert.Equal((byte)12, values["unknown_309"]);
        Assert.Equal((ushort)501, values["unknown_320"]);
        Assert.Equal((ushort)502, values["unknown_322"]);
        Assert.Equal((ushort)503, values["unknown_324"]);
        Assert.Equal(601, values[cn ? "unknown_508" : "unknown_500"]);
        Assert.Equal((byte)13, values[cn ? "unknown_432" : "unknown_424"]);
        if (!cn)
            Assert.Equal((byte)14, values["unknown_425"]);
        Assert.Equal(Enumerable.Range(701, 8).Cast<object?>(), Assert.IsType<object?[]>(values["unknown_332"]));
        Assert.Equal(801, values[cn ? "unknown_424" : "unknown_416"]);
        Assert.Equal(802, values[cn ? "unknown_428" : "unknown_420"]);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "ScoreRow", values));
        RejectIncompleteAndTrailing(build, "ScoreRow", bytes);
    }

    /// <summary>The native tails retain counted triples, gated string order and each client's final fields.</summary>
    [Theory]
    [InlineData("kr-1256", false, 29)]
    [InlineData("kr-1256", true, 47)]
    [InlineData("cn-774", false, 44)]
    [InlineData("cn-774", true, 62)]
    [InlineData("tw-454", false, 28)]
    [InlineData("tw-454", true, 46)]
    public void NativeTailsPinConditionalStringsAndRegionSpecificEnd(string build, bool strings, int length)
    {
        // kr-1256 sub_10D16A0, cn-774 sub_8F2280 and tw-454 sub_A72D50.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        foreach (var word in new uint[] { 11, 12, 1, 13, 14, 15 })
            writer.WriteUInt32(word);
        writer.WriteByte(16);
        writer.WriteByte(17);
        writer.WriteUInt16(strings ? (ushort)4 : (ushort)0);
        if (strings)
            foreach (var value in new[] { "A", "B", "C" })
                writer.WriteWideStringPrefixed(value);
        if (build == "kr-1256")
            writer.WriteByte(0);
        else if (build == "cn-774")
            foreach (var word in new uint[] { 1, 0, 18, 19 })
                writer.WriteUInt32(word);
        var bytes = buffer.WrittenSpan.ToArray();
        Assert.Equal(length, bytes.Length);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "ScoreDataTail", bytes);
        var tw = build == "tw-454";
        Assert.Equal(11, values[tw ? "unknown_18892" : "unknown_19180"]);
        Assert.Equal(12, values[tw ? "unknown_18896" : "unknown_19184"]);
        var entry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object?[]>(values[tw ? "container_18900" : "container_19188"])));
        Assert.Equal(13, entry["unknown_n0"]);
        Assert.Equal(14, entry["unknown_n1"]);
        Assert.Equal(15, entry["unknown_n2"]);
        Assert.Equal((byte)16, values[tw ? "unknown_18908" : "unknown_19196"]);
        Assert.Equal((byte)17, values[tw ? "unknown_18909" : "unknown_19197"]);
        var names = tw ? new[] { "unknown_18912", "unknown_18960", "unknown_18936" } : ["unknown_19200", "unknown_19248", "unknown_19224"];
        for (var i = 0; i < names.Length; i++)
            if (strings)
                Assert.Equal(((char)('A' + i)).ToString(), values[names[i]]);
            else
                Assert.False(values.ContainsKey(names[i]));
        if (build == "cn-774")
        {
            Assert.Equal(true, values["unknown_19276"]);
            Assert.Equal(false, values["unknown_19277"]);
            Assert.Equal(18, values["unknown_19280"]);
            Assert.Equal(19, values["unknown_19284"]);
        }
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "ScoreDataTail", values));
        RejectIncompleteAndTrailing(build, "ScoreDataTail", bytes);
    }

    /// <summary>All modern result headers and live-score vectors share bytes despite differing native opcodes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void SharedScorePhasesKeepPopulatedWireBytes(string build)
    {
        foreach (var (name, hex) in new[]
        {
            ("ScoreDataLead", "02FF01000000"), ("ScoreHeadEntry", "010000000200000003000000"),
            ("ScoreDataMask", "0100000008000000"), ("ScoreGatedWord", "44332211"),
            ("RoundResultOpen", "0100000002000000"), ("RoundResultBanner", "01000000020000000304"),
            ("RoundResultClose", "01"), ("RoundStageReset", ""),
            ("RoundLiveScore", "0234120304010000000556340100000006785601000000079A78"),
        })
        {
            var bytes = Convert.FromHexString(hex);
            var values = PacketCodec.Read(TestBuilds.Packets, build, name, bytes);
            Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, name, values));
            RejectIncompleteAndTrailing(build, name, bytes);
        }
    }

    private static void RejectIncompleteAndTrailing(string build, string structure, byte[] bytes)
    {
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, bytes.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, structure, [.. bytes, 0]));
    }
}
