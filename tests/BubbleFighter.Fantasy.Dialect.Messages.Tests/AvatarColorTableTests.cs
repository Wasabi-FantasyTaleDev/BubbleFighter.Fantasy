using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Avatar material color uses the local stat word and the peer seat's independent half-word.</summary>
public sealed class AvatarColorTableTests
{
    private static readonly PacketTables Tables = PacketTables.Load(TestBuilds.PacketsDirectory());

    [Theory]
    [InlineData("kr-812", 380)]
    [InlineData("cn-717", 380)]
    [InlineData("id-400", 340)]
    [InlineData("id-1809", 344)]
    [InlineData("cn-324", 256)]
    [InlineData("cn-279", 168)]
    public void LocalStatCarriesColorWithoutChangingWalletOrFighterFields(string build, int length)
    {
        var values = new Dictionary<string, object?> { ["lucci"] = 123, ["cash"] = 456 };
        var unchanged = PacketCodec.Write(Tables, build, "UserStat", values);
        values["color_index"] = 14;
        var colored = PacketCodec.Write(Tables, build, "UserStat", values);
        Assert.Equal(length, colored.Length);
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32LittleEndian(colored));
        Assert.Equal(unchanged.AsSpan(4).ToArray(), colored.AsSpan(4).ToArray());
    }

    [Theory]
    [InlineData("kr-812", 42, 23)]
    [InlineData("cn-717", 42, 23)]
    [InlineData("id-400", 42, 23)]
    [InlineData("id-1809", 42, 23)]
    [InlineData("cn-324", 18, 23)]
    [InlineData("cn-279", 18, 23)]
    [InlineData("cn-706", 42, 23)]
    [InlineData("cn-406", 42, 23)]
    [InlineData("cn-404", 42, 23)]
    [InlineData("cn-408", 42, 23)]
    [InlineData("kr-1256", 40, 24)]
    public void PeerColorRetainsNativeOffsetWidthAndIndependentTeam(string build, int objectOffset, int wireOffset)
    {
        var field = Assert.Single(Tables.Resolve(build, "UserSlot"), field => field.Name == "color_index");
        Assert.Equal(objectOffset, field.Offset);
        Assert.Equal("u16", field.Type);
        var values = new Dictionary<string, object?> { ["team"] = 2 };
        var unchanged = PacketCodec.Write(Tables, build, "UserSlot", values);
        values["color_index"] = 14;
        var colored = PacketCodec.Write(Tables, build, "UserSlot", values);
        Assert.Equal(unchanged.Length, colored.Length);
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(colored.AsSpan(wireOffset)));
        colored[wireOffset] = colored[wireOffset + 1] = 0;
        Assert.Equal(unchanged, colored);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    public void SharedRoomWriterSuppliesColorForOccupiedAndDepartedSeatsOnly(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var slot = new RoomSlotValues(true, false, 42, "", 300001, [], false) { ColorIndex = 14, Team = 2 };
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WritePlayUserSlot(new PacketWriter(buffer), schema, slot);
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(buffer.WrittenSpan[23..]));

        buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WritePlayUserSlot(new PacketWriter(buffer), schema, slot with { Occupied = false, Departed = true });
        Assert.Equal(14, BinaryPrimitives.ReadUInt16LittleEndian(buffer.WrittenSpan[23..]));

        buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WritePlayUserSlot(new PacketWriter(buffer), schema, RoomSlotValues.Empty(false) with { ColorIndex = 14 });
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(buffer.WrittenSpan[23..]));
    }
}
