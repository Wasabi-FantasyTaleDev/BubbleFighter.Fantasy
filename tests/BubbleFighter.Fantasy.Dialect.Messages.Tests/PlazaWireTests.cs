using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte pins for the kr-812 plaza packets, byte for byte
/// (position upload and entry burst).
/// </summary>
public sealed class PlazaWireTests
{
    private static readonly RoomSchema Schema = TestBuilds.Of("kr-812").Schema;

    // The recorded entrant: twelve-character nickname, nineteen worn items, character item 300010,
    // level 10 (s2c#1771's record fields).
    private static readonly PlazaMember Entrant =
        new(0, 282, "TestPlayer01", 10, 300010, [.. Enumerable.Range(100000, 19)]);

    // c2s#1839 gcpSendSessionChrPos: unknown1 6, x, y, z, two zero bytes, heading.
    private const string RecordedUpload = "06 00 00 00 D8 52 31 44 58 5C 33 43 E4 FC 04 44 00 00 15 7A 43 C0";

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", ""));

    [Fact]
    public void ThePositionUploadIsTwentyTwoBytesAndItsTailIsCarriedVerbatim()
    {
        var upload = PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, "kr-812", Hex(RecordedUpload));

        Assert.Equal(22, upload.Body.Length);
        // The broadcast line: the identity and sequence in front, the 18-byte tail after the
        // upload's leading u32, verbatim.
        var line = PlayRoomCodec.WritePlazaPositionLine(TestBuilds.Packets, "kr-812", 282, 1, upload.Body.Span);
        Assert.Equal(26, line.Length);
        Assert.Equal(282, BitConverter.ToInt32(line, 0));
        Assert.Equal(1, BitConverter.ToInt32(line, 4));
        Assert.Equal(upload.Body.Span[4..].ToArray(), line[8..]);

        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, "kr-812", Hex(RecordedUpload)[..21]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadPositionUpload(TestBuilds.Packets, "kr-812", [.. Hex(RecordedUpload), 0]));
    }

    /// <summary>The complete native install preserves sparse seat indices and selects the real local slot.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-404")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void NativePlazaInstallKeepsSparseIndices(string build)
    {
        var own = Entrant with { Slot = 2 };
        var peer = new PlazaMember(0, 77, "peer", 9, 300001, [300001]);
        var body = PlayRoomCodec.WritePlazaInstall(TestBuilds.Packets, build, TestBuilds.Of(build).Schema, new(own, [peer]));
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", body);
        Assert.Equal(2, fields["local_slot"]);
        var setting = Assert.IsType<Dictionary<string, object?>>(fields["session_setting"]);
        Assert.Equal(3, setting["kind"]);
        var users = Assert.IsType<object?[]>(fields["user_slots"]);
        Assert.Equal(3, users.Length);
        Assert.Equal(77, Assert.IsType<Dictionary<string, object?>>(users[0])["user_number"]);
        var gap = Assert.IsType<Dictionary<string, object?>>(users[1]);
        Assert.Equal(false, gap["occupied"]);
        Assert.Equal(0, gap["user_number"]);
        Assert.Equal(282, Assert.IsType<Dictionary<string, object?>>(users[2])["user_number"]);
        Assert.Throws<ArgumentException>(() => PlayRoomCodec.WritePlazaInstall(TestBuilds.Packets, build,
            TestBuilds.Of(build).Schema, new(own, [peer with { Slot = own.Slot }])));
    }

    [Fact]
    public void TheSceneMoveAndSessionInitMatchTheRecordedFrames()
    {
        // s2c#1772 gspRepMoveInto and s2c#1775 gspReceiveSessionInit.
        Assert.Equal(Hex("01 00 00 00 3C 00 00 00 01 00 00 00"),
            PlayRoomCodec.WritePlazaMoveInto(TestBuilds.Packets, "kr-812"));
        Assert.Equal(Hex("1A 01 00 00"),
            PlayRoomCodec.WritePlazaSessionInit(TestBuilds.Packets, "id-1809", 282));
    }

    // S2C 970 gspRepNotifyChangeLevelIcon at 13:49:05.875: the entrant's main-menu UserSlot, 292 bytes.
    private const string RecordedSeatRecord =
        "010000000100000001000000FF1A0100000C000000540065007300740050006C006100790065007200300031000A0000" +
        "00000A00000000000000010000000100000001000000000000000000000013000000EE8601000F8701002F8701008087" +
        "01009187010092870100D6870100D7870100C0890100C1890100C2890100C3890100C4890100B68A0100E9200300EA93" +
        "04003611030010190300992803000000000000000000FFFFFFFF000000000000000000000000FFFFFFFFFFFFFFFFFFFF" +
        "FFFFFFFF000000000000000000000000FFFFFFFF0000000000000000000000000000000001000000000000000000FA43" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FFFFFFFF" +
        "00000000";

    [Fact]
    public void TheSeatRecordIsTheRecordedMainMenuRecord()
    {
        int[] worn = [100078, 100111, 100143, 100224, 100241, 100242, 100310, 100311, 100800, 100801, 100802,
            100803, 100804, 101046, 205033, 300010, 201014, 203024, 207001];
        var entrant = new PlazaMember(0, 282, "TestPlayer01", 10, 300010, worn);
        var expected = Hex(RecordedSeatRecord);
        // kr-812 sub_8F6A20 requires the +140 gate; the old menu-derived snapshot cleared it at wire byte178.
        expected[178] = 1;
        Assert.Equal(expected, PlayRoomCodec.WritePlazaSeatRecord(TestBuilds.Packets, "kr-812", Schema, entrant));
    }

    [Fact]
    public void TheSeatRecordAndSeatSlotHaveTheRecordedLengths()
    {
        // s2c#1771 gspRepNotifyChangeLevelIcon: the UserSlot alone.
        Assert.Equal(292, PlayRoomCodec.WritePlazaSeatRecord(TestBuilds.Packets, "kr-812", Schema, Entrant).Length);
        // s2c#1773 gspRoomSetSlot: the seat index, the UserSlot and the item list; after the seat
        // index and the three flags the record's identity starts (spawn-index byte, user number).
        var slot = PlayRoomCodec.WritePlazaSeatSlot(TestBuilds.Packets, "kr-812", Schema, Entrant);
        Assert.Equal(400, slot.Length);
        Assert.Equal(0, BitConverter.ToInt32(slot, 0));
        Assert.Equal(0xFF, slot[16]);
        Assert.Equal(282, BitConverter.ToInt32(slot, 17));
        // s2c#1768 gspRoomLeaveSlot: the seat index and the cleared record.
        var left = PlayRoomCodec.WritePlazaLeaveSeat(TestBuilds.Packets, "kr-812", Schema, Entrant);
        Assert.Equal(196, left.Length);
        Assert.Equal(0, BitConverter.ToInt32(left, 0));
        Assert.Equal(0xFF, left[16]);
        Assert.Equal(0, BitConverter.ToInt32(left, 17));
    }
}
