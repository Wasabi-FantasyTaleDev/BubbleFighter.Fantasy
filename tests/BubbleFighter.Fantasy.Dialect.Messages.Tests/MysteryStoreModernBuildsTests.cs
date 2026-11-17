using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Pins the Mysterystore of cn-774 and tw-454 against their own clients: the request reader cn-774 sub_47FC10 / tw-454
/// sub_471DA0 (C2S 127), the reply reader cn-774 sub_47FD90 / tw-454 sub_471F20, and cn-774's slot-4 window
/// (cn-774 sub_E45BD0). Their layouts are kr-1256's field for field.
/// </summary>
public sealed class MysteryStoreModernBuildsTests
{
    private static readonly string[] Structures =
    [
        "UserMysteryStoreRequest", "UserMysteryStoreRequestType2", "UserMysteryStoreRequestType3",
        "UserMysteryStoreRequestType4", "MysteryStorePacketConfig", "MysteryStorePacketUserData",
        "MysteryStorePacketStock", "MysteryStoreStockList", "UserMysteryStoreReply", "UserMysteryStorePurchaseTail",
    ];

    /// <summary>Both builds follow kr-1256's tables: the same fields in the same order with the same types.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void LayoutsAreKr1256sFieldForField(string build)
    {
        foreach (var structure in Structures)
        {
            var expected = TestBuilds.Packets.Resolve("kr-1256", structure).Select(field => (field.Name, field.Type)).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, TestBuilds.Packets.Resolve(build, structure).Select(field => (field.Name, field.Type)).ToArray());
        }
    }

    /// <summary>The request is C2S 127 in both builds (128 is another request there) and the reply is S2C 148 or 146.</summary>
    [Theory]
    [InlineData("cn-774", 148)]
    [InlineData("tw-454", 146)]
    public void RequestAndReplyNumbersAreTheBuildsOwn(string build, int reply)
    {
        Assert.Equal((ushort)127, TestBuilds.Opcodes(build).C2S("gcpReqUserMysteryStore"));
        Assert.Equal((ushort)reply, TestBuilds.Opcodes(build).S2C("gspRepUserMysteryStore"));
    }

    /// <summary>The four request bodies keep the lengths the native reader consumes: 5, 7, 7 and 11 bytes.</summary>
    [Theory]
    [InlineData("cn-774", "UserMysteryStoreRequest", 5, 1)]
    [InlineData("cn-774", "UserMysteryStoreRequestType2", 7, 2)]
    [InlineData("cn-774", "UserMysteryStoreRequestType3", 7, 3)]
    [InlineData("cn-774", "UserMysteryStoreRequestType4", 11, 4)]
    [InlineData("tw-454", "UserMysteryStoreRequest", 5, 1)]
    [InlineData("tw-454", "UserMysteryStoreRequestType2", 7, 2)]
    [InlineData("tw-454", "UserMysteryStoreRequestType3", 7, 3)]
    [InlineData("tw-454", "UserMysteryStoreRequestType4", 11, 4)]
    public void RequestLengthsAreExact(string build, string structure, int length, int type)
    {
        var body = new byte[length];
        body[0] = (byte)type;
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body);
        Assert.Equal(type, Convert.ToInt32(fields["type"]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, [.. body, 0]));
    }

    /// <summary>The binding validates both builds' layouts and statuses; the statuses are the kr-1256 ones (cn-774 sub_E46880).</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TheBindingAcceptsTheBuildsTables(string build)
    {
        MysteryStoreBinding.RequireLayouts(TestBuilds.Packets, build);
        Assert.Equal(MysteryStoreBinding.ReadStatuses(TestBuilds.Packets, "kr-1256"), MysteryStoreBinding.ReadStatuses(TestBuilds.Packets, build));
    }

    /// <summary>cn-774 opens the store; tw-454 has no pool in its workbook and keeps it off.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("kr-812", false)]
    public void CapabilityMarksTheBuildsWithAStore(string build, bool store) =>
        Assert.Equal(store, TestBuilds.Packets.CapabilityOf(build, "mystery_store"));

    /// <summary>
    /// cn-774 reads category 4 as a counted zlib blob of two eight-byte dates (cn-774 sub_811660, cn-774 sub_E45BD0); the
    /// stamp is the Adler-32 of the compressed bytes and a current request is answered with the acknowledgement path.
    /// </summary>
    [Fact]
    public void Cn774ServesTheWindowAsACountedBlob()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, "cn-774");
        Assert.Contains(4, wire.ServedBits);
        Assert.Equal("DynamicConfigBlob", DynamicConfigWire.CarrierOf(TestBuilds.Packets, "cn-774", 4));
        var request = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 1 << 4);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), wire.StampOf(4) ^ 1);
        var reply = Assert.IsType<byte[]>(wire.Reply(request));
        Assert.Equal(1 << 4, BinaryPrimitives.ReadUInt16LittleEndian(reply));
        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(2));
        Assert.Equal(reply.Length - 6, length);
        Assert.Equal(wire.StampOf(4), DynamicConfigWire.Checksum(reply.AsSpan(6)));
        using var input = new MemoryStream(reply[6..], writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        Assert.Equal(16, output.Length);
        var window = PacketCodec.Read(TestBuilds.Packets, "cn-774", "MysteryStoreContext", output.ToArray());
        Assert.True((double)window["start"]! < (double)window["end"]!);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(2), wire.StampOf(4));
        Assert.Null(wire.Reply(request));
    }

    /// <summary>
    /// tw-454 reads its categories as plain bytes (tw-454 sub_628B00): its window category is plain too, but no window is
    /// served because the store is off there.
    /// </summary>
    [Fact]
    public void Tw454ReadsItsCategoriesAsPlainBytes()
    {
        Assert.DoesNotContain(4, new DynamicConfigWire(TestBuilds.Packets, "tw-454").ServedBits);
        Assert.Equal("MysteryStoreContext", DynamicConfigWire.CarrierOf(TestBuilds.Packets, "tw-454", 4));
        Assert.Equal("LobbyMinigameContext", DynamicConfigWire.CarrierOf(TestBuilds.Packets, "tw-454", 3));
        Assert.Throws<ConfigException>(() => DynamicConfigWire.CarrierOf(TestBuilds.Packets, "tw-454", 14));
    }
}
