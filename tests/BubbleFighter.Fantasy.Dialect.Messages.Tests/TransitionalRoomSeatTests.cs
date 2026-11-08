using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins intermediate room seats against their own native readers, including distinct nested and tail values.</summary>
public sealed class TransitionalRoomSeatTests
{
    // cn-735 sub_425A50 / cn-738 sub_4295D0, including their nested social reader's eight-byte value.
    private const string Seat = "01000000000000000100000007443322110100000041000A000B00020C000000"
        + "010000000000000001000000000000000D0000000E000000020000000F00000010000000"
        + "1100000012000000130000000100000014000000000000001500000016001700180019001A00"
        + "1B0000001C000000010000001D00000001000000420001000000000000001E0000001F000000"
        + "200000002200000023000000240000002500000026000000270000002800000001000000"
        + "00000000004045402B0000002C0000002D000000";

    // cn-742 sub_42D560 delegates to cn-742 sub_4014C0, which consumes two words before the social record.
    private const string SeatWithTwoArenaWords = "01000000000000000100000007443322110100000041000A000B00020C000000"
        + "010000000000000001000000000000000D0000000E000000020000000F00000010000000"
        + "1100000012000000130000000100000014000000000000001500000016001700180019001A00"
        + "1B0000001C000000010000001D00000001000000420001000000000000001E0000001F000000"
        + "20000000210000002200000023000000240000002500000026000000270000002800000001000000"
        + "00000000004045402B0000002C0000002D000000";

    [Theory]
    [InlineData("cn-735", Seat, 180, 224, 232, 280)]
    [InlineData("cn-738", Seat, 168, 212, 216, 264)]
    [InlineData("cn-742", SeatWithTwoArenaWords, 168, 212, 224, 272)]
    public void DistinctFieldsPinNativeOffsetsAndEveryNestedWidth(
        string build, string hex, int textOffset, int arenaOffset, int socialOffset, int tailOffset)
    {
        var body = Convert.FromHexString(hex);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", body);
        Assert.Equal(0x11223344, fields["user_number"]);
        Assert.Equal("A", fields["nickname"]);
        Assert.Equal((byte)7, fields["controller_slot"]);
        Assert.Equal((byte)2, fields["team"]);
        Assert.True(Assert.IsType<bool>(fields["ready"]));
        Assert.False(Assert.IsType<bool>(fields["unknown_133"]));
        Assert.True(Assert.IsType<bool>(fields["unknown_134"]));
        Assert.False(Assert.IsType<bool>(fields["unknown_135"]));
        Assert.Equal(new object?[] { 15, 16 }, fields["equipped_items"]);
        Assert.Equal(28, fields["unknown_160"]);
        Assert.Equal(29, fields[$"unknown_{textOffset - 4}"]);
        Assert.Equal("B", fields[$"unknown_{textOffset}"]);
        Assert.Equal(30, fields[$"unknown_{textOffset + 32}"]);
        Assert.Equal(31, fields[$"unknown_{textOffset + 36}"]);
        Assert.Equal(32, fields[$"unknown_{arenaOffset}"]);
        for (var index = 0; index < 7; index++)
            Assert.Equal(34 + index, fields[$"unknown_{socialOffset + 4 + index * 4}"]);
        Assert.True(Assert.IsType<bool>(fields[$"unknown_{socialOffset + 32}"]));
        Assert.Equal(42.5, fields[$"unknown_{socialOffset + 40}"]);
        Assert.Equal(43, fields["unknown_152"]);
        Assert.Equal(44, fields[$"unknown_{tailOffset + 4}"]);
        Assert.Equal(45, fields[$"unknown_{tailOffset}"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "UserSlot", fields));
        Assert.DoesNotContain(TestBuilds.Packets.Resolve(build, "UserSlot"), field => field.Type == "f32");
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", [.. body, 0]));
    }

