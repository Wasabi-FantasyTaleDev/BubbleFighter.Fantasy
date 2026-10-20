using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// The room install burst every room path shares (S2C 117 with the seat and profile arrays,
/// S2C 511 seat records, S2C 201, S2C 118 and the S2C 806 equipped views), the room-UI
/// re-publish, and the profile entries the S2C 503 seat records carry.
///
/// </summary>
internal static class RoomPush
{
    /// <summary>
    /// Sends the room's MapMeta to the given seats as S2C 505 (dispatcher case 0x1F9 copies it into session+140 and
    /// redraws the mode and map board). The host's map choice and a server-set mode both travel this way.
    ///
    /// </summary>
    /// <param name="map">The room's MapMeta after the change.</param>
    /// <param name="seats">The seats that receive it.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async ValueTask SendMapUpdateAsync(MapMetaBlob map, IEnumerable<RoomSeat> seats, CancellationToken ct)
    {
        var update = RoomSessionBlob.RoomMapUpdate(map);
        foreach (var seat in seats)
            await seat.Game.SendAsync(InGameOpcodes.S2CRoomMapInfo, update, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-issues the room UI for every seat of a live room. Static so the
    /// combat loop can share it: the reply set is identical whichever loop
    /// detected that the room stage is live.
    ///
    /// </summary>
    /// <param name="room">The room whose seats are re-published.</param>
    /// <param name="logger">Logger for the re-publish line.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task RepublishRoomUiAsync(
        PlayRoom room, ILogger logger, CancellationToken ct)
    {
        RoomSeat[] seats;
        RoomPlayerIdentity[] identities;
        RoomPlayerInfo[] records;
        lock (room.Gate)
        {
            seats = [.. room.Seats];
            identities = [.. room.Seats.Select(s => s.ToIdentity())];
            records = [.. room.Seats.Select(
                s => s.ToPlayerInfo())];
        }

        var waitList = RoomSessionBlob.WaitUserList(identities);
        foreach (var seat in seats)
            await seat.Game.SendAsync(InGameOpcodes.S2CWaitUserList, waitList, ct).ConfigureAwait(false);

        // The per-seat S2C 511 records are off for the same reason as in the room
        // push: the client does not need them, and
        // sub_9DB1E0's uninitialised byte can take the destroy branch.

        logger.LogInformation(
            "Room {Room} UI re-published to {Seats} seat(s) as S2C 118.",
            room.Id, seats.Length);
    }

    /// <summary>
    /// Builds the profile-map entries a room's seats present: one entry per seat that carries a
    /// resolvable equipped character id, keyed by its <c>UserNum</c>. The caller must hold the room gate. Used
    /// by the S2C 117 snapshot and the S2C 503 create reply, which both apply the map before the kind switch.
    /// </summary> <param name="room">The room whose seats are snapshotted.</param> <param name="modelItemIds">
    /// The vecB payload each entry carries, replayed by the client into <c>equipment+64</c>. Pass an empty list
    /// to match the reference, which sends no vecB ids. </param>
    internal static List<ProfileMapEntry> ProfileEntries(
        PlayRoom room, IReadOnlyList<int>? modelItemIds = null)
    {
        List<ProfileMapEntry> entries = [];
        var position = 0;
        foreach (var seat in room.Seats)
        {
            var characterId = seat.CharacterId;
            if (characterId is < 300000 or > 301999)
                characterId = seat.LocalCharacterId;
            if (characterId is < 300000 or > 301999)
            {
                position++;
                continue;
            }

            // 0x817DF0 replaces equipment from vecA on every 117/503 update;
            // preserve the whole worn snapshot, including unchanged weapon positions.
            entries.Add(new ProfileMapEntry(
                seat.UserNum, characterId, position, modelItemIds, seat.EquippedItemIds));
            position++;
        }

        return entries;
    }

    /// <summary>
    /// Pushes a room with the shipped vecA shape. The static entry point for
    /// callers with no configured weapon-preset choice (tests, and the plaza
    /// snapshot).
    ///
    /// </summary>
    /// <param name="room">The room to push.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static Task PushRoomStaticAsync(PlayRoom room, CancellationToken ct) =>
        PushRoomAsync(
            room, null, null, null, true, true, false, false, null, SessionSettingBlob.RoomStateInRoom,
            SessionSettingBlob.RoomStateInRoom, false, null, null, ct);

    /// <summary>
    /// Pushes the room install, the per-seat S2C 511 records and the S2C 118
    /// user list. The optional overrides let a caller present a roster wider
    /// than its game sessions - the training room's AI practice seats, which
    /// have no session but must still reach the spawner's seat walk.
    ///
    /// </summary>
    /// <param name="room">The room to push.</param>
    /// <param name="identityOverride">
    /// When non-null, the identity rows to install instead of the room's own
    /// seats; the per-seat S2C 511 records are written for these rows too.
    /// </param>
    /// <param name="profileOverride">
    /// When non-null, the profile-map entries to write instead of the room's
    /// own; must cover every identity in <paramref name="identityOverride"/>.
    /// </param>
    /// <param name="equippedView">
    /// Optional publisher for the S2C 806 equipped view; when null the record is
    /// not sent.
    /// </param>
    /// <param name="sendSeatRecords">
    /// Whether the per-seat S2C 511 records are written. Off by default because the
    /// client does not need them and sub_9DB1E0 can take its destroy branch.
    /// </param>
    /// <param name="sendPartyUpdate">
    /// Whether the per-seat S2C 201 party update is written. On by default: it is
    /// the packet that spawns and dresses a Character per occupied seat.
    /// </param>
    /// <param name="sendWaitUserList">
    /// Whether the S2C 118 waiting-room list is written. Off by default because
    /// the reference's room-entry burst carries no 118.
    /// </param>
    /// <param name="enterGameStage">
    /// Whether the S2C 510 stage enter closes the burst. Off by default; it is
    /// the only lever on the stage that gates the room buttons.
    /// </param>
    /// <param name="modelItemIds">
    /// The vecB payload each profile entry carries, replayed by the client into
    /// <c>equipment+64</c>. Pass null or an empty list to match the reference,
    /// which sends no vecB ids.
    /// </param>
    /// <param name="roomState">
    /// The state the S2C 510 stage enter writes to <c>session+12</c>. The
    /// dispatcher copies the record's order-4 <c>i32</c> there
    /// (<c>sub_9D9930</c>) and five UI gates test it as equal to four, so this
    /// must match the state the room session itself carries.
    /// </param>
    /// <param name="installState">
    /// The state written into <c>session+12</c> by the S2C 117 install alone. It is
    /// 4 because that is what the kind-2 branch tests - but read
    /// <paramref name="primeInstallState"/> first: the test is
    /// <c>cmp dword ptr [eax+0Ch], 4</c> at <c>0x9ACAB4</c> on the object loaded by
    /// <c>mov eax, dword_18E56A4</c> at <c>0x9ACAAB</c>, i.e. the session the client
    /// <em>already</em> holds, evaluated before this packet is applied. So this
    /// value decides what the <em>next</em> install sees, not whether this one
    /// lands, and the start click on the same dword wants 2
    /// (<c>sub_970590</c>) - which is why the room's own
    /// <paramref name="roomState"/> still rides the S2C 501 that follows.
    /// </param>
    /// <param name="primeInstallState">
    /// Send one S2C 501 carrying <paramref name="installState"/> to each seat
    /// immediately before the install, so the dword the kind-2 compare reads is
    /// already 4 when the 117 arrives. This is the only packet-order move the
    /// instruction allows: writing 4 into the install itself cannot help the first
    /// one, which is exactly what the live client showed (two creates still needed,
    /// while a map change onto an existing room session started re-entering -
    /// evidence that carrying 4 makes <em>later</em> installs apply). Off by
    /// default: it puts a room state into the session the plaza is still using for
    /// the length of one packet, and no run has yet shown that nothing else reads
    /// it in that window.
    /// </param>
    /// <param name="targetSeat">
    /// When non-null, the whole burst goes to that one seat instead of to every
    /// seat. The installed payload is unchanged - the identity rows and the
    /// profile entries still describe the full room - because only the delivery
    /// list is narrowed; with <paramref name="sendSeatRecords"/> on the per-seat
    /// records follow the same narrowed list. Why this has to be possible: the
    /// kind-2 install rebuilds the room unconditionally, since <c>0x9AC6C2</c>
    /// deletes the current room session, <c>0x9AC70F</c> and <c>0x9AC768</c>
    /// republish it, <c>sub_9D9250</c> reconstructs the seats and
    /// <c>sub_91EDD0</c> frees every live character. Nothing in the family
    /// compares the incoming room with the installed one (<c>sub_87C990</c>
    /// drops a duplicate stage request, not the install), so a second kind-2 on a
    /// connection already in the room makes that client leave and re-enter it.
    /// Aiming the burst at the seat that has no room installed yet is therefore
    /// what lets a join be announced to the occupants by an S2C 503 seat record
    /// instead of a rebuild. Throws when the seat is not one of the room's seats.
    /// </param>
    /// <param name="logger">Receives phase warnings for recipients in an unexpected phase.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="partySender">The dialect sender that retains membership during scene appearance refreshes.</param>
    internal static async Task PushRoomAsync(
        PlayRoom room,
        IReadOnlyList<RoomPlayerIdentity>? identityOverride,
        IReadOnlyList<ProfileMapEntry>? profileOverride,
        IEquippedViewPush? equippedView = null,
        bool sendSeatRecords = false,
        bool sendPartyUpdate = true,
        bool sendWaitUserList = false,
        bool enterGameStage = false,
        IReadOnlyList<int>? modelItemIds = null,
        int roomState = SessionSettingBlob.RoomStateInRoom,
        int installState = SessionSettingBlob.RoomStateInRoom,
        bool primeInstallState = false,
        RoomSeat? targetSeat = null,
        ILogger? logger = null,
        CancellationToken ct = default,
        IMessageSender<PartyUpdated>? partySender = null)
    {
        RoomSeat[] seats;
        RoomSeat[] roomSeats;
        RoomPlayerIdentity[] identities;
        SessionSettingBlob setting;
        int masterSlot;
        List<ProfileMapEntry> profileEntries = [];
        lock (room.Gate)
        {
            seats = room.Seats.ToArray();
            roomSeats = seats;
            identities = identityOverride is null ? room.SnapshotIdentities() : [.. identityOverride];
            setting = room.Setting;
            masterSlot = room.MasterSlot;
            // Parity item 1: the reference's install carries the typed room name in the setting's first wstring
            // (SessionSetting.Unknown8, wire order 3) while the client's own create-time setting still holds
            // that slot empty - the client types the name after the create and re-sends it in the C2S 402 tail,
            // which the registry stores in RoomName. Echoing it here covers the 117 install and the 501
            // snapshots, which share this variable. Bounded: an empty name stays empty, nothing invented.
            if (room.RoomName.Length > 0)
                setting = setting with { Unknown8 = room.RoomName };
            // The profile map is the avatar gate for local and remote characters alike, and the S2C 117 map564
            // entries are its multi-entry writer. Every seat that carries a resolved character id gets an entry
            // so remote seats can render; seats without one are skipped (an unwritten id would default to an
            // empty record and fail the gate). The local seat's id falls back to the lobby character type (1 =
            // Dao, character item 300001) so a single-player room still renders its own avatar exactly like the
            // login burst does.
            profileEntries = profileOverride is null
                ? ProfileEntries(room, modelItemIds)
                : [.. profileOverride];
        }

        // A targeted install narrows only the delivery list: the body still
        // carries every seat and every profile entry, because the client's
        // sub_9D9250 rebuilds the seat array from the players vector and an
        // install that listed one seat would leave the room half-populated for
        // the seat that receives it.
        if (targetSeat is not null)
        {
            var install = Array.Find(seats, seat => seat.Game.Id == targetSeat.Game.Id)
                ?? throw new InvalidOperationException(
                    $"Room {room.Id} install was targeted at slot {targetSeat.Slot} of session {targetSeat.Game.Id}, which is not a seat of this room.");
            seats = [install];
        }

        // The S2C 117 gspGotoRoom installs the room session object (the seat array the S2C 503 create reply
        // writes into) and carries the full seat list; the S2C 118 user list refreshes the room UI. Both bodies
        // are pinned. The S2C 501 snapshot stays available for the room-info path but is not part of this
        // exchange. The install carries installState, not the room's own state, and that difference is the
        // whole reason a create used to need a second click: the kind-2 branch of the handler opens with `cmp
        // dword ptr [eax+0Ch], 4` / `jz loc_9ACB70` (kr-1256 sub_9AC490 at 0x9ACAB4 and 0x9ACAB8), so the install
        // only takes the applying path when the state it writes into session+12 is 4 - while the start click is
        // gated on that same dword being 2 (sub_970590). One client field, two gates, two values: the 117
        // lands 4 and the S2C 501 that follows it lands the room's own state, which is what the click then
        // reads.
        var waitList = RoomSessionBlob.WaitUserList(identities);

        // Order proven by the client code: the 117 install lands on every seat first, because it is the packet that
        // fills the 36 x 320-byte seat array at session+592, applies the map564 profile entries, and takes the
        // kind-2 branch to SetStage(4) - the move that constructs UiRoomStageUserList.
        foreach (var seat in seats)
        {
            // The prime, and the reason it exists. The kind-2 arm loads the
            // client's CURRENT session object before it tests the state:
            // `mov eax, dword_18E56A4` at 0x9ACAAB then `cmp dword ptr [eax+0Ch], 4`
            // at 0x9ACAB4. So the value this packet carries cannot make this
            // install apply - it only decides what the next one sees - which is
            // why a first create still sits outside while a second one, or a map
            // change onto an existing room session, walks straight in. The S2C 501
            // is the packet that writes session+12 on a connection that is already
            // in a room stage (kr-1256 sub_9D9930), so sending it once with
            // the install value immediately before the 117 is what puts 4 where the
            // compare reads it. Off by default: it writes a room state into the
            // session the plaza is still using, and no run has yet shown that no
            // other reader of that dword misbehaves in the window between the two
            // packets.
            if (primeInstallState)
            {
                await seat.Game.SendAsync(
                    InGameOpcodes.S2CRoomInfoSnapshot,
                    RoomSessionBlob.RoomInfoSnapshot(setting with { Unknown4 = installState }), ct)
                    .ConfigureAwait(false);
            }

            var gotoRoom = RoomSessionBlob.GotoRoom(
                setting with { Unknown4 = installState }, identities, profileEntries,
                masterSlot: masterSlot, localSlot: seat.Slot);
            await seat.Game.SendAsync(InGameOpcodes.S2CGotoRoom, gotoRoom, ct).ConfigureAwait(false);
            ScenePhase.Entered(seat.Game, SessionPhase.Room, logger ?? NullLogger.Instance);
        }

        if (sendWaitUserList)
        {
            foreach (var seat in seats)
                await seat.Game.SendAsync(InGameOpcodes.S2CWaitUserList, waitList, ct).ConfigureAwait(false);
        }

        // The room-info snapshot, then the equipped view, then the weapon
        // presets - the tail of the reference's room-entry burst. The 501 body is
        // exactly one SessionSetting, the same blob the 117 carries; the handler
        // applies it to the room stage when session+72 == 2, so it re-asserts the
        // room-stage gate and the room's map after the install.
        var roomInfo = RoomSessionBlob.RoomInfoSnapshot(setting);
        foreach (var seat in seats)
            await seat.Game.SendAsync(InGameOpcodes.S2CRoomInfoSnapshot, roomInfo, ct).ConfigureAwait(false);

        // S2C 201, after the room-info snapshot and before the equipped view -
        // the reference's own order is 117, 521, 501, 201, 806, 837, 836. Its
        // handler sub_9064B0 is the only client writer of the party member
        // vector at dword_18E5660+8/+12, writes a profile-map record per member
        // (sub_9064B0), and reaches the spawner sub_920940 with a3 = 0
        // (byte-verified push 0 at 0x9066CE) - which makes the party-membership
        // gate a no-op (sub_920940) and walks the whole room seat array,
        // running sub_90EF10 and sub_91F720 per seat. It is the packet that
        // turns the 117's seat list into visible characters.
        if (sendPartyUpdate)
        {
            foreach (var seat in seats)
            {
                await PartyScenePush.SendAsync(seat.Game, seat.UserNum, seat.Nickname, seat.CharacterId,
                    seat.EquippedItemIds, partySender, ct)
                    .ConfigureAwait(false);
            }
        }

        // The per-seat S2C 511 records are OFF by default and come last, after
        // the party update. The S2C 117 players vector already writes every seat
        // through the same record builders sub_9DB1E0 uses (sub_1143430 and
        // sub_1143710, copied into session+592+320*i by sub_9D9250), so a 511 is
        // redundant for seat population. Enable it to reproduce the old
        // per-seat records. Note that sub_9DB1E0's destroy-versus-spawn branch
        // at sub_9DB1E0 turns on a stack slot the client code never assigns, so
        // nothing is known about which way it goes; that is not a reason either
        // to send or to withhold the packet.
        if (sendSeatRecords)
        {
            for (var i = 0; i < seats.Length; i++)
            {
                var row = seats[i];
                var record = row.ToPlayerInfo();
                foreach (var seat in seats)
                {
                    await seat.Game.SendAsync(
                        InGameOpcodes.S2CSeatRecord,
                        GameToClient.SeatRecord(row.Slot, record),
                        ct).ConfigureAwait(false);
                }
            }
        }

        // The equipped view, last in the sequence. A working reference session
        // sends S2C 806 right after the room install, and the client handler
        // sub_9AF2F0 upserts it into the profile map dword_18E55D8 - the map the
        // in-room spawner and the appearance rebuilder read. Without it a room's
        // character has no equipped set to build from, which is the difference
        // between a room that shows its character and one that does not. The
        // record carries the seat's own identity and equipped character plus the
        // weapon ids the profile entry repeats in vecA.
        if (equippedView is not null)
        {
            foreach (var seat in seats)
            {
                var characterId = seat.CharacterId;
                if (characterId is < 300000 or > 301999)
                    continue;
                if (seat.EquippedItemIds is { } equipped)
                    await equippedView.SendSelectionAsync(seat.Game, seat.UserNum, characterId, equipped, ct)
                        .ConfigureAwait(false);
                else
                    await equippedView.SendAsync(seat.Game, seat.UserNum, characterId,
                        new[] { characterId }.Concat(ProfileMapEntry.StarterWeaponIds).ToArray(), ct).ConfigureAwait(false);
            }

            // Each delivered client also gets every other seat's equipped view: the profile map
            // (dword_18E55D8) is keyed per identity and sub_9AF2F0 upserts a remote record
            // without touching the local preset, so repeating an identical record is harmless.
            foreach (var seat in seats)
            {
                foreach (var peer in roomSeats)
                {
                    if (peer.Game is null || peer.Game.Id == seat.Game.Id)
                        continue;
                    if (peer.CharacterId is < 300000 or > 301999
                        || peer.EquippedItemIds is not { } peerEquipped)
                        continue;
                    await equippedView.SendRemoteAsync(seat.Game, peer.UserNum, peer.CharacterId, peerEquipped, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // The stage enter closes the burst. The three room buttons - map change, start, team - are gated by
        // sub_96F3A0 on the stage, and the stage is written only by the MainApp stage setter sub_87C990, whose
        // sole S2C caller is the S2C 510 handler. Without it an ordinary room never rises above the menu stage,
        // which is the state in which those buttons are dead. The record carries the room's own map keys,
        // because sub_9E5E70 copies them onto session+184/+208/+232 and an empty triple would erase the 117
        // install. The training ground is excluded: it already sends its own stage enter from the create path
        // (StartTrainingImmediately), and a second one in the same burst moves the client out of the round the
        // first one created. A live session showed that as the round being entered and then never starting. An
        // ordinary created room must stay in the room stage until its own start chain runs, so this is the only
        // place the enter belongs.
        if (enterGameStage && room.ModeId != RoomRegistry.TrainingModeId)
        {
            var map = FirstMapKey(setting.Map);
            if (map.Length > 0)
            {
                // A round-entry stage enter always carries the IN-ROUND state
                // (4): the five UI gates test session+12 == 4 once the round
                // begins, and the round's own S2C 608 re-forces 4 at
                // sub_9E7570. The waiting state (2) belongs to the 117/501
                // install family, not to the stage move.
                var stage = EnterGameStageRecord.ForMap(map, SessionSettingBlob.RoomStateInRoom);
                foreach (var seat in seats)
                {
                    await seat.Game.SendAsync(
                        InGameOpcodes.S2CEnterGameStage, GameToClient.EnterGameStage(stage), ct)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Picks the map key a room's <c>MapMeta</c> actually carries, preferring the
    /// resolved slot the S2C 510 stage enter echoes and falling back to the raw
    /// selection a C2S 402 client sends. Returns an empty string when the room
    /// has no map at all. Random-pool markers never win: the round's map is drawn
    /// from them at start (<see cref="Data.MapCatalog"/>).
    ///
    /// </summary>
    /// <param name="map">The room's map metadata.</param>
    internal static string FirstMapKey(MapMetaBlob map)
    {
        foreach (var candidate in new[] { map.Unknown68, map.Unknown44, map.Unknown92, map.Unknown116 })
        {
            if (!string.IsNullOrEmpty(candidate) && !RandomMapMarkers.Contains(candidate))
                return candidate;
        }

        return string.Empty;
    }

    /// <summary>The map-list pool markers the client resolves locally.</summary> <remarks> The client's own map
    /// list carries <c>Random</c> and <c>Random_Hot</c> entries (dword_18EA2EC / dword_18EA304), and its list
    /// builder drops both before handing one concrete entry back (sub_89E820). A marker string is not a map
    /// key: the round scene loader consumes the raw session+232 string at world build
    /// (sub_6CB270:6cc7f5-6cc7fd) and the in-round spawn picker resolves its restriction tables through the
    /// same key (sub_98DB80), so a marker at that slot loads no scene, leaves the spawn-point map empty,
    /// and the round avatar is never placed and never filled with HP - the outside-map/scattered-spawn/ HP-zero
    /// family. The room's map is therefore resolved server-side at create and at every C2S 513 map change, and
    /// no echo path can carry a marker. </remarks>
    internal static readonly IReadOnlySet<string> RandomMapMarkers =
        new HashSet<string>(StringComparer.Ordinal) { "Random", "Random_Hot" };
}
