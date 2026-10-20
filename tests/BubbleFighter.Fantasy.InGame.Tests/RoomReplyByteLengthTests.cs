using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the S2C 503 create-room reply body to the 212-byte minimum the client handler
/// <c>sub_9DA720</c> reads: <c>i32 slotIndex</c>, one <c>RoomPlayerInfo</c> (188 B minimum), two empty keyed
/// maps (4 B each) and an empty <c>ServerTeamInfo</c> (12 B). Field order and widths follow that client
/// handler.
/// </summary>
public sealed class RoomReplyByteLengthTests
{
    [Theory]
    [InlineData((short)1, "hello")]
    [InlineData((short)-3, "\u4F60\u597D")]
    [InlineData((short)0, "")]
    public void ChatPanelPlacesTextAtObject60AndUsesBooleanDwords(short actor, string text)
    {
        var payload = GameToClient.ChatPanel(actor, "name", 0, 0, text, -1, 0, 1, "", 0);
        Assert.Equal(42 + 2 * (4 + text.Length), payload.Length);
        var reader = new PacketReader(payload);
        Assert.Equal(unchecked((ushort)actor), reader.ReadUInt16());
        Assert.Equal("name", reader.ReadWideStringPrefixed());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("", reader.ReadWideStringPrefixed());
        Assert.Equal(text, reader.ReadWideStringPrefixed());
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("", reader.ReadWideStringPrefixed());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void CreateRoomReplyPinsTheTwoHundredTwelveByteMinimum()
    {
        var body = GameToClient.CreateRoomReply(0, RoomPlayerInfo.Default(5, string.Empty));

        Assert.Equal(212, body.Length);
    }

    [Fact]
    public void CreateRoomReplyPinsSlotIndexAndIdentityHead()
    {
        var body = GameToClient.CreateRoomReply(3, RoomPlayerInfo.Default(5, "guest"));

        var r = new PacketReader(body);
        Assert.Equal(3, r.ReadInt32());            // slotIndex
        // RoomPlayerInfo unknown0 is the occupancy dword; sub_9EE100 scans its
        // first two bytes at the seat-array base, so both must be nonzero.
        Assert.Equal(0x0101, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());            // unknown1: zero keeps the seat slot enabled
        Assert.Equal(0, r.ReadInt32());            // unknown2
        Assert.Equal((byte)0, r.ReadByte());       // unknown3
        Assert.Equal((byte)0, r.ReadByte());       // unknown4: the team label key; the compatibility fixture uses 0 while waiting.
        Assert.Equal(5, r.ReadInt32());            // userNum
        Assert.Equal("guest", r.ReadWideStringPrefixed());
    }

    [Fact]
    public void CreateRoomReplyEndsWithTheEmptyMapsAndTeamPanel()
    {
        var body = GameToClient.CreateRoomReply(0, RoomPlayerInfo.Default(1, string.Empty));

        // 4 + 188 consumed; then mapA count, mapB count, team map count, two
        // trailing team vectors: five zero u32.
        var tail = body[192..];
        Assert.Equal(20, tail.Length);
        Assert.Equal(new byte[20], tail);
    }

    [Fact]
    public void NicknameLengthAddsTwoBytesPerCharacter()
    {
        var shortName = GameToClient.CreateRoomReply(0, RoomPlayerInfo.Default(5, "ab"));
        var longName = GameToClient.CreateRoomReply(0, RoomPlayerInfo.Default(5, "abcd"));

        Assert.Equal(212 + 4, shortName.Length);
        Assert.Equal(212 + 8, longName.Length);
    }

    [Fact]
    public void OptionalBlockAddsNineBytesWhenTheOuterTagIsPresentAndSeventeenForBoth()
    {
        var absent = RoomPlayerInfo.Default(5, string.Empty);
        var present = absent with { Opt204Tag = RoomPlayerInfo.OptPresentTag, Unknown204 = 1, Unknown208 = 2 };
        var nested = present with { Opt212Tag = RoomPlayerInfo.OptPresentTag, Unknown212 = 3, Unknown216 = 4 };

        Assert.Equal(188, Measure(absent).Length);
        // The reader consumes the outer tag, two i32 and THEN a second tag, so a
        // present block is nine bytes over the absent form rather than the eight
        // this pin used to claim (sub_1143710).
        Assert.Equal(188 + 9, Measure(present).Length);
        Assert.Equal(188 + 17, Measure(nested).Length);
    }

    [Fact]
    public void SocialSystemInfoPinsFortyEightZeroBytes()
    {
        var body = Measure(SocialSystemInfoBlob.Empty());
        Assert.Equal(48, body.Length);
        Assert.Equal(new byte[48], body);
    }

    [Fact]
    public void ServerTeamInfoPinsTwelveEmptyBytes()
    {
        var body = Measure(ServerTeamInfoBlob.Empty());
        Assert.Equal(12, body.Length);
        Assert.Equal(new byte[12], body);
    }

    [Fact]
    public void ServerTeamInfoWritesTheMapEntryShape()
    {
        var team = new ServerTeamInfoBlob(
            [(2, [7, 8])],
            [9],
            []);

        var body = Measure(team);
        // map count(4) + key(1) + slot count(4) + 2 slots(8) + vecA count(4) + 1(4) + vecB count(4)
        Assert.Equal(4 + 1 + 4 + 8 + 4 + 4 + 4, body.Length);

        var r = new PacketReader(body);
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal((byte)2, r.ReadByte());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(8, r.ReadInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(9, r.ReadInt32());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RoomInfoSnapshotIsExactlyOneSessionSetting()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = SessionSettingBlob.MainMenuKind };
        var snapshot = RoomSessionBlob.RoomInfoSnapshot(setting);

        var w = PacketWriter.Create();
        setting.Write(w);
        var expected = ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();

        Assert.Equal(expected, snapshot);
        Assert.NotEmpty(snapshot);
    }

    [Fact]
    public void JoinHandshakePinsFourBytes()
    {
        var payload = RoomSessionBlob.JoinHandshake(0x11223344);
        Assert.Equal(4, payload.Length);
        Assert.Equal("44332211", Convert.ToHexString(payload));
    }

    [Fact]
    public void JoinSlotUpdatePinsTheEchoAndLiteralZero()
    {
        var payload = RoomSessionBlob.JoinSlotUpdate(0x11223344, 7, 9);
        Assert.Equal(16, payload.Length);
        Assert.Equal("44332211" + "00000000" + "07000000" + "09000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void GotoRoomWritesProfileEntriesForResolvedSeats()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = 2 };
        var entries = new[] { new ProfileMapEntry(9, 300001), new ProfileMapEntry(10, 300002) };
        var payload = RoomSessionBlob.GotoRoom(setting, [], entries);

        var r = new PacketReader(payload);
        _ = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0u, r.ReadUInt32());          // player count
        Assert.Equal(2u, r.ReadUInt32());          // map564 count
        Assert.Equal(9, r.ReadInt32());            // entry 1 key
        Assert.Equal(300001, r.ReadInt32());       // entry 1 character id
        Assert.Equal(0u, r.ReadUInt32());          // entry 1 vecA: empty
        Assert.Equal(0u, r.ReadUInt32());          // vecB
        Assert.Equal(0u, r.ReadUInt32());          // vecC
        Assert.Equal(16u, r.ReadUInt32());         // blob
        for (var i = 0; i < 4; i++)
            _ = r.ReadInt32();
        Assert.Equal(0u, r.ReadUInt32());          // items
        Assert.Equal(10, r.ReadInt32());           // entry 2 key
        Assert.Equal(300002, r.ReadInt32());       // entry 2 character id
        Assert.Equal(0u, r.ReadUInt32());          // entry 2 vecA: empty
        Assert.Equal(0u, r.ReadUInt32());          // vecB
        Assert.Equal(0u, r.ReadUInt32());          // vecC
        Assert.Equal(16u, r.ReadUInt32());         // blob
        for (var i = 0; i < 4; i++)
            _ = r.ReadInt32();
        Assert.Equal(0u, r.ReadUInt32());          // items
        Assert.Equal(0u, r.ReadUInt32());          // map588 count (empty)
    }

    [Fact]
    public void GotoRoomKeepsEmptyMapsWhenNoSeatResolves()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = 2 };
        var payload = RoomSessionBlob.GotoRoom(setting, [], []);

