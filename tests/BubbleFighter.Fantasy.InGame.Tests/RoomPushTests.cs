using BubbleFighter.Fantasy.Contracts.Rooms;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the room push: which reply set a two-seat room receives and in which order. The client code
/// order is the S2C 117 install on every seat first - it fills the 36 x 320-byte seat array at
/// <c>session+592</c>, applies the <c>map564</c> profile entries, and takes the kind-2 branch to
/// <c>SetStage(4)</c>, the move that constructs <c>UiRoomStageUserList</c> - then the per-seat S2C 511 records
/// whose handler calls the spawner <c>sub_9201D0</c>, then the S2C 118 rows <c>sub_E7D7B0</c> renders into the
/// panel. S2C 118 before the stage move is dropped by <c>sub_9ACD00</c>'s <c>RoomStage8vs8</c> cast.
/// </summary>
public sealed class RoomPushTests
{
    [Fact]
    public async Task PushRoomSendsTheInstallThenThePartyUpdateThenTheRoomInfo()
    {
        // This minimal installer omits optional signature and equipment services.
        // S2C 201 spawns each occupied character (0x9064B0 -> 0x920940).
        var (room, host, guest) = TwoSeatRoom();
        var loop = new RoomLoop(
            new RoomRegistry(new InGameOptions()), new ModeCatalog(), new InGameOptions(), "127.0.0.1",
            NullLogger.Instance);

        await loop.PushRoomAsync(room, CancellationToken.None);

        Assert.Equal(
            new ushort[]
            {
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
            },
            host.Opcodes);
        Assert.Equal(host.Opcodes, guest.Opcodes);
    }

    [Fact]
    public async Task PushRoomWritesBothSeatsAndBothProfileEntriesIntoTheInstall()
    {
        var (room, host, _) = TwoSeatRoom();

        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);

