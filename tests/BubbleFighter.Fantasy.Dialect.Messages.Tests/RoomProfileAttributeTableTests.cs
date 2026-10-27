using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins nonempty room modifiers against kr-1256 sub_10D9410's native field sequence.</summary>
public sealed class RoomProfileAttributeTableTests
{
    [Fact]
    public void NonemptyRoomModifierRetainsItsExactWordBitsAndString()
    {
        const string Hex = "01000000E1930400010000005C190300000000000000000000000000"
            + "010000005C190300070000000400000030F8FFFF0000803F0000000007000000FFFFFFFF010000004100";
        var bytes = Convert.FromHexString(Hex);
        Assert.Equal(70, bytes.Length);
        var decoded = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "RoomProfileEntry", bytes);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, "kr-1256", "RoomProfileEntry", decoded));
        var reader = new PacketReader(bytes);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(300001, reader.ReadInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(203100, reader.ReadInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(203100, reader.ReadInt32());
        Assert.Equal(7u, reader.ReadUInt32());
        Assert.Equal(4u, reader.ReadUInt32());
        Assert.Equal(4294965296u, reader.ReadUInt32());
        Assert.Equal(0x3F800000u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(7u, reader.ReadUInt32());
        Assert.Equal(uint.MaxValue, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(new byte[] { 0x41, 0 }, reader.ReadBytes(2).ToArray());
        Assert.Equal(bytes.Length, reader.Offset);
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "RoomProfileEntry", bytes[..length]));
    }
}
