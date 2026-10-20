using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
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

    // s2c#1774 gspReceiveSessionChrPos: user number 282, sequence 1, the spawn step of the walk.
    private const string RecordedPositionLine = "1A 01 00 00 01 00 00 00 D8 52 31 44 58 9C 33 43 E4 FC 04 44 01 7F 00 00 00 00";

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

    [Fact]
    public void TheEntryWalkReplaysTheRecordedTwentyOneSteps()
    {
        // Step one has the reference line's length, identity, state bytes and height; only the
        // column differs, because the start is random per entry.
        var recorded = Hex(RecordedPositionLine);
        var first = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", 282, 1, 7);
        Assert.Equal(recorded.Length, first.Length);
        Assert.Equal(recorded[..8], first[..8]);
        Assert.Equal(recorded[12..16], first[12..16]);
        Assert.Equal(recorded[20..], first[20..]);

        // Step two (s2c#1788): the same column, one step lower, idle.
        var second = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", 282, 2, 7);
        Assert.Equal(first[8..12], second[8..12]);
        Assert.Equal(first[16..20], second[16..20]);
        Assert.Equal(26, second.Length);
        Assert.Equal((282, 2), (BitConverter.ToInt32(second, 0), BitConverter.ToInt32(second, 4)));
        Assert.Equal(179.6107177734375f - 0.25f, BitConverter.ToSingle(second, 12));
        Assert.Equal([0x00, 0x7F], new[] { second[20], second[21] });

        // The last step lands on the plaza floor and marks the arrival.
        var last = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", 282, 21, 7);
        Assert.Equal(21, BitConverter.ToInt32(last, 4));
        Assert.Equal(179.6107177734375f - 0.25f * 20 * 20, BitConverter.ToSingle(last, 12));
        Assert.Equal([0x02, 0x7F], new[] { last[20], last[21] });
        Assert.Equal(0f, BitConverter.ToSingle(last, 22));
    }

    [Fact]
    public void TheSpawnColumnStaysInTheRecordedBoxAndVariesPerEntry()
    {
        var seen = new HashSet<(float, float)>();
        for (var seed = 1; seed <= 200; seed++)
        {
            var line = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", 282, 1, seed);
            Assert.Equal(26, line.Length);
            var x = BitConverter.ToSingle(line, 8);
            var z = BitConverter.ToSingle(line, 16);
            Assert.InRange(x, 697f, 743f);
            Assert.InRange(z, 512f, 551f);
            Assert.Equal(179.6107177734375f, BitConverter.ToSingle(line, 12));
            seen.Add((x, z));
        }
        // 200 entries must not all land on one spot.
        Assert.True(seen.Count > 100);
    }

    [Fact]
    public void EveryReceiverOfOneEntryGetsTheSameSpawnLine()
    {
        // The entrant's own burst, a present member's burst and a late joiner's empty-position
        // line all carry the same member and entry value, so the bytes must be identical.
        var entrant = Entrant with { EntrySeed = 41 };
        var forEntrant = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", entrant.UserNumber, 1, entrant.EntrySeed);
        var forOthers = PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", entrant.UserNumber, 1, entrant.EntrySeed);
        Assert.Equal(forEntrant, forOthers);
        Assert.NotEqual(forEntrant, PlayRoomCodec.WritePlazaWalkStep(TestBuilds.Packets, "kr-812", entrant.UserNumber, 1, 42));
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
        Assert.Equal(Hex(RecordedSeatRecord), PlayRoomCodec.WritePlazaSeatRecord(TestBuilds.Packets, "kr-812", Schema, entrant));
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
        Assert.Equal(220, left.Length);
        Assert.Equal(0, BitConverter.ToInt32(left, 0));
        Assert.Equal(0xFF, left[16]);
        Assert.Equal(282, BitConverter.ToInt32(left, 17));
    }
}