    [Theory]
    [InlineData("cn-735", Seat, 733)]
    [InlineData("cn-738", Seat, 733)]
    [InlineData("cn-742", SeatWithTwoArenaWords, 741)]
    public void MultiplayerInstallPreservesSeatAndEquipmentBoundaries(string build, string seatHex, int length)
    {
        // cn-735 sub_A4D6F0, cn-738 sub_A76920 and cn-742 sub_9BD720 read both counted vectors before the tail.
        var session = new byte[237];
        BinaryPrimitives.WriteUInt16LittleEndian(session, 7);
        BinaryPrimitives.WriteInt32LittleEndian(session.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(session.AsSpan(10), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(session.AsSpan(12), 8);
        BinaryPrimitives.WriteInt32LittleEndian(session.AsSpan(24), 2);
        var seat = Convert.FromHexString(seatHex);
        var equipment = Convert.FromHexString(
            "44332211EA9304000000000002000000A1860100A2860100000000000000000000000000");
        byte[] body = [.. session, 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0,
            .. seat, .. seat, 2, 0, 0, 0, .. equipment, .. equipment, 1, 0, 0, 0, 0x44, 0x55, 0x66, 0x77];
        Assert.Equal(length, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", body);
        Assert.Equal(1, fields["master_slot"]);
        Assert.Equal(0, fields["local_slot"]);
        Assert.Equal(0x77665544, fields["unknown_700"]);
        Assert.True(Assert.IsType<bool>(fields["party_transition"]));
        var seats = Assert.IsType<object?[]>(fields["user_slots"]);
        var equipmentLists = Assert.IsType<object?[]>(fields["equipped_item_lists"]);
        Assert.Equal(2, seats.Length);
        Assert.Equal(2, equipmentLists.Length);
        foreach (var value in seats)
            Assert.Equal(seat, PacketCodec.Write(TestBuilds.Packets, build, "UserSlot",
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value)));
        foreach (var value in equipmentLists)
            Assert.Equal(equipment, PacketCodec.Write(TestBuilds.Packets, build, "EquippedItemList",
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value)));
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "gspGotoRoom", fields));
        for (var missing = 1; missing <= 8; missing++)
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", body[..^missing]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", [.. body, 0]));
    }

    [Fact]
    public void LatestIntermediateSeatConsumesBothArenaWords()
    {
        var fields = PacketCodec.Read(TestBuilds.Packets, "cn-742", "UserSlot", Convert.FromHexString(SeatWithTwoArenaWords));
        Assert.Equal(33, fields["unknown_216"]);
        Assert.Equal(204, Convert.FromHexString(SeatWithTwoArenaWords).Length);
        Assert.Equal(200, Convert.FromHexString(Seat).Length);
    }

    [Theory]
    [InlineData("cn-735", Seat, 240)]
    [InlineData("cn-738", Seat, 240)]
    [InlineData("cn-742", SeatWithTwoArenaWords, 244)]
    public void SeatUpdateAndLeaveRetainTheirNativeEquipmentBoundary(string build, string seatHex, int updateLength)
    {
        // cn-735 sub_9F28D0, cn-738 sub_A208D0 and cn-742 sub_960A10 read one equipment record after the seat.
        var seat = Convert.FromHexString(seatHex);
        var equipment = Convert.FromHexString(
            "44332211EA9304000000000002000000A1860100A2860100000000000000000000000000");
        byte[] left = [7, 0, 0, 0, .. seat];
        byte[] updated = [.. left, .. equipment];
        Assert.Equal(updateLength, updated.Length);
        Pin("RoomSeatLeave", left, false);
        Pin("RoomSeatUpdate", updated, true);

        void Pin(string structure, byte[] body, bool hasEquipment)
        {
            var fields = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(7, fields["slot"]);
            Assert.Equal(seat, PacketCodec.Write(TestBuilds.Packets, build, "UserSlot",
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["user"])));
            Assert.Equal(hasEquipment ? 3 : 2, fields.Count);
            if (hasEquipment)
                Assert.Equal(equipment, PacketCodec.Write(TestBuilds.Packets, build, "EquippedItemList",
                    Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fields["equipment"])));
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, fields));
            for (var length = 0; length < body.Length; length++)
                Assert.Throws<PacketFormatException>(() =>
                    PacketCodec.Read(TestBuilds.Packets, build, structure, body[..length]));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
        }
    }

    [Theory]
    [InlineData("cn-735", 188, "unknown_176", "unknown_216", "unknown_280", "unknown_284")]
    [InlineData("cn-738", 188, "unknown_164", "unknown_204", "unknown_264", "unknown_268")]
    [InlineData("cn-742", 192, "unknown_164", "unknown_204", "unknown_272", "unknown_276")]
    public void RoomWriterRetainsReadyAndNativeResetTail(
        string build, int length, string indexField, string counterField, string firstTail, string secondTail)
    {
        var schema = TestBuilds.Of(build).Schema;
        foreach (var occupied in new[] { false, true })
        {
            var buffer = new ArrayBufferWriter<byte>();
            RoomCodec.WritePlayUserSlot(new PacketWriter(buffer), schema,
                new RoomSlotValues(occupied, false, 99, "", 300001, [], true));
            var body = buffer.WrittenSpan.ToArray();
            Assert.Equal(length, body.Length);
            var fields = PacketCodec.Read(TestBuilds.Packets, build, "UserSlot", body);
            Assert.Equal(occupied, fields["occupied"]);
            Assert.Equal(occupied, fields["ready"]);
            Assert.Equal(-1, fields[indexField]);
            Assert.Equal(1, fields[counterField]);
            Assert.Equal(1, fields["unknown_160"]);
            Assert.Equal(0, fields[firstTail]);
            Assert.Equal(0, fields[secondTail]);

            var envelope = RoomCodec.WriteSeatEnvelope(TestBuilds.Packets, build, schema, 3,
                new RoomSlotValues(occupied, false, 99, "", 300001, [], true));
            var wrapped = PacketCodec.Read(TestBuilds.Packets, build, "RoomSeatUpdate", envelope);
            Assert.Equal(3, wrapped["slot"]);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "UserSlot",
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(wrapped["user"])));
        }
    }

    [Theory]
    [InlineData("cn-735", false)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", true)]
    public void MainMenuRetainsItsOwnEnvelopeAndNestedSeat(string build, bool additionalArenaWord)
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        Assert.Equal(GotoRoomGotoMenuGoldenCodec.WriteMainMenu(room, additionalArenaWord),
            GotoRoomGotoMenuCodec.WriteMainMenu(TestBuilds.Packets, build, room));
    }
}
