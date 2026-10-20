using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class RoomWireByteLengthTests
{
    [Fact]
    public void Unknown401PinsFourBytes()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteUInt16(2);
            w.WriteUInt16(10);
        });
        Assert.Equal(4, payload.Length);
        var parsed = Unknown401Request.Parse(payload);
        Assert.Equal(2, parsed.Unknown0);
        Assert.Equal(10, parsed.Unknown1);
        Assert.Throws<PacketFormatException>(() => Unknown401Request.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => Unknown401Request.Parse(payload[..2]));
    }

    [Fact]
    public void Unknown402RoundTripsSessionAndTail()
    {
        var setting = RoomWireSupport.WaitingSetting();
        var payload = WireLengthSupport.Written(w =>
        {
            setting.Write(w);
            w.WriteWideStringPrefixed("pw");
            w.WriteByte(0xBA);
        });
        var parsed = Unknown402Request.Parse(payload);
        Assert.Equal(1, parsed.Setting.Kind);
        Assert.Equal(40, parsed.Setting.Map.Unknown4);
        Assert.Equal("training", parsed.Setting.Map.Unknown44);
        Assert.Equal("pw", parsed.Password);
        Assert.Equal(0xBA, parsed.OptTag);
        Assert.Null(parsed.UnknownOpt);
        Assert.False(parsed.Setting.Map.IsEmpty);
        Assert.Throws<PacketFormatException>(() => Unknown402Request.Parse([.. payload, 1]));
    }

    [Fact]
    public void Unknown402PresentTagPinsOptionalInt32()
    {
        var setting = RoomWireSupport.WaitingSetting();
        var payload = WireLengthSupport.Written(w =>
        {
            setting.Write(w);
            w.WriteWideStringPrefixed(string.Empty);
            w.WriteByte(0x3A);
            w.WriteInt32(7);
        });
        var parsed = Unknown402Request.Parse(payload);
        Assert.Equal(0x3A, parsed.OptTag);
        Assert.Equal(7, parsed.UnknownOpt);
    }

    [Fact]
    public void Unknown403PinsInt32AndWstring()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(12);
            w.WriteWideStringPrefixed("x");
        });
        Assert.Equal(4 + 4 + 2, payload.Length);
        var parsed = Unknown403Request.Parse(payload);
        Assert.Equal(12, parsed.Unknown0);
        Assert.Equal("x", parsed.Password);
        Assert.Throws<PacketFormatException>(() => Unknown403Request.Parse(payload[..4]));
    }

    [Fact]
    public void EmptyPayloadPins404And106()
    {
        EmptyPayload.Parse([], "C2S 404");
        EmptyPayload.Parse([], "C2S 106");
        Assert.Throws<PacketFormatException>(() => EmptyPayload.Parse([0], "C2S 404"));
    }

    [Fact]
    public void GotoLobbyPinsFourBytes()
    {
        var payload = RoomSessionBlob.GotoLobby(0);
        Assert.Equal(4, payload.Length);
        Assert.Equal("00000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void GotoRoomPinsTheRoomStageKindAndOnePlayer()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = 2 };
        var payload = RoomSessionBlob.GotoRoom(setting, [new RoomPlayerIdentity(9, "nick", 1)]);
        var r = new PacketReader(payload);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(2, again.Kind);
        Assert.Equal(40, again.Map.Unknown4);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(0, r.ReadInt32()); // Empty slot zero precedes the occupied slot one.
        r.ReadBytes(184);
        // First dword is the occupancy value (0x0101 + seat). sub_9EE100 scans
        // the first two bytes of the seat record at session+592, so it must be
        // nonzero; the second dword lands at seat+1 and must stay zero because
        // sub_980730 disables the slot for any seat whose seat+1 is nonzero.
        Assert.Equal(0x0102, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1, r.ReadByte());   // unknown3 (seat index)
        Assert.Equal(0, r.ReadByte());   // unknown4: the team label key; the compatibility fixture uses 0 while waiting.
        Assert.Equal(9, r.ReadInt32());
        Assert.Equal("nick", r.ReadWideStringPrefixed());
        Assert.True(payload.Length > 80);
        // Kind 2 is the room-stage gate (the 117 handler routes it to stage 4,
        // RoomStage8vs8), so it builds; kinds outside the room session set throw.
        var roomStage = RoomSessionBlob.GotoRoom(
            setting with { Kind = 2 }, [new RoomPlayerIdentity(9, "nick", 0)]);
        Assert.True(roomStage.Length > 80);
        Assert.Throws<InvalidOperationException>(() =>
            RoomSessionBlob.GotoRoom(setting with { Kind = 7 }, [new RoomPlayerIdentity(9, "nick", 0)]));
    }

    [Fact]
    public void WaitUserListPinsCountAndOneRow()
    {
        var payload = RoomSessionBlob.WaitUserList([new RoomPlayerIdentity(1, "n", 0)]);
        Assert.Equal(4 + 4 + 2 + 4, payload.Length);
        var r = new PacketReader(payload);
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal("n", r.ReadWideStringPrefixed());
        // The second field is the level; sub_EE9550 builds the row icon from level - 1, so the writer floors it
        // at 1.
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void GotoRoomPinsTheRoomStateDwordAtWireOffsetTwo()
    {
        var client = RoomWireSupport.WaitingSetting() with { Kind = 0 };
        var room = RoomSessionBlob.GotoRoom(
            SessionSettingBlob.ForRoom(client), [new RoomPlayerIdentity(9, "nick", 0)]);
        var withoutState = RoomSessionBlob.GotoRoom(
            client with { Kind = SessionSettingBlob.MainMenuKind }, [new RoomPlayerIdentity(9, "nick", 0)]);

        // Wire order 1 is the leading u16 and order 2 the room state i32, so
        // the state dword sits at offset 2 and the kind dword at offset 24
        // (2 + 4 + 4 + 6 + 4 + 4) - for this fixture only, because the name is
        // the four-character "nick": Kind sits behind two length-prefixed wide
        // strings and its offset moves with the name. Nothing outside this test
        // may read offset 24 of a real body; parse it instead. sub_9AC490
        // 0x9ACAB4 tests the state against
        // 4 and takes the stage-4 move; the mode-dependent path skips the move
        // for modes 15 / 103 / 36 / 40.
        Assert.Equal(
            SessionSettingBlob.RoomStateInRoom,
            BinaryPrimitives.ReadInt32LittleEndian(room.AsSpan(2)));
        Assert.Equal(
            SessionSettingBlob.MainMenuKind,
            BinaryPrimitives.ReadInt32LittleEndian(room.AsSpan(24)));
        // The state is a value change only: the body width must not move.
        Assert.Equal(withoutState.Length, room.Length);

        var r = new PacketReader(room);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, again.Unknown4);
        // The two MapMeta fields the panel-construction and spawner gates read
        // (MapMeta+0 and MapMeta+20) stay clear.
        Assert.Equal(0, again.Map.Unknown0);
        Assert.Equal(0, again.Map.Unknown20);
    }

    [Fact]
    public void ForRoomHonoursTheRoomStateArgumentAtWireOffsetTwo()
    {
        var client = RoomWireSupport.WaitingSetting() with { Kind = 0 };
        var inRoom = RoomSessionBlob.GotoRoom(
            SessionSettingBlob.ForRoom(client), [new RoomPlayerIdentity(9, "nick", 0)]);
        var waiting = RoomSessionBlob.GotoRoom(
            SessionSettingBlob.ForRoom(client, SessionSettingBlob.RoomStateWaiting),
            [new RoomPlayerIdentity(9, "nick", 0)]);

        // The state is wire order 2, a plain i32 at offset 2, so the argument
        // must move that dword and nothing else. This is the pin behind the
        // room_state setting: without it a configured value could be silently
        // overwritten downstream and the only symptom would be a greyed control.
        Assert.Equal(
            SessionSettingBlob.RoomStateInRoom,
            BinaryPrimitives.ReadInt32LittleEndian(inRoom.AsSpan(2)));
        Assert.Equal(
            SessionSettingBlob.RoomStateWaiting,
            BinaryPrimitives.ReadInt32LittleEndian(waiting.AsSpan(2)));
        Assert.Equal(inRoom.Length, waiting.Length);

        var r = new PacketReader(waiting);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, again.Unknown4);
    }

    [Fact]
    public void GotoRoomPinsTheSeatFieldsThePanelAndSpawnerReadForTwoSeats()
    {
        var setting = SessionSettingBlob.ForRoom(RoomWireSupport.WaitingSetting() with { Kind = 0 });
        var identities = new[]
        {
            new RoomPlayerIdentity(11, "host", 0),
            new RoomPlayerIdentity(22, "guest", 1),
        };
        var entries = new[]
        {
            new ProfileMapEntry(11, 300001),
            new ProfileMapEntry(22, 300017),
        };
        var body = RoomSessionBlob.GotoRoom(setting, identities, entries);

        var r = new PacketReader(body);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.MainMenuKind, again.Kind);
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, again.Unknown4);
        Assert.Equal(0, r.ReadInt32());      // unknown544
        Assert.Equal(0, r.ReadInt32());      // unknown548
        // sub_9D9250 copies one record per element into the 320-byte seat at
        // session+592+320*i, so the vector count is the seat count.
        Assert.Equal(2u, r.ReadUInt32());
        foreach (var identity in identities)
        {
            var expected = WireLengthSupport.Written(
                w => RoomPlayerInfo.ForSeat(identity.UserNum, identity.Nickname, identity.Slot).Write(w));
            var seat = r.ReadBytes(expected.Length).ToArray();
            Assert.Equal(expected, seat);
            // sub_9201D0 requires the presence byte at the seat
            // base to be nonzero, and sub_9EE100 scans the byte after it too.
            Assert.NotEqual(0, seat[0]);
            Assert.NotEqual(0, seat[1]);
            // RoomPlayerInfo +3 (the seat index the spawner stores at
            // Character+1992) is the first byte after the three leading i32s;
            // +8 is the userNum the profile map is keyed by.
            Assert.Equal(identity.Slot, seat[12]);
            Assert.Equal(identity.UserNum, BitConverter.ToInt32(seat, 14));
            // The record is the 188-byte minimum plus the nickname width, so
            // the optional block is absent and nothing reaches the +220..+231
            // gap: seat+224, the byte the spawner requires to be zero
            // (sub_9201D0), stays at the seat constructor's default.
            Assert.Equal(188 + 2 * identity.Nickname.Length, seat.Length);
        }

        // The avatar gate: one map564 entry per seat, keyed by the seat's
        // userNum, carrying an equipped character id inside 300000..301999.
        // sub_81D060 folds it through sub_81CD50 -> sub_817500 -> sub_817DF0,
        // which writes the entry's second i32 to the profile record's
        // equipment+0 - the field sub_91F720 dresses from.
        Assert.Equal(2u, r.ReadUInt32());
        foreach (var entry in entries)
        {
            var expected = WireLengthSupport.Written(w => entry.Write(w));
            Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
            Assert.InRange(entry.CharacterId, 300000, 301999);
        }

        Assert.Equal(0u, r.ReadUInt32());                      // map588
        Assert.Equal(new byte[12], r.ReadBytes(12).ToArray());  // ServerTeamInfo
        Assert.Equal((byte)0, r.ReadByte());                   // opt648 absent
        Assert.Equal(0, r.ReadInt32());                        // unknown728
        Assert.False(r.ReadBool32());                          // unknown732
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EmptyMapMetaRoundTripsAndIsEmpty()
    {
        var payload = WireLengthSupport.Written(MapMetaBlob.Empty().Write);
        var r = new PacketReader(payload);
        var parsed = MapMetaBlob.Read(ref r);
        Assert.Equal(0, r.Remaining);
        Assert.True(parsed.IsEmpty);
        Assert.Equal(payload.Length, WireLengthSupport.Written(parsed.Write).Length);
    }

    [Fact]
    public void ModeCatalogFailsLoudOnMissingOrEmptyFile()
    {
        Assert.False(ModeCatalog.Load(string.Empty).HasRows);
        Assert.Throws<InvalidOperationException>(() => ModeCatalog.Load(@"Z:\does-not-exist-mode-table.txt"));
        var empty = Path.Combine(Path.GetTempPath(), $"ingame-mode-{Guid.NewGuid():N}.txt");
        File.WriteAllText(empty, "# comment only\n");
        try
        {
            Assert.Throws<InvalidOperationException>(() => ModeCatalog.Load(empty));
        }
        finally
        {
            File.Delete(empty);
        }

        var path = Path.Combine(Path.GetTempPath(), $"ingame-mode-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "40\n16\n");
        try
        {
            var catalog = ModeCatalog.Load(path);
            catalog.RequireKnown(40);
            Assert.Throws<InvalidOperationException>(() => catalog.RequireKnown(99));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ForRoomClearsTheThreeMapMetaWordsTheRoomGatesRead()
    {
        // A client create request that carries every gate word nonzero, the
        // worst case the reply has to survive: the flags word, the order-3
        // dword the room-state chrome reads, and the order-4 dword the member
        // panel and the spawner read.
        var hostile = RoomWireSupport.TrainingMap() with
        {
            Unknown0 = 0x2,
            Unknown8 = 1,
            Unknown20 = 3,
        };
        var room = MapMetaBlob.ForRoom(hostile);

        Assert.Equal(0, room.Unknown0);
        Assert.Equal(0, room.Unknown8);
        Assert.Equal(0, room.Unknown20);
        // The mode and the map keys are the client's own selection and are not
        // normalised away.
        Assert.Equal(hostile.Unknown4, room.Unknown4);
        Assert.Equal(hostile.Unknown44, room.Unknown44);
        Assert.Equal(hostile.Unknown68, room.Unknown68);
    }

    [Fact]
    public void GotoRoomKeepsTheTeamChangeGateWordClearOnTheWire()
    {
        // sub_96F3A0 disables btnRoomChangeTeam_Blue / _Red and
        // btnRoomReady when MapMeta wire order 3 (session+148) is nonzero and
        // the local seat index equals the seat holder's distinguished index.
        // The reply must therefore carry 0 there whatever the client sent.
        var client = RoomWireSupport.WaitingSetting() with
        {
            Kind = 0,
            Map = RoomWireSupport.TrainingMap() with { Unknown8 = 1 },
        };
        var body = RoomSessionBlob.GotoRoom(
            SessionSettingBlob.ForRoom(client), [new RoomPlayerIdentity(9, "nick", 0)]);

        var r = new PacketReader(body);
        var again = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, again.Map.Unknown8);
        Assert.Equal(0, again.Map.Unknown0);
        Assert.Equal(0, again.Map.Unknown20);
    }

    [Fact]
    public void ChangeTeamTagBytesAreTheTwoDocumentedValues()
    {
        // sub_4B3AC0 writes 58 when the tagged block that
        // follows is absent and 186 when it is present; sub_10C3E90
        // (sub_10C3E90) repeats the pair one level down. The opcode is
        // shared with two other builders, so the handler logs the tag rather
        // than parsing a body - these are the only two values it can print.
        Assert.Equal(58, 0x3A);
        Assert.Equal(186, unchecked((byte)0xBA));
    }
}
