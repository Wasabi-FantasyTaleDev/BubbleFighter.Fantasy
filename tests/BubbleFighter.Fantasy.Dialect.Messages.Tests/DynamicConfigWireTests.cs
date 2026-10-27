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
        Assert.Equal(76, plain.Length);
        Assert.Equal(Convert.FromHexString("14000000"), plain[..4]);
        Assert.Equal("Ui/DlgCreateUser3.ui", System.Text.Encoding.Unicode.GetString(plain, 4, 40));
        Assert.Equal(Convert.FromHexString("040000000A00000013000000190000001B000000000000000000000064000000"), plain[44..]);
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
        Assert.Equal(new byte[4], wire.Reply(Request(mask, 123, checksum, 456)));
        Assert.Equal(snapshot, wire.Reply(Request(mask, checksum, checksum ^ 1, checksum)));
        Assert.Equal(new byte[4], wire.Reply(Request(1, checksum)));
        Assert.Equal(new byte[4], wire.Reply(Request(0)));
        Assert.Throws<PacketFormatException>(() => wire.Reply(Request(mask, 123, checksum)));
        Assert.Throws<PacketFormatException>(() => wire.Reply(Request(1 << 14, 0, 1)));
    }

    /// <summary>The client's checksum seed is zero, including for the empty stream.</summary>
    [Fact]
    public void AdlerChecksumUsesTheNativeZeroSeed()
    {
        Assert.Equal(0u, DynamicConfigWire.Checksum([]));
        Assert.Equal(0x11DD0397u, DynamicConfigWire.Checksum("Wikipedia"u8));
    }

    /// <summary>Unaudited modern creation and the legacy native form do not receive the Korean content category.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    [InlineData("kr-812")]
    public void OtherBuildsKeepTheirExistingEmptyDynamicReply(string build)
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, build);
        Assert.False(wire.HasCreationContent);
        Assert.Equal(new byte[4], wire.Reply(Request(1 << 14, 0)));
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
