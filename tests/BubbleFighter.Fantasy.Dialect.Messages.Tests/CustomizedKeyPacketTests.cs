using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the audited key-map wire format across the baseline and three modern clients.</summary>
public sealed class CustomizedKeyPacketTests
{
    // Two names exercise UTF-16 code units, including a surrogate pair, and a signed key word.
    private const string MappingHex = "02000000040000004A0075006D007000200000000200000001D800DCFFFFFFFF";

    [Theory]
    [InlineData("kr-812", 32)]
    [InlineData("kr-1256", 28)]
    [InlineData("cn-774", 28)]
    [InlineData("tw-454", 28)]
    public void NativeObjectStridesDoNotChangeTheWireRows(string build, int keyOffset)
    {
        var bytes = Convert.FromHexString("00000000" + MappingHex);
        Assert.Equal(36, bytes.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "CustomizedKeysUpdate", bytes,
            requireCanonicalBooleans: true);
        Assert.False(Assert.IsType<bool>(fields["use_defaults"]));
        var rows = Assert.IsType<object?[]>(fields["mappings"]);
        Assert.Equal(2, rows.Length);
        var first = Assert.IsType<Dictionary<string, object?>>(rows[0]);
        Assert.Equal("Jump", first["name"]);
        Assert.Equal(32, first["key"]);
        var second = Assert.IsType<Dictionary<string, object?>>(rows[1]);
        Assert.Equal("\U00010400", second["name"]);
        Assert.Equal(-1, second["key"]);
        Assert.Equal(keyOffset, TestBuilds.Packets.Resolve(build, "CustomizedKey").Single(f => f.Name == "key").Offset);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "CustomizedKeysUpdate", fields));
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "CustomizedKeysUpdate", bytes[..length]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OutgoingResetAndIncomingApplyHaveSeparateMeanings(string build)
    {
        var reset = Convert.FromHexString("0100000000000000");
        Assert.Equal(8, reset.Length);
        var update = PacketCodec.Read(TestBuilds.Packets, build, "CustomizedKeysUpdate", reset);
        Assert.True(Assert.IsType<bool>(update["use_defaults"]));
        Assert.Empty(Assert.IsType<object?[]>(update["mappings"]));
        Assert.Equal(reset, PacketCodec.Write(TestBuilds.Packets, build, "CustomizedKeysUpdate", update));

        var bytes = Convert.FromHexString("01000000" + MappingHex);
        var reply = PacketCodec.Read(TestBuilds.Packets, build, "CustomizedKeysReply", bytes);
        Assert.True(Assert.IsType<bool>(reply["apply_mappings"]));
        Assert.False(reply.ContainsKey("use_defaults"));
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "CustomizedKeysReply", reply));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void UntrustedNamesCountsFlagsAndTrailingBytesObeyCallerBounds(string build)
    {
        var bytes = Convert.FromHexString("00000000" + MappingHex);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", bytes, new PacketReadLimits(MaxCollectionElements: 1)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", bytes, new PacketReadLimits(MaxStringCharacters: 3)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", Convert.FromHexString("00000000FFFFFFFF")));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", Convert.FromHexString("0000000001000000FFFFFFFF")));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", Convert.FromHexString("0200000000000000"), requireCanonicalBooleans: true));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build,
            "CustomizedKeysUpdate", [.. bytes, 0]));
    }
}
