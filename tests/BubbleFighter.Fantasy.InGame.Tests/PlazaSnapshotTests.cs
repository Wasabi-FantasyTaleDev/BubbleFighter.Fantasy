using System.Buffers.Binary;
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
/// Pins for the S2C 117 kind-3 plaza snapshot behind
/// <see cref="InGameOptions.SendPlazaSnapshot"/>. The kind-3 branch of <c>sub_9AC490</c> pushes
/// stage id 3 (the plaza, <c>LobbyStage</c>) and applies <c>map564</c> /
/// <c>map588</c> for every kind, so one body both enters the plaza and writes each
/// listed player's profile record. Neither the spawner <c>sub_9201D0</c> nor the
/// dresser <c>sub_91F720</c> is reachable from the handler, so the snapshot is the
/// plaza's whole peer surface.
///
/// </summary>
public sealed class PlazaSnapshotTests
{
    // Each player's scene character, served the way the roster service serves it in production.
    private readonly Dictionary<int, int> _characters = [];

    private const int SessionSettingBytes = 225;

    private const int ProfileEntryBytes = 4 + 4 + 4 + 4 + 4 + 20 + 4;

    [Fact]
    public async Task RoomReturnDetachesOnlyTheRequesterAndKeepsOtherRoomsInstalled()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1",
            NullLogger.Instance, null, new PlazaRoster());
        var host = new RoomPushTests.RecordingSession(10);
        var other = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(host, 11, "host");
        EnterMainMenu(other, 22, "other");
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        await loop.Plaza.HandleEnterPlazaAsync(other, PlazaEnter(), CancellationToken.None);
        registry.CreateFromSetting(host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        registry.CreateFromSetting(other, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 22, "other");
        var otherPackets = other.Opcodes.Count();

        await loop.HandleUnknown404Async(host,
            new InboundPacket(InGameOpcodes.C2SUnknown404, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Null(registry.TryGet(host.Id));
        Assert.NotNull(registry.TryGet(other.Id));
        Assert.Equal(otherPackets, other.Opcodes.Count());
        Assert.Equal(new ushort[] { InGameOpcodes.S2CGotoRoom, InGameOpcodes.S2CUpdateParty,
            InGameOpcodes.S2CGotoLobby }, host.Opcodes.TakeLast(3));
        Assert.Equal(1, CountPlayers(new PacketReader(host.Body(InGameOpcodes.S2CGotoRoom))));
    }

    [Fact]
    public async Task MalformedRoomReturnDoesNotDetachTheRequester()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance);
        var host = new RoomPushTests.RecordingSession(10);
        EnterMainMenu(host, 11, "host");
        registry.CreateFromSetting(host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");

        await Assert.ThrowsAnyAsync<Exception>(async () => await loop.HandleUnknown404Async(host,
            new InboundPacket(InGameOpcodes.C2SUnknown404, new byte[] { 1 }), CancellationToken.None));

        Assert.NotNull(registry.TryGet(host.Id));
        Assert.Empty(host.Opcodes);
    }

    [Fact]
    public async Task ConflictingPlazaIdentityReceivesNoStageSuccessReply()
    {
        var loop = PlazaLoop(new InGameOptions { SendPlazaSnapshot = true }, out var host);
        EnterMainMenu(host, 11, "host");
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        var sentBefore = host.Opcodes.Count();
        var duplicate = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(duplicate, 11, "host");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await loop.Plaza.HandleEnterPlazaAsync(duplicate, PlazaEnter(), CancellationToken.None));

        Assert.Empty(duplicate.Opcodes);
        Assert.Equal(sentBefore, host.Opcodes.Count());
    }

    [Fact]
    public void SendPlazaSnapshotDefaultsToOff()
    {
        var options = new InGameOptions();

        Assert.False(options.SendPlazaSnapshot);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public async Task PlazaEnterWithTheOptionOffKeepsTheS2C116OnlyReply()
    {
        var loop = PlazaLoop(new InGameOptions(), out var host);
        EnterMainMenu(host, 11, "host");

        await loop.Plaza.HandleEnterPlazaAsync(
            host, PlazaEnter(), CancellationToken.None);

        // Exactly the shipped exchange: S2C 116 result=0 and nothing else. No
        // plaza population is tracked, no profile map is written.
        Assert.Equal(new ushort[] { InGameOpcodes.S2CGotoLobby }, host.Opcodes);
        Assert.Equal(4, host.Body(InGameOpcodes.S2CGotoLobby).Length);
    }

    [Fact]
    public async Task PlazaEnterPublishesTheKindThreeSnapshotToTheEnteringPlayer()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);

        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        // The kind-3 install carries the whole plaza population, the entering player included,
        // then the party update that reaches the plaza's character spawner, then the S2C 116
        // enter result (117 before 116).
        Assert.Equal(
            new ushort[] { InGameOpcodes.S2CGotoRoom, InGameOpcodes.S2CUpdateParty, InGameOpcodes.S2CGotoLobby },
            host.Opcodes);
        var body = host.Body(InGameOpcodes.S2CGotoRoom);

        // 225 (SessionSetting + empty MapMeta) + 4 + 4 (unknown544/548) + 4
        // (players count) + 188 + 2*4 (one RoomPlayerInfo, "host") + 4 (map564
        // count) + 92 (one entry) + 4 (map588 count) + 12 (ServerTeamInfo) + 1
        // (opt648 absent) + 4 + 4 (unknown728/732).
        Assert.Equal(
            SessionSettingBytes + 4 + 4 + 4 + (188 + 2 * "host".Length) + 4 + ProfileEntryBytes + 4 + 12 + 1 + 4 + 4,
            body.Length);
        Assert.Equal(506, body.Length);

        var r = new PacketReader(body);
        var setting = SessionSettingBlob.Read(ref r);
        Assert.Equal(SessionSettingBlob.PlazaKind, setting.Kind);
        Assert.True(setting.Map.IsEmpty);
        Assert.Equal(0, r.ReadInt32());      // unknown544
        Assert.Equal(0, r.ReadInt32());      // unknown548

        // One RoomPlayerInfo per plaza player, in roster order. The seat index
        // is the vector position (sub_9D9250), and the first dword is the
        // occupancy value sub_9EE100 scans as two bytes, so both low bytes must
        // be nonzero for the seat to count as present.
        Assert.Equal(1u, r.ReadUInt32());
        var record = WireLengthSupport.Written(w => RoomPlayerInfo.ForSeat(11, "host", 0).Write(w));
        Assert.Equal(record, r.ReadBytes(record.Length).ToArray());
        Assert.NotEqual(0, record[0]);
        Assert.NotEqual(0, record[1]);
        Assert.Equal(11, BitConverter.ToInt32(record, 14));   // userNum at +8

        // One map564 entry per player, keyed by that player's UserNum, with the
        // equipped character id the same resolver order the room push uses.
        Assert.Equal(1u, r.ReadUInt32());
        var entry = WireLengthSupport.Written(w => new ProfileMapEntry(11, 300017).Write(w));
        Assert.Equal(entry, r.ReadBytes(entry.Length).ToArray());
        Assert.InRange(300017, 300000, 301999);

        Assert.Equal(0u, r.ReadUInt32());                      // map588
        Assert.Equal(new byte[12], r.ReadBytes(12).ToArray());  // ServerTeamInfo
        Assert.Equal((byte)0, r.ReadByte());                   // opt648 absent
        Assert.Equal(0, r.ReadInt32());                        // unknown728
        Assert.False(r.ReadBool32());                          // unknown732
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task EntrantInstallCarriesEveryPlayerAtTheirSeatWithTheirOwnCharacterId()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        var guest = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(guest, 22, "guest", characterType: 7);
        await loop.Plaza.HandleEnterPlazaAsync(guest, PlazaEnter(), CancellationToken.None);

        var r = new PacketReader(guest.Body(InGameOpcodes.S2CGotoRoom));
        _ = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(2u, r.ReadUInt32());      // both players, placed by plaza seat
        foreach (var (userNum, nickname, slot) in new[] { (11, "host", (byte)0), (22, "guest", (byte)1) })
        {
            var expected = WireLengthSupport.Written(w => RoomPlayerInfo.ForSeat(userNum, nickname, slot).Write(w));
            Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
        }
        Assert.Equal(2u, r.ReadUInt32());
        foreach (var (userNum, characterId, slot) in new[] { (11, 300017, 0), (22, 300007, 1) })
        {
            var expected = WireLengthSupport.Written(w => new ProfileMapEntry(userNum, characterId, slot).Write(w));
            Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
        }
        Assert.Equal(0u, r.ReadUInt32());                      // map588
        Assert.Equal(new byte[12], r.ReadBytes(12).ToArray());  // ServerTeamInfo
        Assert.Equal((byte)0, r.ReadByte());                   // opt648 absent
        Assert.Equal(1, r.ReadInt32());                        // recipient slot
        Assert.False(r.ReadBool32());                          // unknown732
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task SecondEntrantIsAddedForTheFirstBySeatRecordWithoutAReinstall()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        var hostBefore = host.Opcodes.Count();

        var guest = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(guest, 22, "guest", equippedItemId: 300009);
        await loop.Plaza.HandleEnterPlazaAsync(guest, PlazaEnter(), CancellationToken.None);

        // The player already there gets the
        // newcomer's seat record (S2C 511), not a second kind-3 install that would rebuild it.
        var hostNew = host.Opcodes.Skip(hostBefore).ToArray();
        Assert.DoesNotContain(InGameOpcodes.S2CGotoRoom, hostNew);
        var record = host.Body(InGameOpcodes.S2CSeatRecord);
        Assert.Equal(1, BitConverter.ToInt32(record, 0));   // the newcomer's plaza seat
        Assert.NotEqual(0, record[4]);                     // occupied
        Assert.Equal(22, BitConverter.ToInt32(record, 4 + 14));
        Assert.Equal(2, CountPlayers(new PacketReader(guest.Body(InGameOpcodes.S2CGotoRoom))));
    }

    [Fact]
    public async Task ClosedConnectionIsClearedFromTheOthersBySeatRecord()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        var guest = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(guest, 22, "guest", equippedItemId: 300009);
        await loop.Plaza.HandleEnterPlazaAsync(guest, PlazaEnter(), CancellationToken.None);
        var guestBefore = guest.Opcodes.Count();

        loop.Plaza.OnConnectionClosed(new ConnectionClosedEvent(host.Connection, "test"));

        // The close is queued on the plaza executor; the survivor gets the vacated seat record.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!guest.Opcodes.Skip(guestBefore).Contains(InGameOpcodes.S2CSeatRecord) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var record = guest.Body(InGameOpcodes.S2CSeatRecord);
        Assert.Equal(Convert.ToHexString(GameToClient.SeatVacated(0)), Convert.ToHexString(record));
        Assert.DoesNotContain(InGameOpcodes.S2CGotoRoom, guest.Opcodes.Skip(guestBefore));
    }

    [Fact]
    public async Task PlazaSnapshotSkipsAPlayerWhoMovedIntoARoom()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(
            registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, new CharacterRoster(_characters), new PlazaRoster());
        var host = new RoomPushTests.RecordingSession(10);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        // The room stage replaces the plaza stage, so a player who creates a
        // room is no longer in the plaza even before the connection closes.
        registry.CreateFromSetting(host, RoomWireSupport.WaitingSetting() with { Kind = 0 }, 11, "host");
        var guest = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(guest, 22, "guest", equippedItemId: 300009);
        await loop.Plaza.HandleEnterPlazaAsync(guest, PlazaEnter(), CancellationToken.None);

        // The room holder is absent from the newcomer's install: its plaza seat is an empty record.
        var install = guest.Body(InGameOpcodes.S2CGotoRoom).AsSpan();
        var hostRecord = WireLengthSupport.Written(w => RoomPlayerInfo.ForSeat(11, "host", 0).Write(w));
        var guestRecord = WireLengthSupport.Written(w => RoomPlayerInfo.ForSeat(22, "guest", 1).Write(w));
        Assert.True(install.IndexOf(hostRecord) < 0);
        Assert.True(install.IndexOf(guestRecord) >= 0);

        // The skip is a hazard guard, not tidiness: the kind-3 body runs the same
        // install prologue as a room's kind-2, which deletes the live room session
        // (0x9AC6C2, republished at 0x9AC70F and 0x9AC768), rebuilds the seats
        // through sub_9D9250 and frees every live character through sub_91EDD0. So
        // the room holder gets no install beyond the one its own plaza enter
        // already sent it, and the withholding is logged as a warning.
        Assert.Equal(1, host.Opcodes.Count(o => o == InGameOpcodes.S2CGotoRoom));
    }

    [Fact]
    public void PlazaRosterIsBoundedAtTheKindThreeSeatCount()
    {
        var roster = new PlazaRoster();
        for (var i = 0; i < PlazaRoster.SeatLimit; i++)
            roster_Enter(roster, i);
        Assert.Equal(PlazaRoster.SeatLimit, roster.Count);

        // sub_9D9250 builds 36 seats for kind 3, so the 37th player fails loud
        // instead of growing an unbounded collection.
        Assert.Throws<InvalidOperationException>(() => roster_Enter(roster, PlazaRoster.SeatLimit));

        static void roster_Enter(PlazaRoster roster, int index)
        {
            var session = new RoomPushTests.RecordingSession(1000 + index);
            session.SetLobbyPlayer(index + 1, $"p{index}");
            roster.Enter(session, index + 1, $"p{index}", 300001, 1);
        }
    }

    [Fact]
    public void PlazaRejectsDuplicateIdentityWithoutChangingTheExistingSeat()
    {
        var roster = new PlazaRoster();
        var original = new RoomPushTests.RecordingSession(10);
        var conflicting = new RoomPushTests.RecordingSession(11);
        var seat = roster.Enter(original, 7, "player", 300001, 2);

        Assert.Throws<InvalidOperationException>(() =>
            roster.Enter(conflicting, 7, "player", 300009, 3));

        Assert.Same(seat, Assert.Single(roster.Snapshot()));
        Assert.Null(roster.Find(conflicting.Id));
        Assert.Equal(300001, seat.CharacterId);
        Assert.Equal(2, seat.Level);
    }

    [Fact]
    public void PlazaRefreshKeepsItsSeatAndAllowsDistinctPlayers()
    {
        var roster = new PlazaRoster();
        var first = new RoomPushTests.RecordingSession(10);
        var second = new RoomPushTests.RecordingSession(11);
        var seat = roster.Enter(first, 7, "first", 300001, 2);
        roster.Enter(second, 8, "second", 300009, 3);

        Assert.Same(seat, roster.Enter(first, 7, "renamed", 300017, 4));
        Assert.Equal(2, roster.Count);
        Assert.Equal("renamed", seat.Nickname);
        Assert.Equal(300017, seat.CharacterId);
        Assert.Equal(4, seat.Level);
        Assert.Throws<InvalidOperationException>(() => roster.Enter(first, 8, "second", 300001, 1));
        Assert.Equal(7, seat.UserNum);
    }

    [Fact]
    public async Task PlazaEnterMovesThePlayerIntoThePlazaPhase()
    {
        var loop = PlazaLoop(new InGameOptions { SendPlazaSnapshot = true }, out var host);
        var player = host.SetLobbyPlayer(11, "host");

        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        Assert.Equal(BubbleFighter.Fantasy.Contracts.Sessions.SessionPhase.Plaza, player.Phase);
    }

    [Fact]
    public async Task PlazaSnapshotCarriesTheLevelIntoTheSeatRecord()
    {
        // PlazaSeat.ToIdentity used to drop the level, so the kind-3 snapshot wrote +172 = 1 for every player,
        // pinning every plaza floating icon and every DlgUserListPop row to the level-1 badge.
        var options = new InGameOptions { SendPlazaSnapshot = true, RelayCharacterPositions = false };
        var loop = PlazaLoop(options, out var host);
        host.SetLobbyPlayer(11, "host", level: 34);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        var body = host.Body(InGameOpcodes.S2CGotoRoom);
        var r = new PacketReader(body);
        _ = SessionSettingBlob.Read(ref r);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(1u, r.ReadUInt32());            // one player
        var expected = WireLengthSupport.Written(
            w => RoomPlayerInfo.ForSeat(11, "host", 0, 34).Write(w));
        Assert.Equal(expected, r.ReadBytes(expected.Length).ToArray());
    }

    [Fact]
    public async Task PlazaSnapshotSendsThePartyUpdateThatReachesTheSpawner()
    {
        // The 117 kind-3 branch installs the seat array and the map564 entries
        // but never reaches a spawner. The plaza's character spawner sub_920940
        // is reached only from sub_9064B0, the S2C 201 handler, which is also
        // the only client writer of the party member vector at
        // dword_18E5660+8/+12. Without it a plaza player's own avatar has no
        // spawn pass, and neither does a peer's.
        var options = new InGameOptions { SendPlazaSnapshot = true, SendPartyUpdate = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host", equippedItemId: 300017);
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);

        var party = new PacketReader(host.Body(InGameOpcodes.S2CUpdateParty));
        Assert.Equal(2, party.ReadInt32());          // dword_18E5660+4; sub_99D510 requires 2
        Assert.Equal(1u, party.ReadUInt32());        // member count
        Assert.Equal(11, party.ReadInt32());         // member +4: the local UserNum
        Assert.Equal("host", party.ReadWideStringPrefixed());
        for (var i = 0; i < 4; i++)
            party.ReadInt32();                       // member +32..+44
        Assert.Equal(1u, party.ReadUInt32());        // profile-map entry count
        Assert.Equal(11, party.ReadInt32());         // entry key
        Assert.Equal(300017, party.ReadInt32());     // entry payload: the equipped character id
        Assert.Equal(0u, party.ReadUInt32());        // entry vecA: empty
        Assert.Equal(0u, party.ReadUInt32());        // entry vecB
        Assert.Equal(0u, party.ReadUInt32());        // entry vecC
        Assert.Equal(0u, party.ReadUInt32());        // entry blob: zero-length
        Assert.Equal(0u, party.ReadUInt32());        // entry items
        Assert.Equal(0, party.Remaining);
    }

    [Fact]
    public void KindTwoRoomBodyPreservesTheSeatRecordAfterAnEmptySlot()
    {
        var setting = RoomWireSupport.WaitingSetting() with { Kind = SessionSettingBlob.MainMenuKind };
        var identity = new RoomPlayerIdentity(9, "nick", 1);
        var entry = new ProfileMapEntry(9, 300017);

        var body = RoomSessionBlob.GotoRoom(setting, [identity], [entry]);

        // The historical 522-byte body omitted vacant slot zero. Preserve its
        // populated seat and tail while inserting the required 188-byte empty record.
        Assert.Equal(710, body.Length);
        var previous = Convert.FromHexString(KindTwoBody);
        Assert.Equal(previous[..249], body[..249]);
        Assert.Equal(2, BitConverter.ToInt32(body, 249));
        Assert.Equal(0, BitConverter.ToInt32(body, 253));
        Assert.Equal(previous[253..], body[441..]);
    }

    /// <summary>
    /// Historical kind-2 body used to pin unchanged seat and tail bytes after sparse-seat correction.
    /// Taken from the shipped writer; a change to any shared field moves it.
    ///
    /// </summary>
    private const string KindTwoBody =
        "0000000000000000000000000000000000000000000000000200000000000000280000000000000000000000000000000000" +
        "0000000000000000000000000800000074007200610069006E0069006E006700000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001" +
        "000000020100000000000000000000010009000000040000006E00690063006B000100000000000000000000000000000000" +
        "00000000000000000000000000000000000000FFFFFFFF000000000000000000000000FFFFFFFF0000000000000000000000" +
        "0000000000000000000000000001000000000000000100000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001" +
        "00000009000000F1930400000000000000000000000000100000000900000000000000F1930400F893040000000000000000" +
        "00000000000000000000000000000000000000000000";

    [Fact]
    public async Task PlazaPositionRelaysToEveryOtherPlazaPlayer()
    {
        // kr-1256 sub_91FB80 applies S2C 301 on stage 3 and its gate blocks only stage 5, so the plaza's
        // continuous multiplayer surface is this relay. The sender's identity is prepended to the body exactly
        // as the room relay does, so a peer can attribute the motion.
        var options = new InGameOptions { RelayCharacterPositions = true, SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        var peer = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(host, 11, "host");
        EnterMainMenu(peer, 22, "peer");
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        await loop.Plaza.HandleEnterPlazaAsync(peer, PlazaEnter(), CancellationToken.None);

        // A seat with no room is the plaza twin of the report (C2S 301).
        await loop.HandleCharacterPositionAsync(host, PlazaPosition(), CancellationToken.None);

        // The peer receives the relayed position; the sender receives nothing.
        Assert.Equal(
            new ushort[] { InGameOpcodes.S2CCharacterPositionPlaza },
            peer.Opcodes.Where(o => o == InGameOpcodes.S2CCharacterPositionPlaza).ToArray());
        Assert.DoesNotContain(InGameOpcodes.S2CCharacterPositionPlaza, host.Opcodes);

        var body = peer.Body(InGameOpcodes.S2CCharacterPositionPlaza);
        var r = new PacketReader(body);
        Assert.Equal(11, r.ReadInt32());   // the sender's identity, prepended
        Assert.Equal(7, r.ReadInt32());    // counter
        Assert.Equal(26, body.Length);
    }

    [Fact]
    public async Task PlazaPositionWithoutTheRelayOptionSendsNothingToPeers()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true };
        var loop = PlazaLoop(options, out var host);
        var peer = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(host, 11, "host");
        EnterMainMenu(peer, 22, "peer");
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        await loop.Plaza.HandleEnterPlazaAsync(peer, PlazaEnter(), CancellationToken.None);

        await loop.HandleCharacterPositionAsync(host, PlazaPosition(), CancellationToken.None);

        Assert.DoesNotContain(InGameOpcodes.S2CCharacterPositionPlaza, peer.Opcodes);
    }

    [Fact]
    public async Task PlazaChatReachesEveryoneInThePlazaFromTheSendersPlazaSeat()
    {
        var options = new InGameOptions { SendPlazaSnapshot = true, BroadcastRoomChat = true };
        var loop = PlazaLoop(options, out var host);
        EnterMainMenu(host, 11, "host");
        await loop.Plaza.HandleEnterPlazaAsync(host, PlazaEnter(), CancellationToken.None);
        var guest = new RoomPushTests.RecordingSession(11);
        EnterMainMenu(guest, 22, "guest");
        await loop.Plaza.HandleEnterPlazaAsync(guest, PlazaEnter(), CancellationToken.None);

        await loop.HandleRoomChatAsync(guest, Chat(0, "hi"), CancellationToken.None);

        var expected = Convert.ToHexString(GameToClient.ChatPanel(1, "guest", 0, 0, "hi", -1, 0, 0, string.Empty, 0));
        Assert.Equal(expected, Convert.ToHexString(host.Body(InGameOpcodes.S2CChatPanel)));
        Assert.Equal(expected, Convert.ToHexString(guest.Body(InGameOpcodes.S2CChatPanel)));
    }

    [Fact]
    public async Task TeamChatReachesOnlyTheSendersTeamInTheTeamColour()
    {
        var options = new InGameOptions { SendPlazaSnapshot = false, BroadcastRoomChat = true };
        var registry = new RoomRegistry(options);
        var loop = new RoomLoop(registry, new ModeCatalog(), options, "127.0.0.1", NullLogger.Instance, null, new PlazaRoster());
        var host = new RoomPushTests.RecordingSession(10);
        var mate = new RoomPushTests.RecordingSession(11);
        var rival = new RoomPushTests.RecordingSession(12);
        var (room, hostSeat) = registry.CreateFromSetting(host, RoomWireSupport.OrdinarySettingWithMap(), 1, "host");
        var (_, mateSeat) = registry.Join(mate, room.Id, 2, "mate");
        var (_, rivalSeat) = registry.Join(rival, room.Id, 3, "rival");
        hostSeat.Team = 1;
        mateSeat.Team = 1;
        rivalSeat.Team = 2;

        await loop.HandleRoomChatAsync(host, Chat(RoomLoop.TeamChatChannel, "go"), CancellationToken.None);

        var expected = Convert.ToHexString(GameToClient.ChatPanel(
            checked((short)hostSeat.Slot), "host", 0, 0, "go", RoomLoop.TeamChatColour, 0, 0, string.Empty, 0));
        Assert.Equal(expected, Convert.ToHexString(host.Body(InGameOpcodes.S2CChatPanel)));
        Assert.Equal(expected, Convert.ToHexString(mate.Body(InGameOpcodes.S2CChatPanel)));
        Assert.DoesNotContain(InGameOpcodes.S2CChatPanel, rival.Opcodes);
        // The team reply writes A1A1FFFF after the message.
        Assert.EndsWith("A1A1FFFF00000000000000000000000000000000", expected);
    }

    private static InboundPacket Chat(int channel, string message) =>
        new(InGameOpcodes.C2SRoomChat, WireLengthSupport.Written(w =>
        {
            w.WriteInt32(channel);
            w.WriteWideStringPrefixed(message);
            w.WriteWideStringPrefixed(string.Empty);
        }));

    private RoomLoop PlazaLoop(InGameOptions options, out RoomPushTests.RecordingSession host)
    {
        host = new RoomPushTests.RecordingSession(10);
        return new RoomLoop(
            new RoomRegistry(options),
            new ModeCatalog(),
            options,
            "127.0.0.1",
            NullLogger.Instance,
            new CharacterRoster(_characters),
            new PlazaRoster());
    }

    private void EnterMainMenu(
        RoomPushTests.RecordingSession session, int userNum, string nickname, int? equippedItemId = null, int? characterType = null)
    {
        // The plaza enter requires a player past the login burst.
        session.SetLobbyPlayer(userNum, nickname);
        if (equippedItemId is { } equipped)
            _characters[userNum] = equipped;
        else if (characterType is { } type)
            _characters[userNum] = 300000 + type;
    }

    private sealed class CharacterRoster(Dictionary<int, int> characters) : BubbleFighter.Fantasy.Contracts.Fighters.ISeatCharacterResolver
    {
        public ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct) =>
            ValueTask.FromResult(characters.GetValueOrDefault(userNum));
    }

    private static InboundPacket PlazaEnter() =>
        new(InGameOpcodes.C2SUnknown106, ReadOnlyMemory<byte>.Empty);

    private static InboundPacket PlazaPosition() =>
        new(InGameOpcodes.C2SCharacterPositionPlaza, WireLengthSupport.Written(
            w => new CharacterPositionRequest(7, 0, 0, 0, 0, 0, 0).Write(w)));

    /// <summary>
    /// Counts the <c>players</c> entries of an S2C 117 body, for the tests that
    /// only care how many identities a snapshot carries.
    ///
    /// </summary>
    private static int CountPlayers(PacketReader r)
    {
        _ = SessionSettingBlob.Read(ref r);
        _ = r.ReadInt32();      // unknown544
        _ = r.ReadInt32();      // unknown548
        return (int)r.ReadUInt32();
    }
}
