using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native form's category envelope, cached checksum and build scope before stage construction.</summary>
public sealed class DynamicConfigWireTests
{
    /// <summary>The native content contains the form path, all four selectable characters, one empty map and two words.</summary>
    [Fact]
    public void CreationContentMatchesTheNativeReaderAndSharedLobbyChoices()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        Assert.True(wire.HasCreationContent);
        var snapshot = wire.CreationSnapshot();
        Assert.Equal(1 << 14, BinaryPrimitives.ReadInt32LittleEndian(snapshot));
        Assert.Equal(snapshot.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(snapshot.AsSpan(4)));
        using var input = new MemoryStream(snapshot[8..]);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var plain = output.ToArray();
        // The shipped generation: the form, the four choices, the four preview overrides and the
        // two trailing words (the shipped f32 0.293 and 110).
        Assert.Equal(108, plain.Length);
        Assert.Equal(Convert.FromHexString("14000000"), plain[..4]);
        Assert.Equal("Ui/DlgCreateUser3.ui", System.Text.Encoding.Unicode.GetString(plain, 4, 40));
        Assert.Equal(Convert.FromHexString(
            "040000000A00000013000000190000001B000000" +
            "040000000A000000C3921E0013000000C4921E0019000000C5921E001B000000C6921E00" +
            "5555953E6E000000"), plain[44..]);
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "CreationContent", plain);
        Assert.Equal(new object?[] { 10, 19, 25, 27 }, Assert.IsType<object?[]>(fields["character_types"]));
        Assert.Equal(snapshot, wire.Reply(Request(1 << 14, 0)));
    }

    /// <summary>Checksum words for lower and higher categories must not shift the creation category's checksum.</summary>
    [Fact]
    public void CacheComparisonUsesCategoryFourteenAndConsumesEveryRequestedChecksum()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        var snapshot = wire.CreationSnapshot();
        var checksum = DynamicConfigWire.Checksum(snapshot.AsSpan(8));
        const uint mask = 0x80004001;
        // kr-1256 sub_99E860 emits the three checksums for bits 0, 14 and 31 in ascending order.
        Assert.Null(wire.Reply(Request(mask, 123, checksum, 456)));
        Assert.Equal(snapshot, wire.Reply(Request(mask, checksum, checksum ^ 1, 456)));
        Assert.Equal(snapshot, wire.Reply(Request(mask, 123, checksum ^ 1, checksum)));
        Assert.Equal(snapshot, wire.Reply(Request(mask, checksum ^ 1, checksum ^ 2, checksum ^ 3)));
        Assert.Null(wire.Reply(Request(1, checksum)));
        Assert.Null(wire.Reply(Request(0)));
        Assert.Throws<PacketFormatException>(() => wire.Reply(Request(mask, 123, checksum)));
        Assert.Throws<PacketFormatException>(() => wire.Reply(Request(1 << 14, 0, 1)));
    }

    /// <summary>A different served category's stamp cannot conceal stale data in a requested category.</summary>
    [Fact]
    public void SwappedCategoryStampsReframeBothCategories()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        var first = wire.StampOf(1);
        var second = wire.StampOf(14);
        Assert.NotEqual(first, second);
        const uint mask = (1u << 1) | (1u << 14);

        Assert.Null(wire.Reply(Request(mask, first, second)));
        var reply = Assert.IsType<byte[]>(wire.Reply(Request(mask, second, first)));
        Assert.Equal(mask, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        var fields = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "DynamicConfigReply", reply);
        Assert.IsType<Dictionary<string, object?>>(fields["slot_1"]);
        Assert.IsType<Dictionary<string, object?>>(fields["slot_14"]);
    }

    /// <summary>A request may refresh one category while another selected category stays cached.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void OnlyTheStaleRequestedCategoryIsReframed(int staleBit)
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "kr-1256");
        var first = wire.StampOf(1);
        var second = wire.StampOf(14);
        const uint mask = (1u << 1) | (1u << 14);
        var reply = Assert.IsType<byte[]>(wire.Reply(Request(mask,
            staleBit == 1 ? first ^ 1 : first, staleBit == 14 ? second ^ 1 : second)));
        var expected = Assert.IsType<byte[]>(wire.Reply(Request(1u << staleBit, wire.StampOf(staleBit) ^ 1)));
        Assert.Equal(expected, reply);
        Assert.Equal(1u << staleBit, BinaryPrimitives.ReadUInt32LittleEndian(reply));
    }

    /// <summary>The client's checksum seed is zero, including for the empty stream.</summary>
    [Fact]
    public void AdlerChecksumUsesTheNativeZeroSeed()
    {
        Assert.Equal(0u, DynamicConfigWire.Checksum([]));
        Assert.Equal(0x11DD0397u, DynamicConfigWire.Checksum("Wikipedia"u8));
    }

    /// <summary>Builds without the Korean creation content retain their native empty-mask byte widths.</summary>
    [Theory]
    [InlineData("cn-735", 2)]
    [InlineData("cn-774", 2)]
    [InlineData("tw-454", 2)]
    [InlineData("kr-812", 4)]
    public void OtherBuildsKeepTheirNativeEmptyMaskWidth(string build, int bytes)
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, build);
        Assert.False(wire.HasCreationContent);
        Assert.Empty(wire.ServedBits);
        var request = PacketCodec.Write(TestBuilds.Packets, build, "DynamicConfigRequest",
            new Dictionary<string, object?> { ["mask"] = 0 });
        Assert.Equal(new byte[bytes], wire.Reply(request));
    }

    private static byte[] Request(uint mask, params uint[] checksums)
    {
        var body = new byte[4 + 4 * checksums.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, mask);
        for (var index = 0; index < checksums.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + 4 * index), checksums[index]);
        return body;
    }
}