        var r = new PacketReader(host.Body(InGameOpcodes.S2CGotoRoom));
        var setting = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.MainMenuKind, setting.Kind);
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, setting.Unknown4);
        Assert.Equal(0, r.ReadInt32());      // unknown544
        Assert.Equal(0, r.ReadInt32());      // unknown548
        Assert.Equal(2u, r.ReadUInt32());    // the players vector: both seats
        foreach (var (userNum, nickname, slot) in new[] { (11, "host", (byte)0), (22, "guest", (byte)1) })
        {
            var expected = WireLengthSupport.Written(
                w => RoomPlayerInfo.ForSeat(userNum, nickname, slot).Write(w));
            Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
        }

        // One map564 entry per resolved seat: the avatar gate needs a profile
        // record for each seat's userNum with a character id in 300000..301999.
        // The entry's identity blob carries the snapshot position, so the
        // first seat is position 0 and the second is position 1.
        Assert.Equal(2u, r.ReadUInt32());
        foreach (var (userNum, characterId, position) in new[] { (11, 300001, 0), (22, 300017, 1) })
        {
            var expected = WireLengthSupport.Written(
                w => new ProfileMapEntry(userNum, characterId, position).Write(w));
            Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
        }

        Assert.Equal(0u, r.ReadUInt32());                      // map588
        Assert.Equal(new byte[12], r.ReadBytes(12).ToArray());  // ServerTeamInfo
        Assert.Equal((byte)0, r.ReadByte());                   // opt648 absent
        Assert.Equal(0, r.ReadInt32());                        // unknown728
        Assert.False(r.ReadBool32());                          // unknown732
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task PushRoomGivesEachSeatItsOwnPartyUpdate()
    {
        // sub_9064B0 writes a profile-map record per party member
        // (sub_9064B0) and spawns a Character per occupied seat
        // (sub_9064B0), so one S2C 201 per seat is what turns the 117's
        // seat list into visible characters. The static entry keeps the seat
        // records on as well, so both orders are pinned here.
        var (room, host, guest) = TwoSeatRoom();

        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);

        Assert.Equal(0, BitConverter.ToInt32(host.First(InGameOpcodes.S2CSeatRecord)));
        Assert.Equal(1, BitConverter.ToInt32(host.Last(InGameOpcodes.S2CSeatRecord)));
        Assert.Equal(0, BitConverter.ToInt32(guest.First(InGameOpcodes.S2CSeatRecord)));
        Assert.Equal(1, BitConverter.ToInt32(guest.Last(InGameOpcodes.S2CSeatRecord)));

        // One party update per seat, delivered to that seat's own session, so the
        // host sees exactly its own record and the guest sees its own.
        Assert.Equal(1, host.Opcodes.Count(o => o == InGameOpcodes.S2CUpdateParty));
        Assert.Equal(1, guest.Opcodes.Count(o => o == InGameOpcodes.S2CUpdateParty));
        var mine = new PacketReader(host.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, mine.ReadInt32());          // dword_18E5660+4
        Assert.Equal(1u, mine.ReadUInt32());        // member count
        Assert.Equal(11, mine.ReadInt32());         // the host's UserNum
        var theirs = new PacketReader(guest.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, theirs.ReadInt32());
        Assert.Equal(1u, theirs.ReadUInt32());
        Assert.Equal(22, theirs.ReadInt32());       // the guest's UserNum
    }

    [Fact]
    public async Task RepublishRoomUiSendsTheUserListToEverySeat()
    {
        // The per-seat S2C 511 records are gone: the client does not need them, and sub_9DB1E0's
        // uninitialised byte can take sub_920060's destroy branch.
        var (room, host, guest) = TwoSeatRoom();

        await new RoomLoop(
                new RoomRegistry(new InGameOptions()), new ModeCatalog(), new InGameOptions(),
                "127.0.0.1", NullLogger.Instance)
            .RepublishRoomUiAsync(room, CancellationToken.None);

        Assert.Equal(new ushort[] { InGameOpcodes.S2CWaitUserList }, host.Opcodes);
        Assert.Equal(new ushort[] { InGameOpcodes.S2CWaitUserList }, guest.Opcodes);

        var rows = new PacketReader(host.Body(InGameOpcodes.S2CWaitUserList));
        Assert.Equal(2u, rows.ReadUInt32());
        Assert.Equal("host", rows.ReadWideStringPrefixed());
        Assert.True(rows.ReadInt32() >= 1);
        Assert.Equal("guest", rows.ReadWideStringPrefixed());
        Assert.True(rows.ReadInt32() >= 1);
        Assert.Equal(0, rows.Remaining);
    }

    [Fact]
    public void TrainingRoomRequestPinsTheFiftyThreeByteBody()
    {
        // The btnGoToTrainingRoom click arm writes the 56-byte rule record as thirteen i32 plus one u8 and
        // nothing else - no SessionSetting, no MapMeta, no mode, no map name.
        Assert.Equal(53, TrainingRoomRequest.BodyBytes);
        var body = new byte[TrainingRoomRequest.BodyBytes];
        for (var i = 0; i < 13; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4), 100 + i);
        body[52] = 0x80;

        var parsed = TrainingRoomRequest.Parse(body);

        Assert.Equal(100, parsed.Rules.EndGameTimeOver);
        Assert.Equal(101, parsed.Rules.EndGameNotifyWinOrLoseTickTeam);
        Assert.Equal(106, parsed.Rules.DeathMatchKill);
        Assert.Equal(107, parsed.Rules.DeathMatchTimeMs);
        Assert.Equal(111, parsed.Rules.Unknown44);
        Assert.Equal(112, parsed.Rules.Unknown48);
        Assert.Equal(0x80, parsed.Rules.Flags52);
    }

    [Fact]
    public void TrainingRoomRequestRejectsAnyOtherLength()
    {
        Assert.Throws<PacketFormatException>(() => TrainingRoomRequest.Parse(new byte[52]));
        Assert.Throws<PacketFormatException>(() => TrainingRoomRequest.Parse(new byte[54]));
        Assert.Throws<PacketFormatException>(() => TrainingRoomRequest.Parse([]));
    }

    [Fact]
    public void TrainingRoomKeepsTheRoomStateSoModeFortyStillMovesTheStage()
    {
        // The kind-2 branch tests session+12 against 4 at sub_9AC490 0x9ACAB4 and only then compares the mode
        // against 40 at 0x9ACB62, so the room state must stay 4 or the stage move is skipped and the room stage
        // never runs. The training map is repeated in the four map-selection strings (orders 10-13, the
        // selected / current / difficulty / scene map): sub_113D030 reads them as four consecutive wstrings,
        // and a tree whose training entry works on a live client fills all four with the same row, while
        // leaving the secondary keys empty produces a session whose registry cannot resolve the later scene
        // bootstrap.
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(30);
        var (room, seat) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 41, "trainee");

        Assert.Equal(RoomRegistry.TrainingModeId, room.ModeId);
        Assert.Equal((ushort)RoomRegistry.TrainingModeId, room.Setting.Map.Unknown4);
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, room.Setting.Unknown4);
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
        Assert.Equal(RoomRegistry.TrainingMapName, room.Setting.Map.Unknown44);
        Assert.Equal(RoomRegistry.TrainingMapName, room.Setting.Map.Unknown68);
        Assert.Equal(RoomRegistry.TrainingMapName, room.Setting.Map.Unknown92);
        Assert.Equal(RoomRegistry.TrainingMapName, room.Setting.Map.Unknown116);
        Assert.Equal(0, room.Setting.Map.Unknown0);
        Assert.Equal(0, room.Setting.Map.Unknown20);
        Assert.Equal(0, seat.Slot);
        Assert.Equal(41, seat.UserNum);
        Assert.Equal("trainee", seat.Nickname);
        Assert.Equal(1, seat.Level);
    }

    [Fact]
    public async Task TrainingRoomPushesTheInstallThenTheSeatRecordThenTheUserList()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(31);
        var (room, seat) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 42, "solo");
        seat.CharacterId = 300001;

        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);

        Assert.Equal(
            new ushort[]
            {
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
                InGameOpcodes.S2CSeatRecord,
            },
            session.Opcodes);
        var party = new PacketReader(session.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, party.ReadInt32());          // dword_18E5660+4; sub_99D510 requires 2
        Assert.Equal(1u, party.ReadUInt32());        // member count
        Assert.Equal(42, party.ReadInt32());         // member +4: the seat's UserNum
        Assert.Equal("solo", party.ReadWideStringPrefixed());
        for (var i = 0; i < 4; i++)
            party.ReadInt32();                       // member +32..+44
        Assert.Equal(1u, party.ReadUInt32());        // profile-map entry count
        Assert.Equal(42, party.ReadInt32());         // entry key: the seat's UserNum
        Assert.Equal(300001, party.ReadInt32());     // entry payload: the equipped character id
        Assert.Equal(0u, party.ReadUInt32());        // entry vecA: empty
        Assert.Equal(0u, party.ReadUInt32());        // entry vecB
        Assert.Equal(0u, party.ReadUInt32());        // entry vecC
        Assert.Equal(0u, party.ReadUInt32());        // entry blob: zero-length
        Assert.Equal(0u, party.ReadUInt32());        // entry items
        Assert.Equal(0, party.Remaining);
    }




    [Fact]
    public void EnterGameStageDefaultIsTheThirtyNineByteMinimum()
    {
        // sub_10D1C10 allocates three list heads (object +96, +104, +112) and
        // sub_10D1D70 fills all three, so the client's minimum body is
        // 3x4 (wstring prefixes) + 4 + 4 + 1 + 1 + 4 + 1 + 4 + 4 + 4 = 39
        // bytes. Writing only two lists leaves the body four bytes short.
        Assert.Equal(39, WireLengthSupport.Written(EnterGameStageRecord.Default.Write).Length);
    }

    [Fact]
    public void EnterGameStageForMapCarriesTheMapKeysAndTheRoomState()
    {
        // sub_9E5E70 copies the record's first three wstrings into session+184 / +208 / +232 (sub_9E5E70
        // then:180-203), which are the MapMeta map-selection slots unknown44 / unknown68 / unknown92. An empty
        // triple therefore erases the map the S2C 117 install wrote. The dispatcher also copies the order-4 i32
        // into session+12 (sub_9D9930, v19[18] = record +72) - the room state five UI gates test as equal to
        // four - so a zero there erases the room state for the whole scene.
        var record = EnterGameStageRecord.ForMap("rel_trainingRoom01", SessionSettingBlob.RoomStateInRoom);
        var body = WireLengthSupport.Written(record.Write);

        var r = new PacketReader(body);
        Assert.Equal("rel_trainingRoom01", r.ReadWideStringPrefixed());
        Assert.Equal("rel_trainingRoom01", r.ReadWideStringPrefixed());
        Assert.Equal("rel_trainingRoom01", r.ReadWideStringPrefixed());
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());   // unknown4
        Assert.Equal(0, r.ReadByte());    // unknown5: the seat+4 gate value; 36 would skip the seat
        Assert.Equal(0, r.ReadByte());    // unknown6
        Assert.Equal(0, r.ReadInt32());   // unknown7
        Assert.Equal(0, r.ReadByte());    // unknown8
        Assert.Equal(0u, r.ReadUInt32()); // unknown9: empty list
        Assert.Equal(0u, r.ReadUInt32()); // unknown10: empty list (i32,i32,i32 elements)
        Assert.Equal(0u, r.ReadUInt32()); // unknown11: empty list
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void TrainingRoomSeatsOnlyThePlayerBecauseTheDummiesAreSceneObjects()
    {
        // The training ground's dummies are ChrTrainingDummy scene objects the
        // NpcGameStage builds from the game configuration: sub_840F00 reads the
        // Dummyposition and Dummyboundary keys and the class is constructed at
        // sub_47DF90. They are not room seats, so the server sends nothing for
        // them and the room holds exactly one seat - the player. An earlier
        // attempt seated extra players to make them appear; that was wrong
        // because the client counts seats as players.
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(50);
        var (room, seat) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 44, "trainee");

        Assert.Single(room.Seats);
        Assert.Equal(44, seat.UserNum);
        Assert.Equal("trainee", seat.Nickname);
        Assert.Equal((byte)0, seat.Slot);
        Assert.Equal(RoomRegistry.TrainingModeId, room.ModeId);
    }

    [Fact]
    public async Task TrainingRoomPushCarriesOnlyThePlayerSeat()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(51);
        var (room, seat) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 45, "trainee");
        seat.CharacterId = 300001;

        await RoomPush.PushRoomStaticAsync(room, CancellationToken.None);

        // One install, the room-info snapshot, the S2C 201 party update for the
        // single seat, one S2C 511 seat record: no extra seats are invented for
        // the dummies.
        Assert.Equal(
            new ushort[]
            {
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
                InGameOpcodes.S2CSeatRecord,
            },
            session.Opcodes);

        var install = new PacketReader(session.Body(InGameOpcodes.S2CGotoRoom));
        var setting = SessionSettingBlob.Read(ref install);
        Assert.Equal(SessionSettingBlob.MainMenuKind, setting.Kind);
        Assert.Equal(0, install.ReadInt32());      // unknown544
        Assert.Equal(0, install.ReadInt32());      // unknown548
        Assert.Equal(1u, install.ReadUInt32());    // the players vector: one row

        // The S2C 201 party update carries that one seat's identity and equipped
        // character id in the profile-map entry, so the spawner has what it needs.
        var party = new PacketReader(session.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, party.ReadInt32());          // dword_18E5660+4
        Assert.Equal(1u, party.ReadUInt32());        // member count
        Assert.Equal(45, party.ReadInt32());         // the trainee's UserNum
        Assert.Equal("trainee", party.ReadWideStringPrefixed());
        for (var i = 0; i < 4; i++)
            party.ReadInt32();                       // member +32..+44
        Assert.Equal(1u, party.ReadUInt32());        // profile-map entry count
        Assert.Equal(45, party.ReadInt32());         // entry key
        Assert.Equal(300001, party.ReadInt32());     // entry payload: the equipped character id
        Assert.Equal(0u, party.ReadUInt32());        // entry vecA: empty
        Assert.Equal(0u, party.ReadUInt32());        // entry vecB
        Assert.Equal(0u, party.ReadUInt32());        // entry vecC
        Assert.Equal(0u, party.ReadUInt32());        // entry blob: zero-length
        Assert.Equal(0u, party.ReadUInt32());        // entry items
        Assert.Equal(0, party.Remaining);
    }

    [Fact]
    public void PlazaMapMoveOpcodeIsDeclaredForReferenceOnly()
    {
        // C2S 53 is registered by the lobby row, which replies with S2C 11 gspRepMoveInto (sub_9A6D30) - the
        // only post-login same-socket writer of the client's channel state ServerConnection+2508. A second
        // registration for the same opcode fails activation, so the InGame row only declares the constant for
        // reference.
        Assert.Equal((ushort)53, InGameOpcodes.C2SPlazaMapMove);
    }

    [Fact]
    public void RoundInitializeArmsANonZeroRoundTimer()
    {
        // sub_9E7570 passes the first i32 of S2C 608 to sub_6FEFC0 at
        // sub_9E7570, which arms the round timer (sub_6FEFC0). A zero
        // arms a round that is already over, and a value of a few seconds ends
        // it moments after it starts. The shipped fallback is the four minutes a
        // normal round lasts (S2C 608 for mode 1).
        var options = new InGameOptions();

        Assert.Equal(240_000, options.RoundUnknown0);
        Assert.Empty(options.Validate());

        var body = GameToClient.RoundInitialize(options.RoundUnknown0, options.RoundUnknown1);
        var r = new PacketReader(body);
        Assert.Equal(240_000, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(8, body.Length);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task TrainingRoundCarriesTheClientNoLimitValue()
    {
        // The training ground has no time limit, and the client code's own no-limit
        // encoding is 0x3FFFFFFF: sub_6FEFC0 writes it as the second timer's
        // ceiling at sub_6FEFC0 and derives that timer's end tick from it.
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var session = new RecordingSession(72);
        var (room, _) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 47, "trainee");

        var loop = new InGameFlows(options, registry, NullLogger.Instance);
        room.MarkStarting();
        room.StartPending = true;
        var seat = room.Seats[0];
        seat.SceneReady = seat.RelayConfirmed = seat.RelayInitialized = true;
        seat.Relay = new RecordingSession(73);
        await loop.Start.FlushPendingStartAsync(room, CancellationToken.None);
        await room.CountdownTask;

        var round = new PacketReader(session.Body(InGameOpcodes.S2CRoundInitialize));
        Assert.Equal(0x3FFFFFFF, round.ReadInt32());
    }

    [Fact]
    public void CreateFromRulesEchoesTheMapKeysTheRequestCarried()
    {
        // The three C2S 503 wstrings are the client's map selection in the order
        // sub_9D7570 writes them: the resolved map key (MapMeta unknown68,
        // session+208), the map record's MapName (unknown116), and the raw
        // selection (unknown44, session+184, possibly a Random* marker).
        // Echoing them back is what gives the room a scene key, because the
        // S2C 510 stage enter copies its own three map keys into those same
        // slots and an empty triple would erase the 117 install.
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(70);
        var map = MapMetaBlob.Empty() with
        {
            Unknown44 = "Random",
            Unknown68 = "rel_Village00_boomhill",
            Unknown92 = "rel_Village00_boomhill",
            Unknown116 = "Boomhill",
        };

        var (room, _) = registry.CreateFromRules(
            session, RoomWireSupport.SampleRules(), 46, "host", roomName: string.Empty, map);

        Assert.Equal("Random", room.Setting.Map.Unknown44);
        Assert.Equal("rel_Village00_boomhill", room.Setting.Map.Unknown68);
        Assert.Equal("rel_Village00_boomhill", room.Setting.Map.Unknown92);
        Assert.Equal("Boomhill", room.Setting.Map.Unknown116);
        // The room-stage gates stay cleared: ForRoom zeroes the flags word and
        // the order-4 dword, which is what lets the member panel build.
        Assert.Equal(0, room.Setting.Map.Unknown0);
        Assert.Equal(0, room.Setting.Map.Unknown20);
        Assert.Equal(SessionSettingBlob.MainMenuKind, room.Setting.Kind);
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, room.Setting.Unknown4);
    }

    [Fact]
    public void QuickCreateRequestPinsTheRuleFirstThenThreeStringsShape()
    {
        // sub_9D7440 writes the 56-byte rule record first, then three prefixed
        // wstrings and a trailing i32 (sub_9D7440). sub_99F580 fills them
        // from sub_99F9C0's resolved key, sub_10E49C0's MapName query and the
        // key again, abandoning the send when the key does not resolve.
        var body = new List<byte>();
        foreach (var value in new[] { 180000, 0, 0, 0, 0, 0, 20, 300000, 420000, 0, 0, 0, 0 })
            body.AddRange(BitConverter.GetBytes(value));
        body.Add(0); // rule-record flag byte
        foreach (var value in new[] { "rel_Village07_Easy02", "Easy02", "rel_Village07_Easy02" })
        {
            body.AddRange(BitConverter.GetBytes(value.Length));
            body.AddRange(Encoding.Unicode.GetBytes(value));
        }
        body.AddRange(BitConverter.GetBytes(0));

        var request = QuickCreateRoomRequest.Parse(body.ToArray());

        Assert.Equal("rel_Village07_Easy02", request.Unknown1);
        Assert.Equal("Easy02", request.Unknown2);
        Assert.Equal("rel_Village07_Easy02", request.Unknown3);
        Assert.Equal(180000, request.Rules.EndGameTimeOver);
        Assert.Equal(20, request.Rules.DeathMatchKill);
    }

    [Fact]
    public void SeatStateCarriesTheWaitingRoomTeamKey()
    {
        // unknown4 is the team-label key sub_980730 switches on. The waiting-room fixture
        // carries 0 there; the in-round value of slot + 1 is written by the client's own S2C 510 handler
        // through sub_9E4C80 (sub_9E5E70), never by this record, so carrying it here displayed the team-1
        // state in a mode-1 waiting room.
        var slot2 = RoomPlayerInfo.ForSeat(9, "nick", 2);
        var body = WireLengthSupport.Written(slot2.Write);
        var r = new PacketReader(body);

        Assert.Equal(0x0103, r.ReadInt32());   // occupancy dword
        Assert.Equal(0, r.ReadInt32());        // unknown1: zero keeps the slot enabled
        Assert.Equal(0, r.ReadInt32());        // unknown2
        Assert.Equal((byte)2, r.ReadByte());   // unknown3 (seat index)
        Assert.Equal((byte)0, r.ReadByte());   // unknown4 (team label key, waiting value 0)
        Assert.Equal(9, r.ReadInt32());        // userNum
        Assert.Equal("nick", r.ReadWideStringPrefixed());
        Assert.Equal((ushort)1, r.ReadUInt16()); // unknown36: reference value (appearance-path rank 2)
        Assert.Equal((ushort)0, r.ReadUInt16()); // unknown40: native default color, independent of team
    }

    [Fact]
    public async Task CreateRoomEntersTheGameStageWithTheRoomMapKeys()
    {
        // A live session showed an ordinary created room parking in RoomStage8vs8 forever - its twenty-second
        // C2S 517 poll is that stage's own timer - while the training room, which receives the stage enter,
        // shows its character. The stage enter carries the room's own map keys because sub_9E5E70 overwrites
        // session+184/+208/+232 from them, and the room state because the dispatcher copies it onto session+12.
        var options = new InGameOptions
        {
            EnterGameStageOnCreate = true,
            SendPlazaSnapshot = false,
        };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var session = new RecordingSession(80);
        session.SetLobbyPlayer(48, "host");

        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));   // empty room-name wstring
        await loop.HandleUnknown402Async(
            session, new InboundPacket(402, body.ToArray()), CancellationToken.None);

        Assert.Contains(InGameOpcodes.S2CEnterGameStage, session.Opcodes);
        var stage = new PacketReader(session.Body(InGameOpcodes.S2CEnterGameStage));
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, stage.ReadInt32());
    }

    [Fact]
    public async Task CreateRoomWithoutTheOptionKeepsTheWaitingStage()
    {
        // The default. A live session showed the stage enter on the create path
        // dropping the room straight into the round, sitting in the prepare
        // state and being ejected, so the waiting stage is the shipped
        // behaviour and the record is opt-in.
        var options = new InGameOptions { EnterGameStageOnCreate = false, SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var session = new RecordingSession(81);
        session.SetLobbyPlayer(49, "host");

        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));
        await loop.HandleUnknown402Async(
            session, new InboundPacket(402, body.ToArray()), CancellationToken.None);

        Assert.DoesNotContain(InGameOpcodes.S2CEnterGameStage, session.Opcodes);
    }


    [Fact]
    public async Task JoinerGetsTheStageEnterSoBothSidesShareOneStage()
    {
        // The host is moved to the game stage by its own S2C 510 on create. A
        // joiner that only receives the 117 install stays in the room stage, so
        // the two cannot see each other; the joiner therefore gets the same
        // record, carrying the room's map.
        var options = new InGameOptions
        {
            EnterGameStageOnCreate = true,
            RepublishRoomUiOnJoin = true,
            SendPlazaSnapshot = false,
        };        var registry = new RoomRegistry(options);
        var host = new RecordingSession(90);
        var (room, hostSeat) = registry.CreateFromSetting(
            host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        hostSeat.CharacterId = 300001;

        var guest = new RecordingSession(91);
        guest.SetLobbyPlayer(2, "guest");
        // The joiner is not pre-joined: the handler's first-join branch is what
        // registers the seat, exactly as the live client's C2S 517 does.
        var rooms = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var joinBody = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(joinBody, room.Id);
        await rooms.HandleJoinRoomAsync(
            guest, new InboundPacket(InGameOpcodes.C2SJoinRoom, joinBody), CancellationToken.None);

        Assert.Contains(InGameOpcodes.S2CEnterGameStage, guest.Opcodes);
        var stage = new PacketReader(guest.Body(InGameOpcodes.S2CEnterGameStage));
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, stage.ReadInt32());
    }

    [Fact]
    public async Task CreateReSendInstallsForTheAskingSeatOnly()
    {
        // The live client asks twice. Measured on the operator's run of
        // 2026-09-23 11:11 UTC: the create sent one kind-2 install and the client
        // stayed in the plaza, then it re-sent C2S 402 - and answering that with
        // only the S2C 503 left it outside on every attempt, where the older build
        // that re-pushed the room entered on the second one. So the re-ask does
        // have to carry an install; what it must not do is carry one for anybody
        // else, because the kind-2 arm rebuilds the room unconditionally.
        var options = new InGameOptions();
        var (registry, room, host, guest) = HostedRoom(options);
        await JoinAsync(registry, options, room, guest);

        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        // The create handler demands a finished login the same way the join one
        // does, and the helper built the host's seat directly through the registry.
        host.SetLobbyPlayer(11, "host");
        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));
        var hostInstallsBefore = host.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom);
        var guestFramesBefore = guest.Opcodes.Count();
        var guestInstallsBefore = guest.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom);

        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);

        Assert.Equal(hostInstallsBefore + 1, host.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
        // Offset 24 is the dword the handler's kind switch reads (kr-1256 sub_9AC490 at 0x9AC9D7)
        // and 2 is the arm that installs a room, whatever the constant is named.
        var install = new PacketReader(host.Body(InGameOpcodes.S2CGotoRoom));
        install.ReadBytes(24);
        Assert.Equal(SessionSettingBlob.MainMenuKind, install.ReadInt32());

        // The other occupant is untouched. It already holds one kind-2 install -
        // the join aimed at it - so the claim is that nothing is added by a
        // re-ask that never came from this connection.
        Assert.Equal(guestFramesBefore, guest.Opcodes.Count());
        Assert.Equal(guestInstallsBefore, guest.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
    }

    [Fact]
    public async Task InstallCarriesTheApplyingStateWhileTheSnapshotKeepsTheClickedOne()
    {
        // One client dword, two gates: the 117 install carries the state BOTH the kind-2 arms accept (they
        // converge on stage 4 for mode 1) AND the start click requires (2, sub_970590); the S2C 501 snapshot
        // re-carries the same waiting state. The verified defaults make both bodies agree, so the
        // configured-distinction shape below keeps the plumbing observable.
        var options = new InGameOptions { RoomState = 2 };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var session = new RecordingSession(140);
        session.SetLobbyPlayer(70, "host");

        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));
        await loop.HandleUnknown402Async(
            session, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);

        var install = new PacketReader(session.Body(InGameOpcodes.S2CGotoRoom));
        install.ReadUInt16();
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, install.ReadInt32());
        Assert.Equal(options.RoomInstallState, options.RoomState);

        var snapshot = new PacketReader(session.Body(InGameOpcodes.S2CRoomInfoSnapshot));
        snapshot.ReadUInt16();
        Assert.Equal(2, snapshot.ReadInt32());

        // Negative control: the two values above have to come from the fields, not
        // from a constant in the writer, so configuring them equal has to make the
        // bodies agree again.
        var merged = new InGameOptions { RoomState = 2, RoomInstallState = 2 };
        var mergedRegistry = new RoomRegistry(merged);
        var mergedLoop = new RoomLoop(
            mergedRegistry, new ModeCatalog(), merged, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var mergedSession = new RecordingSession(141);
        mergedSession.SetLobbyPlayer(71, "host2");
        await mergedLoop.HandleUnknown402Async(
            mergedSession, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);
        var mergedInstall = new PacketReader(mergedSession.Body(InGameOpcodes.S2CGotoRoom));
        mergedInstall.ReadUInt16();
        Assert.Equal(2, mergedInstall.ReadInt32());
    }

    [Fact]
    public async Task PrimingWritesTheStateIntoTheLiveSessionBeforeTheInstallArrives()
    {
        // The kind-2 apply test reads the session the client already holds:
        // `mov eax, dword_18E56A4` (kr-1256 sub_9AC490 at 0x9ACAAB) then
        // `cmp dword ptr [eax+0Ch], 4` (:0x9ACAB4). A value inside the install is
        // therefore read too late to gate that install - which is exactly what the
        // live client reported: room_install_state 4 changed nothing about the
        // first create. The packet that does reach a live session's session+12 is
        // the S2C 501 (kr-1256 sub_9D9930), so with the prime on, one has to
        // land before the 117 carrying 4, and the one after it still carries the
        // state the start click tests for.
        var options = new InGameOptions
        {
            RoomState = 2,
            PrimeSessionStateBeforeInstall = true,
        };
        var session = new RecordingSession(142);
        session.SetLobbyPlayer(72, "host");
        var loop = new RoomLoop(
            new RoomRegistry(options), new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance,
            null, new PlazaRoster());
        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));

        await loop.HandleUnknown402Async(
            session, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);

        var order = session.Opcodes.ToArray();
        var installAt = Array.IndexOf(order, InGameOpcodes.S2CGotoRoom);
        var firstSnapshotAt = Array.IndexOf(order, InGameOpcodes.S2CRoomInfoSnapshot);
        Assert.True(installAt >= 0 && firstSnapshotAt >= 0 && firstSnapshotAt < installAt,
            $"no S2C 501 preceded the install: order is {string.Join(",", order)}");
        // The prime itself is inert (the held session is destroyed at 0x9AC6C2 before the arm test reads the
        // 117's own field), so both the priming 501 and the install now carry the waiting state the start click
        // requires.
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, StateOf(session.First(InGameOpcodes.S2CRoomInfoSnapshot)));
        Assert.Equal(2, StateOf(session.Body(InGameOpcodes.S2CRoomInfoSnapshot)));

        // Negative control, and the shipped default: with the flag off the first
        // room packet on the burst is the install itself, so nothing primes the
        // dword the compare reads.
        var unprimed = new InGameOptions { RoomState = 2 };
        Assert.False(unprimed.PrimeSessionStateBeforeInstall);
        var plain = new RecordingSession(143);
        plain.SetLobbyPlayer(73, "host");
        var plainLoop = new RoomLoop(
            new RoomRegistry(unprimed), new ModeCatalog(), unprimed, "127.0.0.1", NullLogger.Instance,
            null, new PlazaRoster());
        await plainLoop.HandleUnknown402Async(
            plain, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);
        var plainOrder = plain.Opcodes.ToArray();
        Assert.True(
            Array.IndexOf(plainOrder, InGameOpcodes.S2CRoomInfoSnapshot)
            > Array.IndexOf(plainOrder, InGameOpcodes.S2CGotoRoom));
    }

    /// <summary>Reads the <c>session+12</c> dword out of a body that starts with one
    /// <c>SessionSetting</c>: the i32 after the leading <c>u16</c>.</summary>
    /// <param name="body">The S2C 117 or S2C 501 body.</param>
    private static int StateOf(byte[] body)
    {
        var reader = new PacketReader(body);
        reader.ReadUInt16();
        return reader.ReadInt32();
    }

    /// <summary>Reads the <c>SessionSetting.Unknown8</c> name slot out of a body
    /// that starts with one <c>SessionSetting</c>: the first prefixed wstring,
    /// after the leading <c>u16</c> and the state <c>i32</c> (wire order 3).</summary>
    /// <param name="body">The S2C 117 or S2C 501 body.</param>
    private static string NameOf(byte[] body)
    {
        var reader = new PacketReader(body);
        reader.ReadUInt16();
        reader.ReadInt32();
        return reader.ReadWideStringPrefixed();
    }

    private static SessionSettingBlob ReadFirstRoomListRow(byte[] body)
    {
        var reader = new PacketReader(body.AsSpan(16));
        return SessionSettingBlob.Read(ref reader);
    }

    [Fact]
    public async Task InstallCarriesTheTypedRoomNameInTheSettingNameSlot()
    {
        // 0x962200 places the title in setting+8. The separate password tail must not override it.
        var options = new InGameOptions { SendPlazaSnapshot = false, SendRoomList = true };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var session = new RecordingSession(160);
        session.SetLobbyPlayer(80, "host");

        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written((RoomWireSupport.WaitingSettingWithMap() with { Unknown8 = "TestRoom" }).Write));
        body.AddRange(BitConverter.GetBytes("private-tail".Length));
        body.AddRange(Encoding.Unicode.GetBytes("private-tail"));
        await loop.HandleUnknown402Async(
            session, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);

        Assert.Equal("TestRoom", NameOf(session.Body(InGameOpcodes.S2CGotoRoom)));
        Assert.Equal("TestRoom", NameOf(session.Body(InGameOpcodes.S2CRoomInfoSnapshot)));
        var room = registry.TryGet(session.Id);
        Assert.NotNull(room);
        Assert.Equal("TestRoom", room.RoomName);
        var list = new CapturingSender<RoomListSnapshot>();
        var listing = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster()) { RoomListSender = list };
        await listing.HandleRoomListAsync(session, new RoomListRequested(0), CancellationToken.None);
        var listed = Assert.Single(Assert.Single(list.Sent).Rooms);
        Assert.Equal("TestRoom", listed.Name);
        Assert.Equal("host", listed.MasterNickname);
        // The tail is the room password; the row flag makes the client prompt for it (sub_854C90).
        Assert.Equal("private-tail", room.Password);
        Assert.True(listed.HasPassword);
    }

    [Fact]
    public async Task CreateInstallStaysEmptyWhileNoNameIsKnown()
    {
        // The bounded half of parity item 1: the create-time install that runs
        // before any name is known - an empty C2S 402 tail - still reads empty.
        // Nothing is invented to fill the slot.
        var options = new InGameOptions { SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var session = new RecordingSession(161);
        session.SetLobbyPlayer(81, "host");

        var body = new List<byte>();
        body.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        body.AddRange(BitConverter.GetBytes(0));   // empty room-name wstring
        await loop.HandleUnknown402Async(
            session, new InboundPacket(InGameOpcodes.C2SUnknown402, body.ToArray()), CancellationToken.None);

        Assert.Equal(string.Empty, NameOf(session.Body(InGameOpcodes.S2CGotoRoom)));
        Assert.Equal(string.Empty, NameOf(session.Body(InGameOpcodes.S2CRoomInfoSnapshot)));
    }

    [Fact]
    public async Task ReSendStoresTheTypedNameTheRoomWasCreatedWithout()
    {
        // The resend updates the same setting title without publishing the independent tail.
        var options = new InGameOptions { SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var host = new RecordingSession(162);
        host.SetLobbyPlayer(82, "host");

        var unnamed = new List<byte>();
        unnamed.AddRange(WireLengthSupport.Written(RoomWireSupport.WaitingSettingWithMap().Write));
        unnamed.AddRange(BitConverter.GetBytes(0));
        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, unnamed.ToArray()), CancellationToken.None);
        var room = registry.TryGet(host.Id);
        Assert.NotNull(room);
        Assert.Equal(string.Empty, room.RoomName);

        var named = new List<byte>();
        named.AddRange(WireLengthSupport.Written((RoomWireSupport.WaitingSettingWithMap() with { Unknown8 = "TestRoom" }).Write));
        named.AddRange(BitConverter.GetBytes(0));
        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, named.ToArray()), CancellationToken.None);

        Assert.Equal("TestRoom", room.RoomName);
        // RecordingSession.Body returns the last body, which is the re-send's
        // own install.
        Assert.Equal("TestRoom", NameOf(host.Body(InGameOpcodes.S2CGotoRoom)));
    }

    [Fact]
    public async Task JoinInstallsCarryTheRoomsTypedName()
    {
        // The join path shares the push seam, so a joiner installing into a
        // room whose host typed a name reads the same name.
        var options = new InGameOptions { SendPlazaSnapshot = false };
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(170);
        var (room, hostSeat) = registry.CreateFromSetting(
            host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host", "TestRoom");
        hostSeat.CharacterId = 300001;
        hostSeat.JoinAcked = true;
        var guest = new RecordingSession(172);
        guest.SetLobbyPlayer(22, "guest");
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var joinBody = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(joinBody, room.Id);

        await loop.HandleJoinRoomAsync(
            guest, new InboundPacket(InGameOpcodes.C2SJoinRoom, joinBody), CancellationToken.None);

        Assert.Equal("TestRoom", NameOf(guest.Body(InGameOpcodes.S2CGotoRoom)));
    }

    [Fact]
    public void InSessionSpawnCodeDefaultsToTheUnguardedStandUp()
    {
        // 29 is the value that fills HP, but sub_49C870 opens its body with the
        // bail test sub_49F4B0(a1) - 500 >= 0x64 (sub_49C870) and that getter
        // reads the sub-state through the pointer at +0xDBC
        // (kr-1256 sub_49F4B0), so a character sitting in the dead
        // family takes the other branch and nothing respawns. 21 -> sub_49D020 ->
        // sub_49D5B0 carries no such test: it restores this[218] from this[217]
        // and maps a saved csDead (4) to csNull (0) (sub_49D5B0), which is the
        // symptom actually reported - sealed from entry and never released.
        Assert.Equal(21, new InGameOptions().InSessionSpawnCode);
    }

    [Fact]
    public void ANonEmptyVecBListIsRefusedAtLoad()
    {
        // The option cannot do what its name claims: the entry fold replays vecB
        // and vecC and then sub_818190 truncates equipment+64 and clears the
        // +8/+20/+32 containers before vecA goes in last (sub_818190,
        // sub_817DF0), so the dresser sub_91F720 never sees a vecB value.
        // A deployment that sets the list believes it is dressing a character, so
        // it is a load error rather than a knob that turns nothing.
        var options = new InGameOptions { ModelItemIds = [300001] };
        var failure = Assert.Single(
            options.Validate(),
            r => r.MemberNames.Contains(nameof(InGameOptions.ModelItemIds)));
        Assert.Contains("inert", failure.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinInstallsTheRoomForTheJoinerOnlyAndTellsTheHostBySeatRecord()
    {
        // The default shape. The joiner gets the install because it has no room
        // yet; the occupant gets the S2C 503, whose handler sub_9DA720 writes one
        // seat's record into the array the install built, refreshes the chips and
        // reaches the spawner WITHOUT calling the stage setter - so the host's
        // room is never deleted and rebuilt. The S2C 118 wait list follows for
        // both, which is the seatless packet that cannot touch the stage.
        var options = new InGameOptions { RepublishRoomUiOnJoin = true };
        var (registry, room, host, guest) = HostedRoom(options);

        await JoinAsync(registry, options, room, guest);

        Assert.Equal(
            new ushort[]
            {
                InGameOpcodes.S2CJoinHandshake,
                InGameOpcodes.S2CJoinSlotUpdate,
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
                InGameOpcodes.S2CCreateRoomReply,
                InGameOpcodes.S2CCreateRoomReply,
                InGameOpcodes.S2CWaitUserList,
            },
            guest.Opcodes);
        // The joiner's own seat record first, then the host's.
        var hostSeat = FindSeat(room, host);
        Assert.Equal(new[] { FindSeat(room, guest).Slot, hostSeat.Slot },
            guest.Bodies(InGameOpcodes.S2CCreateRoomReply).Select(body => BitConverter.ToInt32(body, 0)).ToArray());
        Assert.Equal(
            new ushort[] { InGameOpcodes.S2CCreateRoomReply, InGameOpcodes.S2CWaitUserList },
            host.Opcodes);
        Assert.Equal(0, host.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
        Assert.Equal(1, guest.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));

        // The first i32 of the S2C 503 is the seat index the client writes the
        // record into (sub_9DA720, verified as an index): the joiner's own slot.
        var guestSeat = FindSeat(room, guest);
        var reply = new PacketReader(host.Body(InGameOpcodes.S2CCreateRoomReply));
        Assert.Equal(guestSeat.Slot, reply.ReadInt32());
        var record = WireLengthSupport.Written(w => RoomPlayerInfo
            .ForSeat(guestSeat.UserNum, guestSeat.Nickname, checked((byte)guestSeat.Slot), guestSeat.Level,
                guestSeat.Team).Write(w));
        Assert.Equal(record, reply.ReadBytes(record.Length).ToArray());
    }

    [Fact]
    public async Task JoinerSecondProbeReinstallsForItselfAndLeavesTheHostAlone()
    {
        // The first C2S 517 a seated client sends after its join is the push this branch performs (after the
        // room stage is live). With the flag off it is aimed at the probing seat only, so the occupant is not
        // torn out of its room a second time, and it gets no extra seat record either - its room already holds
        // that seat from the join's S2C 503. The only thing that still reaches it is the seatless S2C 118 wait
        // list, which cannot touch the stage.
        var options = new InGameOptions { RepublishRoomUiOnJoin = true };
        var (registry, room, host, guest) = HostedRoom(options);

        await JoinAsync(registry, options, room, guest);
        var sentBefore = host.Opcodes.Count();
        await JoinAsync(registry, options, room, guest);

        Assert.All(host.Opcodes.Skip(sentBefore), opcode => Assert.Equal(InGameOpcodes.S2CWaitUserList, opcode));
        Assert.Equal(0, host.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
        Assert.Equal(1, host.Opcodes.Count(o => o == InGameOpcodes.S2CCreateRoomReply));
        Assert.Equal(2, guest.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
    }

    [Fact]
    public async Task TargetedInstallRefusesASeatTheRoomDoesNotHold()
    {
        // Aiming the burst is a delivery narrowing, not a way to install a room
        // for a player outside it: the seat has to be one of the room's own, and a
        // mismatch is a caller bug the row must hear about.
        var registry = new RoomRegistry(new InGameOptions());
        var loop = new RoomLoop(
            registry, new ModeCatalog(), new InGameOptions(), "127.0.0.1", NullLogger.Instance);
        var (room, _) = registry.CreateFromSetting(
            new RecordingSession(130), RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        var (_, stranger) = registry.CreateFromSetting(
            new RecordingSession(131), RoomWireSupport.WaitingSetting() with { Kind = 0 }, 12, "other");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.PushRoomToSeatAsync(room, stranger, CancellationToken.None));
    }

    /// <summary>
    /// Builds a room hosting one player (slot 0) whose install burst has already
    /// gone out, so the only traffic left to drive is the joiner's.
    ///
    /// </summary>
    private static (RoomRegistry Registry, PlayRoom Room, RecordingSession Host, RecordingSession Guest)
        HostedRoom(InGameOptions options)
    {
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(120);
        var (room, hostSeat) = registry.CreateFromSetting(
            host, RoomWireSupport.OrdinarySettingWithMap() with { Kind = 0 }, 11, "host");
        hostSeat.CharacterId = 300001;
        hostSeat.JoinAcked = true;
        var guest = new RecordingSession(121);
        guest.SetLobbyPlayer(22, "guest");
        return (registry, room, host, guest);
    }

    /// <summary>Drives one C2S 517 join for the room's second session.</summary>
    private static async Task JoinAsync(
        RoomRegistry registry, InGameOptions options, PlayRoom room, RecordingSession guest)
    {
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, room.Id);
        await loop.HandleJoinRoomAsync(
            guest, new InboundPacket(InGameOpcodes.C2SJoinRoom, body), CancellationToken.None);
    }

    /// <summary>Finds a session's seat in a room, taking the room gate as every seat reader must.</summary>
    private static RoomSeat FindSeat(PlayRoom room, RecordingSession session)
    {
        lock (room.Gate)
            return room.FindByGame(session.Id)
                   ?? throw new InvalidOperationException($"Session {session.Id} holds no seat in room {room.Id}.");
    }

    [Fact]
    public void ProfileEntryWritesTheCharacterIdAndEmptyVectors()
    {
        // Both known-good writers send this shape: the login burst's UpdatePartySolo writes all five vectors
        // empty, and a room session's map564 entry carries the character id with zero weapon
        // ids and no counted vectors. An earlier revision repeated twelve class-2 weapon ids in vecA, which is
        // the one vector the applier replays after sub_818190 clears equipment+64 - so it was the one place
        // this emulator could inject weapon ids into the character profile. The equipped set travels on the S2C
        // 806 record instead.
        var body = WireLengthSupport.Written(w => new ProfileMapEntry(5, 300001, 0).Write(w));
        var r = new PacketReader(body);

        Assert.Equal(5, r.ReadInt32());                  // key
        Assert.Equal(300001, r.ReadInt32());             // character id
        Assert.Equal(0u, r.ReadUInt32());                // vecA empty
        Assert.Equal(0u, r.ReadUInt32());                // vecB empty
        Assert.Equal(0u, r.ReadUInt32());                // vecC empty
        Assert.Equal(16u, r.ReadUInt32());               // blob count
        for (var i = 0; i < 4; i++)
            r.ReadInt32();                               // the 16-byte identity blob
        Assert.Equal(0u, r.ReadUInt32());                // items empty
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RoomInfoSnapshotIsExactlyOneSessionSetting()
    {
        // S2C 501 is sent in the room-entry sequence, and its body is exactly one
        // SessionSetting - the same blob the 117 carries, with no player list and no team panel. Our room
        // setting is 241 bytes for this fixture.
        var (room, _, _) = TwoSeatRoom();
        var body = RoomSessionBlob.RoomInfoSnapshot(room.Setting);

        Assert.Equal(241, body.Length);
        var r = new PacketReader(body);
        var setting = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.MainMenuKind, setting.Kind);
        Assert.Equal(SessionSettingBlob.RoomStateWaiting, setting.Unknown4);
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task StageEnterCanCloseTheBurstWithTheRoomMap()
    {
        // The three room buttons are gated on the stage, and the stage is written
        // only by the MainApp stage setter sub_87C990, whose sole S2C caller is
        // the S2C 510 handler. Without it an ordinary room never rises above the
        // menu stage. The record carries the room's own map keys because
        // sub_9E5E70 copies them onto session+184/+208/+232.
        var (room, host, guest) = TwoSeatRoomWithMap();
        var loop = new RoomLoop(
            new RoomRegistry(new InGameOptions()), new ModeCatalog(),
            new InGameOptions { EnterGameStageOnCreate = true }, "127.0.0.1", NullLogger.Instance);

        await loop.PushRoomAsync(room, CancellationToken.None);

        Assert.Contains(InGameOpcodes.S2CEnterGameStage, host.Opcodes);
        var stage = new PacketReader(host.Body(InGameOpcodes.S2CEnterGameStage));
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal("rel_Village00_boomhill", stage.ReadWideStringPrefixed());
        Assert.Equal(SessionSettingBlob.RoomStateInRoom, stage.ReadInt32());
    }

    [Fact]
    public async Task TrainingRoomDoesNotTakeASecondStageEnter()
    {
        // The training ground already sends its own stage enter from the create
        // path (StartTrainingImmediately). A second one in the room push moves the
        // client out of the round the first created, which a live session showed
        // as the round being entered and then never starting.
        var registry = new RoomRegistry(new InGameOptions());
        var session = new RecordingSession(51);
        var (room, _) = registry.CreateTrainingRoom(
            session, RoomWireSupport.SampleRules(), 45, "trainee");
        var loop = new RoomLoop(
            registry, new ModeCatalog(),
            new InGameOptions { EnterGameStageOnCreate = true }, "127.0.0.1", NullLogger.Instance);

        await loop.PushRoomAsync(room, CancellationToken.None);

        Assert.DoesNotContain(InGameOpcodes.S2CEnterGameStage, session.Opcodes);
    }

    [Fact]
    public async Task StageEnterIsOffByDefaultSoTheRoomStaysInTheWaitingStage()
    {
        var (room, host, _) = TwoSeatRoom();
        var loop = new RoomLoop(
            new RoomRegistry(new InGameOptions()), new ModeCatalog(), new InGameOptions(),
            "127.0.0.1", NullLogger.Instance);

        await loop.PushRoomAsync(room, CancellationToken.None);

        Assert.DoesNotContain(InGameOpcodes.S2CEnterGameStage, host.Opcodes);
    }

    [Fact]
    public void CharacterTogglePinsTheTwoIntegerBody()
    {
        // sub_6D55D0 case 0x6D6765 reads one i32 through the stream vtable, then
        // sub_4245E0 reads a second and stores value != 0. Every
        // observed body is 8 bytes with unknown0 = 1000000 and the bool 1 or 0.
        var body = new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00 };
        var on = Unknown32779Request.Parse(body);
        Assert.Equal(1000000, on.Unknown0);
        Assert.Equal(1, on.Unknown4);

        body[4] = 0;
        var off = Unknown32779Request.Parse(body);
        Assert.Equal(0, off.Unknown4);
    }

    [Fact]
    public void CharacterToggleRejectsATrailingByte()
    {
        var body = new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01 };
        Assert.Throws<PacketFormatException>(() => Unknown32779Request.Parse(body));
    }

    [Fact]
    public void CharacterActionsPinsTheObservedBodies()
    {
        // 0x6D6765, 0x6D67D8 and 0x6D6E22 share the character id, not a ping prefix.
        Assert.Equal(1000000, Unknown32779Request.Parse(new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00 }).Unknown0);

        var report = Unknown32780Request.Parse(
            new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00, 0x34, 0x12 });
        Assert.Equal(1000000, report.Unknown0);
        Assert.Equal(1, report.Unknown4);
        Assert.Equal((ushort)0x1234, report.Unknown8);
        Assert.Equal(10, Unknown32780Request.BodyBytes);

        var tick = Unknown32813Request.Parse(new byte[] { 0x40, 0x42, 0x0F, 0x00 });
        Assert.Equal(1000000, tick.Unknown0);
        Assert.Equal(4, Unknown32813Request.BodyBytes);
    }

    [Fact]
    public void CharacterActionsRejectsATrailingByte()
    {
        Assert.Throws<PacketFormatException>(() =>
            Unknown32813Request.Parse(new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x01 }));
    }

    [Fact]
    public async Task ChangeTeamAppliesTeamAndRepublishesForEveryBodyShape()
    {
        // sub_4B3AC0 writes a two-level tagged optional, and
        // the opcode is shared with sub_4A1EB0 and sub_ADEF30, so the handler
        // must accept any length without a format error. The applied team is
        // the second byte when it carries a valid label (1 or 2, the values
        // sub_980730's board switch reads); any other shape
        // toggles 1/2. Every accepted send re-publishes the room UI as one
        // S2C 118 so the team boards re-render.
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(10);
        var (room, _) = registry.CreateFromSetting(
            host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        var loop = new RoomLoop(
            registry, new ModeCatalog(), new InGameOptions(), "127.0.0.1", NullLogger.Instance);

        foreach (var body in new byte[][]
        {
            new byte[] { 0x3A, 0x3A },
            new byte[] { 0xBA, 0x01, 0x00, 0x00, 0x00, 0x3A },
            new byte[] { 0x3A, 0xBA, 0x02, 0x00, 0x00, 0x00, 0x03 },
            new byte[] { 0xBA, 0x04, 0x00, 0x00, 0x00, 0xBA, 0x05, 0x00, 0x00, 0x00, 0x06 },
            Array.Empty<byte>(),
        })
        {
            await loop.HandleChangeTeamAsync(
                host, new InboundPacket(InGameOpcodes.C2SChangeTeam, body), CancellationToken.None);
        }

        // Default: no room install. The 117's kind-2 arm reaches SetStage, so one
        // per team click makes the client leave and re-enter the room, which reads
        // to a player as the team button re-opening the room. The seat state still
        // advances and the label rides the next real install; S2C 504 could carry
        // a single seat but its body is unrecovered, so nothing is invented here.
        Assert.DoesNotContain(InGameOpcodes.S2CGotoRoom, host.Opcodes);

        // The opt-in restores the previous full push (S2C 117 install, 501
        // snapshot, 201 party update) for a deployment that wants the seat record
        // rewritten immediately.
        var fullOptions = new InGameOptions { RepublishFullRoomOnTeamChange = true };
        var fullRegistry = new RoomRegistry(fullOptions);
        var fullHost = new RecordingSession(12);
        fullRegistry.CreateFromSetting(
            fullHost, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        var fullLoop = new RoomLoop(
            fullRegistry, new ModeCatalog(), fullOptions, "127.0.0.1", NullLogger.Instance);
        await fullLoop.HandleChangeTeamAsync(
            fullHost, new InboundPacket(InGameOpcodes.C2SChangeTeam, new byte[] { 0x3A, 0x3A }),
            CancellationToken.None);
        Assert.Equal(
            new[]
            {
                InGameOpcodes.S2CGotoRoom,
                InGameOpcodes.S2CRoomInfoSnapshot,
                InGameOpcodes.S2CUpdateParty,
            },
            fullHost.Opcodes);
        Assert.NotNull(room);
    }

    [Fact]
    public async Task ChangeTeamOutsideARoomIsStillLoggedAndNotReplied()
    {
        var loop = new RoomLoop(
            new RoomRegistry(new InGameOptions()), new ModeCatalog(), new InGameOptions(), "127.0.0.1",
            NullLogger.Instance);
        var session = new RecordingSession(99);

        await loop.HandleChangeTeamAsync(
            session,
            new InboundPacket(InGameOpcodes.C2SChangeTeam, new byte[] { 0x3A, 0x3A }),
            CancellationToken.None);

        Assert.Empty(session.Opcodes);
    }

    [Fact]
    public async Task CreateAnswersWithTheWaitingDialogOnlyWhenEnabled()
    {
        // The waiting wall lifecycle: state 0 opens the client's dialog (UI
        // resource 1760) when the room settles, and the 8-byte body is two
        // i32 - the parser sub_84F6C0 reads exactly that and nothing
        // else. The option is the A/B lever for the missing waiting phase.
        var options = new InGameOptions { SendWaitingDialog = true };
        var host = new RecordingSession(10);
        var loop = new RoomLoop(
            new RoomRegistry(options), new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var payload = WireLengthSupport.Written(w =>
        {
            RoomWireSupport.OrdinarySettingWithMap().Write(w);
            w.WriteWideStringPrefixed("pw");
        });

        host.SetLobbyPlayer(11, "host");
        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, payload), CancellationToken.None);

        Assert.Equal(12, host.Body(InGameOpcodes.S2CWaitingDialog).Length);
        Assert.Equal(
            Convert.ToHexString(GameToClient.WaitingDialog(0, 0)),
            Convert.ToHexString(host.Body(InGameOpcodes.S2CWaitingDialog)));

        // Negative control: with the option off the create sends no 128 at
        // all - the current no-wall shape must stay reachable.
        var offHost = new RecordingSession(12);
        var offLoop = new RoomLoop(
            new RoomRegistry(new InGameOptions()), new ModeCatalog(), new InGameOptions(), "127.0.0.1",
            NullLogger.Instance);
        offHost.SetLobbyPlayer(12, "host", pastLoginBurst: false);
        await offLoop.HandleUnknown402Async(
            offHost, new InboundPacket(InGameOpcodes.C2SUnknown402, payload), CancellationToken.None);
        Assert.DoesNotContain(InGameOpcodes.S2CWaitingDialog, offHost.Opcodes);
    }

    [Fact]
    public async Task ReSendDuringALiveRoundInstallsNothing()
    {
        // The waiting dialog's 0x14 makes the host's client re-send C2S 402 on
        // its own at start time; answering that with an install would delete
        // the live session and every character (0x9AC6C2, sub_91EDD0), so a
        // non-waiting phase gets no reply at all.
        var options = new InGameOptions { SendWaitingDialog = true };
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var payload = WireLengthSupport.Written(w =>
        {
            RoomWireSupport.OrdinarySettingWithMap().Write(w);
            w.WriteWideStringPrefixed("pw");
        });

        host.SetLobbyPlayer(13, "host");
        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, payload), CancellationToken.None);
        var sentBeforeReSend = host.Opcodes.Count();

        var room = registry.TryGet(host.Id);
        Assert.NotNull(room);
        room.MarkStarting();
        var countAfterMark = host.Opcodes.Count();

        await loop.HandleUnknown402Async(
            host, new InboundPacket(InGameOpcodes.C2SUnknown402, payload), CancellationToken.None);

        Assert.Equal(countAfterMark, host.Opcodes.Count());
        Assert.True(sentBeforeReSend > 0);
    }

    private static (PlayRoom Room, RecordingSession Host, RecordingSession Guest) TwoSeatRoom()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.CreateFromSetting(
            host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        hostSeat.CharacterId = 300001;
        var guest = new RecordingSession(11);
        var (_, guestSeat) = registry.Join(guest, room.Id, 22, "guest");
        guestSeat.CharacterId = 300017;
        return (room, host, guest);
    }

    /// <summary>Builds a two-seat ORDINARY room (non-training mode) whose map is the Boomhill village.</summary>
    private static (PlayRoom Room, RecordingSession Host, RecordingSession Guest) TwoSeatRoomWithMap()
    {
        var registry = new RoomRegistry(new InGameOptions());
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.CreateFromSetting(
            host, RoomWireSupport.OrdinarySettingWithMap() with { Kind = 0 }, 11, "host");
        hostSeat.CharacterId = 300001;
        var guest = new RecordingSession(11);
        var (_, guestSeat) = registry.Join(guest, room.Id, 22, "guest");
        guestSeat.CharacterId = 300017;
        return (room, host, guest);
    }

    /// <summary>In-memory game session that records every send. Shared with the plaza snapshot tests.</summary>
    /// <param name="id">The session id.</param>
    internal sealed class RecordingSession(long id) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        private readonly List<(ushort Opcode, byte[] Body)> _sent = [];

        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();

        public IEnumerable<ushort> Opcodes
        {
            get
            {
                lock (_sent)
                    return _sent.Select(s => s.Opcode).ToArray();
            }
        }

        public byte[] Body(ushort opcode)
        {
            lock (_sent)
                return _sent.Last(s => s.Opcode == opcode).Body;
        }

        /// <summary>Gets every body recorded for an opcode, in send order.</summary>
        public byte[][] Bodies(ushort opcode)
        {
            lock (_sent)
                return [.. _sent.Where(s => s.Opcode == opcode).Select(s => s.Body)];
        }

        /// <summary>Gets the first body recorded for an opcode.</summary>
        /// <param name="opcode">The opcode to look up.</param>
        public byte[] First(ushort opcode)
        {
            lock (_sent)
                return _sent.First(s => s.Opcode == opcode).Body;
        }

        /// <summary>Gets the last body recorded for an opcode.</summary>
        /// <param name="opcode">The opcode to look up.</param>
        public byte[] Last(ushort opcode)
        {
            lock (_sent)
                return _sent.Last(s => s.Opcode == opcode).Body;
        }

        /// <summary>Gets or sets an optional callback run after each send is recorded, for tests that widen interleaving windows.</summary>
        public Func<ushort, byte[], ValueTask>? OnSend { get; set; }

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var body = payload.ToArray();
            lock (_sent)
                _sent.Add((opcode, body));
            return OnSend?.Invoke(opcode, body) ?? ValueTask.CompletedTask;
        }
    }

    /// <summary>In-memory connection stub; the pipe members are never touched by the loops.</summary>
    /// <param name="id">The connection id; unique per session so the plaza leave lookup can tell sessions apart.</param>
    internal sealed class FakeConnection(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason)
        {
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Records typed sends so handler tests assert messages, not one build's bytes.</summary>
internal sealed class CapturingSender<T> : BubbleFighter.Fantasy.Abstractions.Protocol.IMessageSender<T> where T : BubbleFighter.Fantasy.Abstractions.Protocol.IMessage
{
    public List<T> Sent { get; } = [];

    public string Protocol => "kr-1256/game";

    public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
    {
        Sent.Add(message);
        return ValueTask.CompletedTask;
    }
}
