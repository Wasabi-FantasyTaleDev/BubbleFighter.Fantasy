using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete inbound equipment vectors while preserving existing outbound room projections.</summary>
public sealed class EquipmentVectorTableTests
{
    private const string Header = "010000000200000003000000020000000400000005000000";
    private const string Flags = "02000000AABB";
    private const string Attributes = "0100000006000000070000000200000008000000090000000A000000010000004100";
    private const string Expiry = "010000000B000000000000000000F83F";

    /// <summary>Provides independently audited native bodies for old and later equipment vectors.</summary>
    public static TheoryData<string, string, int> Bodies => new()
    {
        { "kr-812", Header + Flags + Attributes + Expiry, 80 },
        { "cn-717", Header + Flags + Attributes + Expiry, 80 },
        { "cn-406", Header + Flags + Attributes + Expiry, 80 },
        { "id-400", Header + Flags + Attributes + Expiry, 80 },
        { "id-1809", Header + Flags + Attributes + Expiry, 80 },
        { "cn-706", Header + Flags + Attributes + Expiry, 80 },
        { "cn-324", Header + Flags, 30 },
        { "cn-279", "0100000002000000020000000400000005000000", 20 },
    };

    [Theory, MemberData(nameof(Bodies))]
    public void CompleteNativeVectorsRoundTripAndRejectTruncation(string build, string hex, int length)
    {
        var tables = TestBuilds.Packets;
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(length, bytes.Length);
        var decoded = PacketCodec.Read(tables, build, "EquippedItemList", bytes);
        Assert.Equal(1, decoded["user_number"]);
        Assert.Equal(2, decoded["character_item"]);
        Assert.Equal(new object?[] { 4, 5 }, decoded["equipped_items"]);
        Assert.Equal(bytes, PacketCodec.Write(tables, build, "EquippedItemList", decoded));
        if (length == 80)
        {
            Assert.Equal(new object?[] { (byte)0xAA, (byte)0xBB }, decoded["unknown_32"]);
            var attributes = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object?[]>(decoded["unknown_48"])));
            Assert.Equal(new object?[] { 8, 9 }, attributes["unknown_12"]);
            Assert.Equal("A", attributes["unknown_32"]);
            var expiry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object?[]>(decoded["unknown_64"])));
            Assert.Equal(11, expiry["unknown_4"]);
            Assert.Equal(1.5d, expiry["unknown_8"]);
        }
        else
        {
            Assert.False(decoded.ContainsKey("unknown_48"));
            Assert.False(decoded.ContainsKey("unknown_64"));
            if (build == "cn-324")
                Assert.Equal(new object?[] { (byte)0xAA, (byte)0xBB }, decoded["unknown_28"]);
        }
        for (var i = 0; i < bytes.Length; i++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "EquippedItemList", bytes[..i]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "EquippedItemList", [.. bytes, 0]));
    }

    [Theory]
    [InlineData("kr-812", 36)]
    [InlineData("cn-717", 36)]
    [InlineData("cn-406", 36)]
    [InlineData("id-400", 36)]
    [InlineData("id-1809", 36)]
    [InlineData("cn-706", 36)]
    [InlineData("cn-324", 28)]
    [InlineData("cn-279", 20)]
    public void OutboundEquipmentAndRoomProjectionKeepTheirPreviousBytes(string build, int length)
    {
        const string Prefix = "07000000E1930400";
        const string Items = "020000004433221188776655";
        var hex = length switch
        {
            20 => Prefix + Items,
            28 => Prefix + "00000000" + Items + "00000000",
            _ => Prefix + "00000000" + Items + "000000000000000000000000",
        };
        var bytes = EquipmentViewBinding.WriteView(TestBuilds.Packets, build, 7, 300001, [0x11223344, 0x55667788]);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(Convert.FromHexString(hex), bytes);
        var schema = TestBuilds.Of(build).Schema;
        var slot = new RoomSlotValues(true, false, 7, "test", 300001, [0x11223344, 0x55667788], false);
        var values = RoomCodec.PlaySeatValues(schema, slot, equipment: true);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "EquippedItemList", values));
        Assert.All(schema.EquippedItemList.Where(field => field.Type == RoomFieldType.EmptyVector),
            field => Assert.Null(values[field.Name]));
    }
}
