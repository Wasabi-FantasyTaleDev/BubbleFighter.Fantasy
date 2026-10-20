using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the KR812 uncompressed S2C 802 vector byte for byte.</summary>
public sealed class InventoryWireTests_Kr812
{
    private static readonly InventoryWire Wire = new(TestBuilds.Packets, "kr-812");

    [Fact]
    public void OneSlotMatchesRecordedFortyEightByteFrame()
    {
        // S2C 802, 48 bytes.
        var expected = Convert.FromHexString(
            "01000000208801001C270000000000808A914641" +
            "00000000000000000000000000000000000000000000000000000000");
        var slot = new InventorySlot(100384, 10012,
            BitConverter.Int64BitsToDouble(unchecked((long)0x4146918A80000000)),
            false, false, false, false, false, string.Empty);

        Assert.Equal(48, expected.Length);
        Assert.Equal(expected, Wire.RepInventoryAll([slot]));
    }

    [Fact]
    public void CountedEmptySlotsUseFortyFourBytesEach()
    {
        var slot = new InventorySlot(1, 2, 0, false, false, false, false, false, string.Empty);
        var payload = Wire.RepInventoryAll(Enumerable.Repeat(slot, 55).ToArray());

        // The 55-slot reply is 2424 bytes.
        Assert.Equal(2424, payload.Length);
        Assert.Equal(55, BitConverter.ToInt32(payload, 0));
        Assert.Equal([0, 0, 0, 0], Wire.RepInventoryAll([]));
    }

    [Fact]
    public void DeleteAndThrowLayoutsPinOneWordRequestAndTwoWordReplies()
    {
        var request = PacketCodec.Write(TestBuilds.Packets, "kr-812", "InventoryActionRequest",
            new Dictionary<string, object?> { ["item_id"] = 202001 });
        var delete = PacketCodec.Write(TestBuilds.Packets, "kr-812", "InventoryDeleteReply",
            new Dictionary<string, object?> { ["item_id"] = 202001, ["result"] = 1 });
        var throwReply = PacketCodec.Write(TestBuilds.Packets, "kr-812", "InventoryThrowReply",
            new Dictionary<string, object?> { ["item_id"] = 202001, ["result"] = 1 });

        Assert.Equal(4, request.Length);
        Assert.Equal(8, delete.Length);
        Assert.Equal(8, throwReply.Length);
        Assert.Equal(Convert.FromHexString("1115030001000000"), delete);
        Assert.Equal(delete, throwReply);
        Assert.Equal(202001, PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryActionRequest", request)["item_id"]);
        var deleted = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryDeleteReply", delete);
        Assert.Equal(202001, deleted["item_id"]);
        Assert.Equal(1, deleted["result"]);
        var fields = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryThrowReply", throwReply);
        Assert.Equal(202001, fields["item_id"]);
        Assert.Equal(1, fields["result"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryActionRequest", [1, 2, 3]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryDeleteReply", [.. delete, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryDeleteReply", delete[..4]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryThrowReply", throwReply[..4]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(TestBuilds.Packets, "kr-812", "InventoryThrowReply", [.. throwReply, 0]));
    }
}