        var r = new PacketReader(payload);
        _ = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(0u, r.ReadUInt32());          // map564
        Assert.Equal(0u, r.ReadUInt32());          // map588
    }

    [Fact]
    public void GotoRoomAcceptsThePlazaStageKind()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = 3 };
        Assert.True(RoomSessionBlob.GotoRoom(setting, []).Length > 80);
    }

    [Fact]
    public void ProfileMapEntryPinsTheReferenceShape()
    {
        var payload = WireLengthSupport.Written(w => new ProfileMapEntry(5, 300001, 2).Write(w));

        // key(4) + character(4) + vecA(4) + vecB(4) + vecC(4) + blob(4 + 16) + items(4)
        Assert.Equal(4 + 4 + 4 + 4 + 4 + 20 + 4, payload.Length);
        var r = new PacketReader(payload);
        Assert.Equal(5, r.ReadInt32());
        Assert.Equal(300001, r.ReadInt32());
        Assert.Equal(0u, r.ReadUInt32());            // vecA: empty
        Assert.Equal(0u, r.ReadUInt32());            // vecB
        Assert.Equal(0u, r.ReadUInt32());            // vecC
        Assert.Equal(16u, r.ReadUInt32());           // blob
        // The 16-byte identity blob is written from the entry's own key, the
        // snapshot position and the character id, so the client's actor record
        // is keyed by the stable identity rather than a slot.
        Assert.Equal(5, r.ReadInt32());                 // identity
        Assert.Equal(2, r.ReadInt32());                 // snapshot position
        Assert.Equal(300001, r.ReadInt32());            // character item id
        Assert.Equal(5 ^ 300001 ^ 2, r.ReadInt32());    // identity check word
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RoomListPinsTheHeaderCountAndRecordShape()
    {
        var room = SessionSettingBlob.ForRoomListRow("my room", "town", 1, 7, 2, 8, 41);
        var body = GameToClient.RoomList(3, 1, 1, [room]);

        // Three i32 headers, a u32 count, then one SessionSetting (225 minimum
        // plus two bytes per character of its fifteen wstrings).
        var record = WireLengthSupport.Written(room.Write);
        Assert.Equal(4 + 4 + 4 + 4 + record.Length, body.Length);

        var r = new PacketReader(body);
        Assert.Equal(3, r.ReadInt32());            // header0 (page cursor echoed)
        // header1 carries 1: both official 101 snapshots do; the meaning is unverified.
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());            // header2 (total room count)
        Assert.Equal(1u, r.ReadUInt32());          // record count
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal((ushort)41, again.Unknown0);  // room id echoed on a row click
        Assert.Equal("my room", again.Unknown8);   // room name
        Assert.Equal("town", again.Unknown40);     // master nickname
        Assert.Equal(2, again.Unknown32);          // occupant count
        // The client count widget renders (+32 - +34) / +36, so the capacity sits in +36 and +34 stays 0 like
        // the official rows.
        Assert.Equal(0, again.Unknown34);
        Assert.Equal(8, again.Unknown36);
        Assert.Equal(SessionSettingBlob.MainMenuKind, again.Kind);
        Assert.Equal((ushort)1, again.Map.Unknown4);   // create mode
        Assert.Equal(7, again.Map.Unknown20);          // channel key
        // MapMeta+288 is 0: official rows carry 0 there and the client reads
        // the slot only on hoisted rows, which we never send.
        Assert.Equal(0, again.Map.Unknown288);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RoomListRowCarriesTheRoomIdAtWireOffsetZero()
    {
        // C2S 403 field 1 is the clicked row's SessionSetting+0, so the row must carry the room id there. It
        // replaces the zero the writer used to emit, so the record length is unchanged.
        var zero = SessionSettingBlob.ForRoomListRow("r", string.Empty, 1, 0, 0, 8, 0);
        var row = SessionSettingBlob.ForRoomListRow("r", string.Empty, 1, 0, 0, 8, 9);

        Assert.Equal(WireLengthSupport.Written(zero.Write).Length, WireLengthSupport.Written(row.Write).Length);
        Assert.Equal((ushort)0, zero.Unknown0);
        Assert.Equal((ushort)9, row.Unknown0);

        var body = GameToClient.RoomList(0, 1, 1, [row]);
        var r = new PacketReader(body);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());            // header1: official snapshots carry 1
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(1u, r.ReadUInt32());
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal((ushort)9, again.Unknown0);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RoomListWithNoRoomsIsTheSixteenByteEmptyForm()
    {
        // The handler emits header1=1 even on an empty page, matching both official snapshots.
        var body = GameToClient.RoomList(0, 1, 0, []);

        Assert.Equal(16, body.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, body);
    }

    [Fact]
    public void SeatRecordPinsSlotIndexRecordAndTeamPanel()
    {
        var body = GameToClient.SeatRecord(2, RoomPlayerInfo.ForSeat(5, "guest", 2));

        // 4 + 188 + 2*nickname + 12 (empty ServerTeamInfo); no keyed maps, unlike the S2C 503 create reply.
        Assert.Equal(4 + 188 + 2 * "guest".Length + 12, body.Length);
        var r = new PacketReader(body);
        Assert.Equal(2, r.ReadInt32());            // slotIndex
        Assert.Equal(0x0103, r.ReadInt32());       // occupancy dword (0x0101 + slot)
        Assert.Equal(0, r.ReadInt32());            // unknown1: zero keeps the seat slot enabled
        Assert.Equal(0, r.ReadInt32());            // unknown2
        Assert.Equal((byte)2, r.ReadByte());       // unknown3 (seat)
        Assert.Equal((byte)0, r.ReadByte());       // unknown4: the team label key; the compatibility fixture uses 0 while waiting.
        Assert.Equal(5, r.ReadInt32());            // userNum
        Assert.Equal("guest", r.ReadWideStringPrefixed());
    }

    [Fact]
    public void SeatChatPinsTheEightFieldMinimum()
    {
        var body = GameToClient.SeatChat(2, "hello");

        // u16 + i32 + wstring + wstring + i32 + i32 + wstring + i32
        Assert.Equal(2 + 4 + (4 + 2 * 0) + (4 + 2 * "hello".Length) + 4 + 4 + (4 + 2 * 0) + 4, body.Length);
        var r = new PacketReader(body);
        Assert.Equal((ushort)2, r.ReadUInt16());           // seat index
        Assert.Equal(0, r.ReadInt32());                    // unknown2
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed()); // unknown4
        Assert.Equal("hello", r.ReadWideStringPrefixed()); // unknown28: the chat line
        Assert.Equal(0, r.ReadInt32());                    // unknown52
        Assert.Equal(0, r.ReadInt32());                    // unknown56
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed()); // unknown60
        Assert.Equal(0, r.ReadInt32());                    // unknown84
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void SeatChatEmptyMessageIsThirtyBytes()
    {
        Assert.Equal(30, GameToClient.SeatChat(0, string.Empty).Length);
    }

    [Fact]
    public void SeatAttributePinsTheIndexAndValue()
    {
        var body = GameToClient.SeatAttribute(3, 0x24);

        Assert.Equal(4 + 1 + 1, body.Length);
        var r = new PacketReader(body);
        Assert.Equal(3, r.ReadInt32());
        Assert.Equal(0x24, r.ReadByte());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.Remaining);
    }

    private static byte[] Measure(RoomPlayerInfo info)
    {
        var w = PacketWriter.Create();
        info.Write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    /// <summary>Byte length of one all-empty <see cref="ProfileMapEntry"/>.</summary>
    private const int ProfileEntryBytes = 4 + 4 + 4 + 4 + 4 + 20 + 4;

    private static byte[] Measure(SocialSystemInfoBlob blob)
    {
        var w = PacketWriter.Create();
        blob.Write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    private static byte[] Measure(ServerTeamInfoBlob blob)
    {
        var w = PacketWriter.Create();
        blob.Write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

    private static byte[] Measure(ProfileMapEntry entry)
    {
        var w = PacketWriter.Create();
        entry.Write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
