using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native combat records whose widths or field order differ from the Korean modern feed.</summary>
public sealed class NativeModernCombatTablesTests
{
    /// <summary>The seven-word native impact preserves both identities and does not acquire a finishing verdict.</summary>
    [Theory]
    [InlineData("00000000", false)]
    [InlineData("01000000", true)]
    public void TaiwanImpactKeepsItsNativeSevenWordBody(string flag, bool expected)
    {
        var tables = PacketTables.Load(FindPackets());
        // tw-454 sub_718700 writes seven words; tw-454 sub_72F6F0 reads the last as a Boolean.
        var body = Convert.FromHexString("40420F00E0C810000000A03F000020C000007040EFBEADDE" + flag);
        Assert.Equal(28, body.Length);
        var values = PacketCodec.ReadScalars(tables, "tw-454", "Unknown32796", body);
        Assert.Equal(1000000, values["unknown_0"]);
        Assert.Equal(1100000, values["unknown_4"]);
        Assert.Equal(1.25f, values["position_x"]);
        Assert.Equal(-2.5f, values["position_y"]);
        Assert.Equal(3.75f, values["position_z"]);
        Assert.Equal(unchecked((int)0xDEADBEEF), values["unknown_20"]);
        Assert.Equal(expected, values["unknown_24"]);
        Assert.Equal(body, PacketCodec.Write(tables, "tw-454", "Unknown32796",
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        RejectMalformed(tables, "tw-454", "Unknown32796", body);
    }

    /// <summary>Native packed motion retains the narrow fields and ends after its final short.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void PackedMotionRetainsEveryNativeFieldWithoutAListSuffix(string build)
    {
        var tables = PacketTables.Load(FindPackets());
        // cn-774 sub_489090 and tw-454 sub_4761B0 read 4,1,2,2,2,2,2,2,4,1,4,4,2 bytes.
        var body = Convert.FromHexString("40420F00A5010002000300040005000600EFBEADDEE778563412EFCDAB89BC9A");
        Assert.Equal(32, body.Length);
        var values = PacketCodec.ReadScalars(tables, build, "Unknown32925", body);
        Assert.Equal(1000000, values["actor"]);
        Assert.Equal((byte)0xA5, values["unknown_4"]);
        foreach (var (offset, value) in new[] { (5, 1), (7, 2), (9, 3), (11, 4), (13, 5), (15, 6) })
            Assert.Equal((ushort)value, values["unknown_" + offset]);
        Assert.Equal(unchecked((int)0xDEADBEEF), values["unknown_17"]);
        Assert.Equal((byte)0xE7, values["unknown_21"]);
        Assert.Equal(0x12345678, values["unknown_22"]);
        Assert.Equal(unchecked((int)0x89ABCDEF), values["unknown_26"]);
        Assert.Equal((ushort)0x9ABC, values["unknown_30"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, "Unknown32925",
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        RejectMalformed(tables, build, "Unknown32925", body);
    }

    /// <summary>The native contact carries full position floats before its target, without a compact-hit suffix.</summary>
    [Theory]
    [InlineData("tw-454", "785634120000A03F000020C000007040E0C81000", 20)]
    [InlineData("cn-774", "785634120000A03F000020C000007040EFBEADDEE0C81000", 24)]
    public void DamagerContactKeepsNativeFloatCoordinatesAndTargetOrder(string build, string hex, int length)
    {
        var tables = PacketTables.Load(FindPackets());
        // cn-774 sub_8B43B0 consumes an extra word before the target; tw-454 sub_728CC0 has five words.
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        var values = PacketCodec.ReadScalars(tables, build, "RoundDamagerContactNative", body);
        Assert.Equal(0x12345678, values["unknown_0"]);
        Assert.Equal(1.25f, values["position_x"]);
        Assert.Equal(-2.5f, values["position_y"]);
        Assert.Equal(3.75f, values["position_z"]);
        Assert.Equal(1100000, values["target"]);
        if (length == 24)
            Assert.Equal(unchecked((int)0xDEADBEEF), values["unknown_16"]);
        else
            Assert.False(values.ContainsKey("unknown_16"));
        Assert.Equal(body, PacketCodec.Write(tables, build, "RoundDamagerContactNative",
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        RejectMalformed(tables, build, "RoundDamagerContactNative", body);
    }

    /// <summary>The native rescue consumes a complete Boolean word, rather than a float followed by one byte.</summary>
    [Theory]
    [InlineData("tw-454", "00000000", false)]
    [InlineData("tw-454", "01000000", true)]
    [InlineData("cn-774", "00000000", false)]
    [InlineData("cn-774", "01000000", true)]
    public void RescueKeepsTwoIdentitiesAndOneBooleanWord(string build, string flag, bool expected)
    {
        var tables = PacketTables.Load(FindPackets());
        // tw-454 sub_70B3F0 case 0x8027 calls tw-454 sub_44ABD0 for its final four-byte Boolean.
        var body = Convert.FromHexString("40420F00E0C81000" + flag);
        Assert.Equal(12, body.Length);
        var values = PacketCodec.ReadScalars(tables, build, "RoundRescueNative", body);
        Assert.Equal(1000000, values["rescuer"]);
        Assert.Equal(1100000, values["victim"]);
        Assert.Equal(expected, values["unknown_8"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, "RoundRescueNative",
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        RejectMalformed(tables, build, "RoundRescueNative", body);
    }

    /// <summary>The paired rescue frames reverse their participant order and retain an uninterpreted byte.</summary>
    [Theory]
    [InlineData("tw-454", "RoundRescuePairNative", "E0C8100040420F0000", 9, 0)]
    [InlineData("tw-454", "RoundRescuePairNative", "E0C8100040420F00E7", 9, 231)]
    [InlineData("tw-454", "RoundRescueReleaseNative", "40420F00E0C81000EFBEADDE00", 13, 0)]
    [InlineData("tw-454", "RoundRescueReleaseNative", "40420F00E0C81000EFBEADDEE7", 13, 231)]
    [InlineData("cn-774", "RoundRescuePairNative", "E0C8100040420F0000", 9, 0)]
    [InlineData("cn-774", "RoundRescuePairNative", "E0C8100040420F00E7", 9, 231)]
    [InlineData("cn-774", "RoundRescueReleaseNative", "40420F00E0C81000EFBEADDE00", 13, 0)]
    [InlineData("cn-774", "RoundRescueReleaseNative", "40420F00E0C81000EFBEADDEE7", 13, 231)]
    public void PairedRescueKeepsNativeParticipantOrderAndByteSuffix(string build, string structure, string hex, int length, int flag)
    {
        var tables = PacketTables.Load(FindPackets());
        // tw-454 sub_7FE6C0 writes both records; tw-454 sub_70E250 reads their suffixes as bytes.
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        var values = PacketCodec.ReadScalars(tables, build, structure, body);
        Assert.Equal(1000000, values["victim"]);
        Assert.Equal(1100000, values["rescuer"]);
        Assert.Equal(flag, Convert.ToInt32(values[length == 9 ? "unknown_8" : "unknown_12"]));
        if (length == 13)
            Assert.Equal(unchecked((int)0xDEADBEEF), values["unknown_8"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, structure,
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        RejectMalformed(tables, build, structure, body);
    }

    private static void RejectMalformed(PacketTables tables, string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, structure, body.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build, structure, [.. body, 0]));
    }

    private static string FindPackets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "packets")))
            directory = directory.Parent;
        return directory is null ? throw new DirectoryNotFoundException("packets") : Path.Combine(directory.FullName, "packets");
    }
}
