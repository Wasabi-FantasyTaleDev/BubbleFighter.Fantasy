using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Party;
using System.Buffers;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using System.Buffers.Binary;
using System.Net;
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
/// Waiting rooms from the plaza: the room list (C2S 401), create (402), join from the list (403)
/// or by id (C2S 517), leave (404), seat close (509), chat, team change and the in-room
/// position report. A join installs the room for the joining seat only and announces the new
/// seat to the occupants with an S2C 503 record, because the S2C 117 install rebuilds a room
/// unconditionally (occupants receive only the S2C 503). The plaza side
/// lives in <see cref="PlazaLoop"/>, the shared install burst in <see cref="RoomPush"/>.
///
/// </summary>
internal sealed class RoomLoop
{
    internal GameCommandHandler? Commands { get; set; }

    /// <summary>Gets or sets the sender of the notices that tell a room's members about a mode change; none by default.</summary>
    internal RoomNotices ModeNotices { get; set; } = new(null, null);
    private readonly RoomRegistry _registry;
    private readonly ModeCatalog _modes;
    private readonly InGameOptions _options;
    private readonly string _relayAddress;
    private readonly ISeatCharacterResolver? _characters;
    private readonly ILogger _logger;
    private readonly PlazaRoster _plaza;
    private readonly IEquippedViewPush? _equippedView;
    private readonly IEquipmentSelection? _selection;
    private readonly IMessageSender<PartyUpdated>? _partySender;

    /// <summary>Builds the room handlers and the plaza handlers they leave the plaza through.</summary>
    /// <param name="registry">The room registry this loop drives.</param>
    /// <param name="modes">The recovered game-mode table.</param>
    /// <param name="options">The row configuration.</param>
    /// <param name="relayAddress">The relay IPv4 the connect record advertises.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="characters">Optional equipped-character resolver.</param>
    /// <param name="plaza">Optional plaza roster; one is created when absent.</param>
    /// <param name="equippedView">
    /// Optional publisher for the S2C 806 equipped view. A working reference
    /// session sends that record as part of its room-entry sequence, right after
    /// the room install; the client upserts it into the profile map the in-room
    /// spawner and the appearance rebuilder read, so it is what gives a room's
    /// character an equipped set to build from.
    /// </param>
    /// <param name="selection">Current persisted equipment, preferred over login and optional roster snapshots.</param>
    /// <param name="partySender">The dialect sender that retains membership during scene appearance refreshes.</param>
    public RoomLoop(
        RoomRegistry registry,
        ModeCatalog modes,
        InGameOptions options,
        string relayAddress,
        ILogger logger,
        ISeatCharacterResolver? characters = null,
        PlazaRoster? plaza = null,
        IEquippedViewPush? equippedView = null,
        IEquipmentSelection? selection = null,
        IMessageSender<PartyUpdated>? partySender = null)
    {
        _registry = registry;
        _modes = modes;
        _options = options;
        _relayAddress = relayAddress;
        _characters = characters;
        _logger = logger;
        _plaza = plaza ?? new PlazaRoster();
        _equippedView = equippedView;
        _selection = selection;
        _partySender = partySender;
        Plaza = new PlazaLoop(registry, options, logger, _plaza, characters, equippedView, selection, partySender);
    }

    /// <summary>Gets the plaza handlers; a created or joined room leaves the plaza through them.</summary>
    internal PlazaLoop Plaza { get; }

    /// <summary>
    /// Test seam for the seat character resolution order. The production path
    /// calls <see cref="ResolveSeatEquipmentAsync"/>; the tests reach the same
    /// code without a live packet.
    ///
    /// </summary>
    internal async ValueTask<int> ResolveSeatCharacterForTest(ISession game, int userNum) =>
        (await ResolveSeatEquipmentAsync(game, userNum, CancellationToken.None)).CharacterId;

    /// <summary>
    /// Resolves the current worn character before constructing any scene profile entry.
    /// Legacy login/roster fallbacks apply only when no equipment selection service is mounted.
    ///
    /// </summary>
    private ValueTask<SeatEquipment> ResolveSeatEquipmentAsync(ISession game, int userNum, CancellationToken ct) =>
        SeatCharacter.ResolveEquipmentAsync(game, userNum, _selection, _characters, ct);

    /// <summary>Updates scene snapshots from the committed bag and refreshes observers without sending local preset commands.</summary>
    internal async ValueTask OnEquipmentSelectionCommittedAsync(EquipmentSelectionCommitted change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var room = _registry.TryGet(change.Session.Id);
        ISession[] observers = [];
        if (room is not null)
        {
            lock (room.Gate)
            {
                var seat = room.FindByGame(change.Session.Id);
                if (seat is null || !ReferenceEquals(seat.Game, change.Session) || seat.UserNum != change.UserNum
                    || room.Phase != RoomPhase.Waiting)
                    return;
                new SeatEquipment(change.CharacterId, change.EquippedItemIds).Apply(seat);
                observers = room.Seats.Where(other => other.Game.Id != change.Session.Id)
                    .Select(other => other.Game).ToArray();
            }
        }
        lock (_plaza.Gate)
        {
            var seat = _plaza.Find(change.Session.Id);
            if (seat is not null && ReferenceEquals(seat.Game, change.Session) && seat.UserNum == change.UserNum)
            {
                seat.Refresh(seat.UserNum, seat.Nickname, change.CharacterId, seat.Level);
                seat.EquippedItemIds = change.EquippedItemIds;
                if (room is null)
                    observers = _plaza.Others(change.Session.Id).Where(other => _registry.TryGet(other.Game.Id) is null)
                        .Select(other => other.Game).ToArray();
            }
        }
        if (_equippedView is null) return;
        foreach (var observer in observers)
        {
            if (change.Session.Connection.IsClosed) break;
            if (!ReferenceEquals(_registry.TryGet(change.Session.Id), room)) break;
            if (observer.Connection.IsClosed) continue;
            if (room is not null && !ReferenceEquals(_registry.TryGet(observer.Id), room)) continue;
            if (room is null && (_plaza.Find(observer.Id) is null || _registry.TryGet(observer.Id) is not null)) continue;
            await _equippedView.SendRemoteAsync(observer, change.UserNum, change.CharacterId, change.EquippedItemIds, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The build's encoder for the open-room list; null where the build has no room list.</summary>
    internal IMessageSender<RoomListSnapshot>? RoomListSender { get; init; }

    /// <summary>
    /// Answers a lobby room-list request with every open room. The pairing and row fields are the
    /// codec's; the reply stays behind the option gate as a deployment choice.
    /// </summary>
    public ValueTask HandleRoomListAsync(ISession session, RoomListRequested request, CancellationToken ct)
    {
        if (!_options.SendRoomList)
        {
            _logger.LogInformation("Session {Id} room list request (cursor {Cursor}) is gated off; not replied.",
                session.Id, request.Cursor);
            return ValueTask.CompletedTask;
        }
        var sender = RoomListSender
            ?? throw new InvalidOperationException("Room list requested on a build without a room list encoder.");

        var open = _registry.OpenRooms();
        var rows = new List<RoomListEntry>(open.Count);
        foreach (var room in open)
        {
            lock (room.Gate)
            {
                var name = string.IsNullOrEmpty(room.RoomName) ? room.Setting.Unknown8 : room.RoomName;
                // 0x63C240 binds +40 to lbRoomMaster, consumed by 0x63CF90.
                var master = room.Seats.FirstOrDefault(s => s.Slot == room.MasterSlot)?.Nickname ?? string.Empty;
                // Closed seats cannot be joined, so the row counts only the open ones.
                rows.Add(new RoomListEntry(room.Id, name, master, room.ModeId, room.Seats.Count,
                    room.MaxSeats - room.ClosedSlots.Count, room.Password.Length > 0)
                    { MatchInProgress = room.Phase != RoomPhase.Waiting });
            }
        }

        _logger.LogInformation("Session {Id} room list (cursor {Cursor}): {Count} room row(s).",
            session.Id, request.Cursor, rows.Count);
        return sender.SendAsync(session, new RoomListSnapshot(request.Cursor, rows), ct);
    }

    /// <summary>
    /// C2S 402 create-room from the lobby dialog. The live client sends this
    /// with <c>SessionSetting.kind = 0</c> (observed 2026-09-20 10:15Z and
    /// 10:34Z) and re-sends it once the room name is typed; both are answered
    /// idempotently. The reply set is the walked pair: the S2C 503 create
    /// reply (handler <c>sub_9DA720</c>, verified body) then the room push
    /// (S2C 501 snapshot + S2C 118 user list).
    ///
    /// </summary>
    public async ValueTask HandleUnknown402Async(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = Unknown402Request.Parse(packet.Payload.Span);
        // Live evidence (2026-09-20 10:15Z): the real client creates a room with
        // C2S 402 and SessionSetting.kind = 0. The earlier refusal of kind 0 was
        // an unproven guard that dropped the live client on room create, so the
        // kind is now accepted and normalized by the registry.
        if (request.Setting.Map.IsEmpty)
        {
            throw new InvalidOperationException(
                "C2S 402 MapMeta is empty. Create-room must carry the client dialog mode/map blob; empty-room stubs are refused.");
        }

        _modes.RequireKnown(request.Setting.Map.Unknown4);

        // The mode id rides in MapMeta.unknown4 on the client's create request
        // (the verified read site for the authoritative mode is the session
        // game-spec object, which this push populates). Resolve it through the
        // verified sub_711210 table so the room records its family and the
        // create-time rule overrides for the log and later match start.
        var mode = Data.GameMode.Resolve(request.Setting.Map.Unknown4);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);

        // Live observation (2026-09-20 10:32Z): after a successful create the
        // client re-sends C2S 402 carrying the typed room name. A session that
        // already owns a room gets the room state re-pushed instead of a
        // connection-killing refusal; the create is idempotent.
        var existing = _registry.TryGet(session.Id);
        if (existing is not null)
        {
            // The waiting dialog's state 0x14 makes the first seat's client
            // re-send C2S 402 on its own at start time (sub_858250). That
            // re-send must not install a fresh room over a round that is
            // already starting or playing - the install deletes the session and
            // every live character - so a non-waiting phase is answered with
            // nothing and recorded.
            if (existing.Phase is not RoomPhase.Waiting)
            {
                _logger.LogInformation(
                    "Session {Id} C2S 402 re-send while room {Room} is {Phase}; no install during a live round.",
                    session.Id, existing.Id, existing.Phase);
                return;
            }
            await existing.Executor.ExecuteAsync(
                operationCt => RepeatCreateCoreAsync(session, request, existing, operationCt), ct).ConfigureAwait(false);
            return;
        }

        // A Random/Random_Hot selection is stored as it came; the
        // round's concrete map is drawn at each start (MapCatalog), and only S2C 510 carries it.
        var resolvedSetting = request.Setting;
        // A "#id#" tag in the title forces the mode and is not part of the title the room shows.
        var taggedMode = ReadTaggedMode(session, request.Setting.Unknown8, out var shownTitle);
        if (taggedMode is { } forcedMode)
            resolvedSetting = request.Setting with
            {
                Unknown8 = shownTitle, Map = request.Setting.Map with { Unknown4 = forcedMode },
            };
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        var (room, seat) = _registry.CreateFromSetting(
            session, resolvedSetting, userNum, nickname, resolvedSetting.Unknown8, listed: false);
        equipment.Apply(seat);
        lock (room.Gate)
            room.Password = request.Password;
        _logger.LogInformation(
            "Session {Id} C2S 402 created room {Room} slot={Slot} character={Character} clientKind={Kind} roomKind={RoomKind} clientMode={ClientMode} installedMode={InstalledMode} family={Family} tag=0x{Tag:X2} mapFlags24=0x{Flags24:X8}.",
            session.Id, room.Id, seat.Slot, seat.CharacterId, request.Setting.Kind, room.Setting.Kind,
            request.Setting.Map.Unknown4, room.ModeId, mode.Family, request.OptTag,
            request.Setting.Map.Unknown24);
        // The host's whole create burst runs on the new room's executor, and the room is published
        // only once it is sent, so no join can reach the room ahead of the host's own install.
        await room.Executor.ExecuteAsync(
            operationCt => CreatePushCoreAsync(session, room, seat, operationCt), ct).ConfigureAwait(false);
        _registry.Publish(room);
        if (taggedMode is not null)
            await room.Executor.ExecuteAsync(
                operationCt => ModeNotices.AnnounceModeAsync(room, room.ModeId, operationCt), ct).ConfigureAwait(false);

        // The creator left the plaza; the players still there see that seat vacated.
        await Plaza.LeavePlazaAsync(session.Id, ct).ConfigureAwait(false);
    }

    // Runs on the room's executor so the seat-only re-install is serial with joins and leaves.
    private async ValueTask RepeatCreateCoreAsync(ISession session, Unknown402Request request, PlayRoom existing,
        CancellationToken ct)
    {
        RoomSeat? existingSeat;
        lock (existing.Gate)
            existingSeat = existing.FindByGame(session.Id);
        // A re-send from a session the registry maps to a room but holds no seat
        // for is what a live client does right after the prepare wall dismisses
        // and it rebuilds its room. Throwing here is the same failure that closed
        // a live session in the relay-endpoint handler, so it is logged and the
        // re-push is skipped rather than the connection dropped.
        if (existingSeat is null)
        {
            _logger.LogInformation(
                "Session {Id} C2S 402 re-send while mapped to room {Room} but with no seat; not re-pushing.",
                session.Id, existing.Id);
            return;
        }

        // A C2S 402 from a client that already hosts this room is a re-ask, not
        // a new room - and the live client needs the install that comes with
        // answering it. Measured on the operator's client (2026-09-23 11:11 UTC,
        // session 7): the create pushed one kind-2 install (688 B) and the client
        // stayed in the plaza; it then re-sent C2S 402, and answering that with
        // only the S2C 503 left it outside on every attempt. Before the
        // narrowing it entered on the second create, which is this same install
        // landing a second time. So the install is sent here, aimed at the asking
        // seat alone. What must NOT come back is the all-seat push: its S2C 117
        // deletes the room session and rebuilds it unconditionally (0x9AC6C2,
        // 0x9AC70F, 0x9AC768 in sub_9AC490), which is what tore the other
        // occupants out of the rooms they were already standing in. Why the first
        // install does not apply is still open and stays queued (item 7: the
        // session+12 = 4 short-circuit at 0x9ACAB4).
        // 0x962200 writes the dialog title into setting+8; the trailing string is separate.
        var taggedMode = ReadTaggedMode(session, request.Setting.Unknown8, out var shownTitle);
        lock (existing.Gate)
            existing.RoomName = shownTitle;
        // The others learn the forced mode from the map update; the asking seat gets it with its install below.
        if (taggedMode is { } forcedMode)
            await ApplyModeAsync(existing, forcedMode, session, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} C2S 402 re-send while hosting room {Room}; answered with a room install for that seat alone plus the S2C 503 seat record.",
            session.Id, existing.Id);
        await PushRoomToSeatAsync(existing, existingSeat, ct).ConfigureAwait(false);
        List<ProfileMapEntry> reentries;
        lock (existing.Gate)
            reentries = RoomPush.ProfileEntries(
                existing, _options.ModelItemIds);
        await session.SendAsync(
            InGameOpcodes.S2CCreateRoomReply,
            GameToClient.CreateRoomReply(existingSeat.Slot, existingSeat.ToPlayerInfo(), reentries),
            ct).ConfigureAwait(false);
        if (taggedMode is { } announced)
            await ModeNotices.AnnounceModeAsync(existing, announced, ct).ConfigureAwait(false);
    }

    // Reads the mode tag of a room title. Returns the forced mode and the title to show, or null (title as typed)
    // when there is no tag, the tag is not a mode id, or the deployment's mode table does not list it.
    private ushort? ReadTaggedMode(ISession session, string title, out string shownTitle)
    {
        shownTitle = title;
        var tag = RoomModeTag.Extract(title);
        if (tag.RejectedTag is { } rejected)
            _logger.LogWarning("Session {Id} room title '{Title}' has the mode tag {Tag}, which is not a mode id from {Min} to {Max}; the tag is ignored.",
                session.Id, title, rejected, RoomModeTag.MinModeId, RoomModeTag.MaxModeId);
        if (tag.ModeId is not { } mode)
            return null;
        if (_modes.HasRows && !_modes.Ids.Contains((ushort)mode))
        {
            _logger.LogWarning("Session {Id} room title '{Title}' names mode {Mode}, which is not in the loaded mode table; the tag is ignored.",
                session.Id, title, mode);
            return null;
        }
        shownTitle = tag.Title;
        return (ushort)mode;
    }

    /// <summary>
    /// Changes a waiting room's mode and shows it to its seats (except <paramref name="except"/>): the mode lives in the
    /// room's MapMeta, which S2C 505 carries to the clients (the same update the host's map choice uses). Runs on the
    /// room's executor; the caller sends the notice.
    ///
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="mode">The mode id.</param>
    /// <param name="except">A session that is answered by other means, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the mode table does not list the mode or the room is not waiting.</returns>
    internal async ValueTask<bool> ApplyModeAsync(PlayRoom room, ushort mode, ISession? except, CancellationToken ct)
    {
        if (_modes.HasRows && !_modes.Ids.Contains(mode))
            return false;
        RoomSeat[] seats;
        MapMetaBlob map;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Waiting)
                return false;
            room.UpdateMap(room.Setting.Map with { Unknown4 = mode });
            room.ModeId = mode;
            map = room.Setting.Map;
            seats = [.. room.Seats.Where(seat => seat.Game.Id != except?.Id)];
        }
        await RoomPush.SendMapUpdateAsync(map, seats, ct).ConfigureAwait(false);
        _logger.LogInformation("Room {Room} mode set to {Mode} by the server: S2C 505 sent to {Seats} seat(s).", room.Id, mode, seats.Length);
        return true;
    }

    // The C2S 402 create burst: install, S2C 503 seat record, then the optional stage enter and waiting wall.
    private async ValueTask CreatePushCoreAsync(ISession session, PlayRoom room, RoomSeat seat, CancellationToken ct)
    {
        // The 501 snapshot installs the room session and its seat arrays; the
        // S2C 503 create reply writes the host seat record into them, so the
        // push must land first.
        await PushRoomAsync(room, ct).ConfigureAwait(false);
        List<ProfileMapEntry> entries;
        lock (room.Gate)
            entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
        await session.SendAsync(
            InGameOpcodes.S2CCreateRoomReply,
            GameToClient.CreateRoomReply(seat.Slot, seat.ToPlayerInfo(), entries),
            ct).ConfigureAwait(false);

        // The stage-5 spawn path. A live session showed an ordinary created room parking in RoomStage8vs8
        // forever - the twenty-second C2S 517 poll comes from that stage (sub_978C80, whose only caller is the
        // stage's own vtable slot 74) - while the training room, which receives the stage enter, shows its
        // character. S2C 510 spawns nothing itself: it copies the room seats into the in-play session
        // (sub_9E5E70 -> sub_87C990 case 5 -> sub_753790), which is the state the sibling spawner sub_920940
        // and its shared factory and dresser need. The record MUST carry a real map: sub_9E5E70 copies its
        // three wstrings onto session+184/+208/+232 (sub_9E5E70), so an empty triple erases whatever the
        // 117 install wrote. A C2S 402 room carries the client's selection in the selected-map slot, which may
        // be a Random* marker the client resolves through its own pool - that is still the room's map, so it is
        // echoed as-is. A room with no map at all is left alone: sending the empty record would do more harm
        // than sending nothing. The mechanism is verified; its sufficiency is inferred.
        if (_options.EnterGameStageOnCreate)
        {
            var map = RoomPush.FirstMapKey(room.Setting.Map);
            if (map.Length > 0)
            {
                var stage = EnterGameStageRecord.ForMap(map, SessionSettingBlob.RoomStateInRoom);
                await session.SendAsync(
                    InGameOpcodes.S2CEnterGameStage, GameToClient.EnterGameStage(stage), ct)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Session {Id} room {Room} create: S2C 510 stage enter sent with the {Map} scene keys.",
                    session.Id, room.Id, map);
            }
            else
            {
                _logger.LogInformation(
                    "Session {Id} room {Room} create: S2C 510 withheld - the room carries no map key to repeat.",
                    session.Id, room.Id);
            }
        }

        // The waiting wall: state 0 opens the client's "waiting for players"
        // dialog (UI resource 1760) right after the room settles, and state
        // 0x14 at start time dismisses it; the two sends form the waiting
        // dialog lifecycle used by this room-entry path.
        if (_options.SendWaitingDialog)
        {
            await session.SendAsync(
                InGameOpcodes.S2CWaitingDialog, GameToClient.WaitingDialog(0, 0), ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Session {Id} room {Room} create: S2C 128 waiting dialog opened.",
                session.Id, room.Id);
        }
    }

    /// <summary>
    /// C2S 403 room-list row click. Body: the clicked row's <c>SessionSetting+0</c> value (the room
    /// id, which is what the server wrote there) plus a prefixed wstring (password candidate). Joins that room;
    /// the join reply is the S2C 517 / 518 handshake pair plus the room push. <c>kr-1256 sub_854C90</c> returns
    /// this exact slot, so before the row writer carried the room id every click resolved to the same value and
    /// the wrong room was joined.
    /// </summary>
    public async ValueTask HandleUnknown403Async(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = Unknown403Request.Parse(packet.Payload.Span);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);
        // A room that closed after the list was sent is refused with S2C 103, not a dropped connection.
        if (_registry.FindJoinable(request.Unknown0) is not { } target)
        {
            await RefuseJoinAsync(session, request.Unknown0, GameToClient.JoinResultNoRoom, ct).ConfigureAwait(false);
            return;
        }
        // S2C 201 writes the local equipment map used by 0xDB9F30; a zero character
        // reaches its unchecked table lookup even when S2C 117's room map is valid.
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        try
        {
            await target.Executor.ExecuteAsync(
                operationCt => JoinFromListCoreAsync(session, request, target, userNum, nickname, equipment, operationCt),
                ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room's executor stopped because the room closed between the lookup and the queue.
            await RefuseJoinAsync(session, request.Unknown0, GameToClient.JoinResultNoRoom, ct).ConfigureAwait(false);
            return;
        }
        // Leaving the plaza is the plaza's own operation, queued once the room operation is done.
        if (_registry.TryGet(session.Id) is not null)
            await Plaza.LeavePlazaAsync(session.Id, ct).ConfigureAwait(false);
    }

    // Runs on the room's executor: the refusal checks, the seat and the announcements see one member table.
    private async ValueTask JoinFromListCoreAsync(ISession session, Unknown403Request request, PlayRoom target,
        int userNum, string nickname, SeatEquipment equipment, CancellationToken ct)
    {
        int? refusal;
        lock (target.Gate)
        {
            refusal = target.Phase switch
            {
                RoomPhase.Waiting when target.IsFull => GameToClient.JoinResultRoomFull,
                // The prompt fills the password only for a flagged row; an open room ignores the field.
                RoomPhase.Waiting when target.Password.Length > 0 && request.Password != target.Password
                    => GameToClient.JoinResultWrongPassword,
                RoomPhase.Waiting => null,
                RoomPhase.Closed => GameToClient.JoinResultNoRoom,
                _ => GameToClient.JoinResultRoomStarted,
            };
        }
        if (refusal is { } result)
        {
            await RefuseJoinAsync(session, target.Id, result, ct).ConfigureAwait(false);
            return;
        }

        var (room, joined) = _registry.Join(session, target.Id, userNum, nickname, equipment.CharacterId, equipment.ItemIds);
        _logger.LogInformation(
            "Session {Id} C2S 403 joined room {Room} slot={Slot} character={Character} passwordLength={Len}.",
            session.Id, room.Id, joined.Slot, joined.CharacterId, request.Password.Length);
        // S2C 128 result 0 leads the joiner's burst, before the install. It opens the lobby finding-room dialog (kr-1256 sub_858250, resource 1760), which
        // the room stage switch of the following 117 tears down; no 0x14 dismissal follows.
        await session.SendAsync(InGameOpcodes.S2CWaitingDialog, GameToClient.WaitingDialog(0, 0), ct)
            .ConfigureAwait(false);
        // The joiner gets the room install; the members already inside get only the new seat's
        // S2C 503. A kind-2 install on a client that
        // already shows the room rebuilds it from nothing (0x9AC6C2 onward).
        await PushRoomToSeatAsync(room, joined, ct).ConfigureAwait(false);
        await SendSeatRecordsToJoinerAsync(room, joined, ct).ConfigureAwait(false);
        await AnnounceSeatToOccupantsAsync(room, joined, ct).ConfigureAwait(false);
        if (_options.RepublishRoomUiOnJoin)
            await RepublishRoomUiAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C2S 509: the room master closes or reopens an empty seat. The client sends it only for the
    /// master clicking an empty seat (<c>sub_980300</c>), so anything else is logged and ignored.
    /// Every seat receives the seat's new record (S2C 511 with <c>RoomPlayerInfo</c> +1), and a
    /// closed seat is skipped by joins and counts toward a full room.
    ///
    /// </summary>
    public async ValueTask HandleSeatCloseAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var span = packet.Payload.Span;
        if (span.Length != 8)
            throw new PacketFormatException($"C2S 509 must be 8 bytes, got {span.Length}.", 0);
        var slot = BinaryPrimitives.ReadInt32LittleEndian(span);
        var closedState = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        if (closedState is not (0 or 1))
            throw new PacketFormatException("C2S 509 closed state must be 0 or 1.", 4);
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogInformation("Session {Id} C2S 509 seat {Slot} with no room; ignored.", session.Id, slot);
            return;
        }
        try
        {
            await room.Executor.ExecuteAsync(
                operationCt => SeatCloseCoreAsync(session, room, slot, closedState == 1, operationCt), ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room closed after the lookup; there is no seat left to change.
        }
    }

    private async ValueTask SeatCloseCoreAsync(ISession session, PlayRoom room, int slot, bool closed, CancellationToken ct)
    {
        RoomSeat[] seats;
        lock (room.Gate)
        {
            var sender = room.FindByGame(session.Id);
            if (sender is null || sender.Slot != room.MasterSlot || room.Phase != RoomPhase.Waiting
                || slot < 0 || slot >= room.MaxSeats || room.Seats.Any(seat => seat.Slot == slot))
            {
                _logger.LogInformation(
                    "Session {Id} C2S 509 seat {Slot} closed={Closed} refused: not the waiting room's master or not an empty seat.",
                    session.Id, slot, closed);
                return;
            }
            if (!room.SetSlotClosed(slot, closed))
                return;
            seats = [.. room.Seats];
        }
        var record = GameToClient.SeatClosed(slot, closed);
        foreach (var seat in seats)
            await seat.Game.SendAsync(InGameOpcodes.S2CSeatRecord, record, ct).ConfigureAwait(false);
        _logger.LogInformation("Room {Room} seat {Slot} {State} by the master; {Count} seat(s) told with S2C 511.",
            room.Id, slot, closed ? "closed" : "reopened", seats.Length);
    }

    // S2C 103 goes to the requester only; nobody else learns of a refused join.
    private async ValueTask RefuseJoinAsync(ISession session, int roomId, int result, CancellationToken ct)
    {
        await session.SendAsync(InGameOpcodes.S2CJoinResult, GameToClient.JoinResult(result), ct).ConfigureAwait(false);
        _logger.LogInformation("Session {Id} join of room {Room} refused with S2C 103 result {Result}.", session.Id, roomId, result);
    }

    private PlayRoom RequireLiveRoom(int roomId) =>
        _registry.FindJoinable(roomId) ?? throw new InvalidOperationException($"C2S 517 room id {roomId} is not a live room.");

    public async ValueTask HandleUnknown404Async(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 404");
        if (!SessionPlayer.IsPastLoginBurst(session))
            throw new InvalidOperationException("C2S 404 requires an authenticated lobby session.");
        // The return goes through S2C 116; its reader 0x9AC2C0 selects the same lobby stage as C2S 106.
        if (_registry.TryGet(session.Id) is { } room)
        {
            await room.Executor.ExecuteAsync(
                operationCt => SeatDeparture.LeaveAsync(_registry, room, session.Id, _options.ModelItemIds, _logger, operationCt),
                ct).ConfigureAwait(false);
        }
        else
        {
            // No live room: this only drops a mapping left behind by a deleted room.
            _registry.Detach(session.Id);
        }
        await Plaza.HandleEnterPlazaAsync(session,
            new InboundPacket(InGameOpcodes.C2SUnknown106, ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
        _logger.LogInformation("Session {Id} C2S 404 returned from its room to the lobby.", session.Id);
    }

    public async ValueTask HandleJoinRoomAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = JoinRoomRequest.Parse(packet.Payload.Span);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);

        // Live observation (2026-09-20 12:11Z): after a successful create the
        // client re-sends C2S 517 (room id 0) on every 1500 environment probe
        // while it waits for the room to become ready — hundreds of times, each
        // answered with a full 517/518/117/118 push. That push storm is what
        // starves the connection into a drop, and it never carries new state.
        // A session that already sits in a room therefore gets ONE push per
        // room-state change (the handshake pair, then the room install) and
        // afterwards only the handshake echo; a join for a DIFFERENT existing
        // room is still a loud failure.
        var current = _registry.TryGet(session.Id);
        if (current is not null)
        {
            await current.Executor.ExecuteAsync(
                operationCt => RepeatJoinCoreAsync(session, request, current, operationCt), ct).ConfigureAwait(false);
            return;
        }

        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        var target = RequireLiveRoom(request.RoomId);
        await target.Executor.ExecuteAsync(
            operationCt => JoinCoreAsync(session, request, target, userNum, nickname, equipment, operationCt), ct).ConfigureAwait(false);
        // The joiner left the plaza; queued on the plaza once the room operation is done.
        await Plaza.LeavePlazaAsync(session.Id, ct).ConfigureAwait(false);
    }

    // Runs on the seated room's executor, so the echo and the one-time install read the seat as it is now.
    private async ValueTask RepeatJoinCoreAsync(ISession session, JoinRoomRequest request, PlayRoom current, CancellationToken ct)
    {
        if (request.RoomId != 0 && current.Id != request.RoomId)
        {
            // A seated client that clicks a different room in the list is
            // still asking to leave its own room. Refusing with an exception
            // killed the connection on a live session (2026-09-21: the
            // client sent C2S 517 with another room's id while seated and
            // the handler failed), so the request is answered with the
            // current room's handshake instead: the client stays where it
            // is and the click is a no-op, which is the safe reading while
            // no client site proves a room-to-room switch exists.
            _logger.LogInformation(
                "Session {Id} C2S 517 for room {Requested} while seated in room {Current}; echoing the current room instead of refusing.",
                session.Id, request.RoomId, current.Id);
        }

        RoomSeat? seat;
        lock (current.Gate)
            seat = current.FindByGame(session.Id);
        // Mapped to a room with no seat is the state a session is in right after
        // its room was replaced. The join handshake has nothing to say to such a
        // session, so it is logged and dropped rather than throwing - the same
        // guard the relay-endpoint, 402 re-send, position-report, relay-motion
        // and result handlers already carry.
        if (seat is null)
        {
            _logger.LogInformation(
                "Session {Id} C2S 517 join mapped to room {Room} but with no seat; ignoring.",
                session.Id, current.Id);
            return;
        }

        var reToken = seat.Key.Unknown0;
        await session.SendAsync(InGameOpcodes.S2CJoinHandshake, RoomSessionBlob.JoinHandshake(reToken), ct)
            .ConfigureAwait(false);
        await session.SendAsync(
            InGameOpcodes.S2CJoinSlotUpdate, RoomSessionBlob.JoinSlotUpdate(reToken, 0, 0), ct)
            .ConfigureAwait(false);

        if (seat.JoinAcked)
        {
            // The live client re-sends C2S 517 on every environment probe while it waits for the room to become
            // ready (observed 2026-09-20 22:58Z: four re-sends in forty seconds and no C2S 511 at all). The
            // room-stage factory the S2C 117 handler only records is consumed by the frame loop, so the create
            // burst's S2C 118 is dropped by sub_9ACD00's RoomStage8vs8 cast and the per-seat S2C 511 never
            // reaches the spawner. Each re-send is therefore the moment to re-issue the room UI: by the time
            // the client asks again the stage is live, so the rows land and the spawner runs.
            if (_options.RepublishRoomUiOnJoin)
                await RepublishRoomUiAsync(current, ct).ConfigureAwait(false);
            _logger.LogDebug(
                "Session {Id} C2S 517 re-send (id {RoomId}) while in room {Room}; handshake echo{Republish}.",
                session.Id, request.RoomId, current.Id,
                _options.RepublishRoomUiOnJoin ? " plus room UI" : " only");
            return;
        }

        seat.JoinAcked = true;
        // This is the push that installs the room for a client the registry
        // already seats - the joiner's own first re-send - so with the flag
        // off it lands on that seat alone and the other occupants are left
        // untouched: their rooms already hold this seat, because the join that
        // created it announced it to them by S2C 503. Broadcasting it instead
        // made every one of them delete and rebuild its room (0x9AC6C2,
        // sub_9D9250, sub_91EDD0).
        _logger.LogInformation(
            "Session {Id} C2S 517 re-send (id {RoomId}) while in room {Room} slot={Slot}; room state pushed once.",
            session.Id, request.RoomId, current.Id, seat.Slot);
        await PushRoomToSeatAsync(current, seat, ct).ConfigureAwait(false);
        if (_options.RepublishRoomUiOnJoin)
            await RepublishRoomUiAsync(current, ct).ConfigureAwait(false);
    }

    // Runs on the target room's executor: the seat is added and announced before any other membership change.
    private async ValueTask JoinCoreAsync(ISession session, JoinRoomRequest request, PlayRoom target, int userNum, string nickname,
        SeatEquipment equipment, CancellationToken ct)
    {
        var (room, joined) = _registry.Join(session, target.Id, userNum, nickname, equipment.CharacterId, equipment.ItemIds);
        _logger.LogInformation(
            "Session {Id} C2S 517 joined room {Room} slot={Slot} character={Character} by id {RoomId}.",
            session.Id, room.Id, joined.Slot, joined.CharacterId, request.RoomId);

        // Handshake pair verified from sub_9DF920: S2C 517 carries one i32 token and the handler echoes it as
        // the first field of the S2C 518 slot update. The token is the joining seat's relay key first dword,
        // which is unique per seat and per process.
        var token = joined.Key.Unknown0;
        await session.SendAsync(InGameOpcodes.S2CJoinHandshake, RoomSessionBlob.JoinHandshake(token), ct)
            .ConfigureAwait(false);
        await session.SendAsync(InGameOpcodes.S2CJoinSlotUpdate, RoomSessionBlob.JoinSlotUpdate(token, 0, 0), ct)
            .ConfigureAwait(false);

        // The install goes to the joiner only. Broadcasting it would reach the
        // occupants with a kind-2 body that rebuilds their room from nothing - the 117 handler deletes the room
        // session at 0x9AC6C2, republishes it at 0x9AC70F and 0x9AC768,
        // reconstructs the seats through sub_9D9250 and frees every live character
        // through sub_91EDD0, with no comparison against the room already
        // installed - so each of them visibly leaves and re-enters the room it is
        // already standing in. The S2C 503 writes one seat's record into the array
        // the install made, refreshes the chips and reaches the spawner, and does
        // not call the stage setter, so it is the packet that tells an installed
        // client about a seat without rebuilding it.
        await PushRoomToSeatAsync(room, joined, ct).ConfigureAwait(false);
        await SendSeatRecordsToJoinerAsync(room, joined, ct).ConfigureAwait(false);
        await AnnounceSeatToOccupantsAsync(room, joined, ct).ConfigureAwait(false);

        if (_options.RepublishRoomUiOnJoin)
            await RepublishRoomUiAsync(room, ct).ConfigureAwait(false);

        // The joiner lands in the same waiting wall the host got on create; the
        // dismissal at start time is broadcast to every seat there.
        if (_options.SendWaitingDialog)
        {
            await session.SendAsync(
                InGameOpcodes.S2CWaitingDialog, GameToClient.WaitingDialog(0, 0), ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Session {Id} joined room {Room}: S2C 128 waiting dialog opened.",
                session.Id, room.Id);
        }

        // The joiner needs the same stage enter the host got on create, or the
        // two sit in different stages and cannot see each other: the host is
        // moved to the game stage by its own S2C 510, and a joiner that only
        // receives the 117 install stays in the room stage. The record carries
        // the room's map, and is withheld when the room has none, exactly as on
        // the create path.
        if (_options.EnterGameStageOnCreate)
        {
            var map = RoomPush.FirstMapKey(room.Setting.Map);
            if (map.Length > 0)
            {
                await session.SendAsync(
                    InGameOpcodes.S2CEnterGameStage,
                    GameToClient.EnterGameStage(
                        EnterGameStageRecord.ForMap(map, SessionSettingBlob.RoomStateInRoom)),
                    ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Session {Id} joined room {Room}: S2C 510 stage enter sent with the {Map} scene keys.",
                    session.Id, room.Id, map);
            }
        }
    }

    /// <summary>
    /// Tells the seats that already hold a room about one newcomer without
    /// reinstalling their room: one S2C 503 per occupant carrying the new seat's
    /// <c>RoomPlayerInfo</c> and the room's full profile-map entry list.
    ///
    /// </summary>
    /// <remarks>
    /// <para>
    /// The S2C 503 handler <c>sub_9DA720</c> writes one seat's full record into
    /// the seat array the install built, refreshes the chips and reaches the
    /// spawner, and it does not call the MainApp stage setter - which is exactly
    /// the difference from the S2C 117 (<c>0x9AC6C2</c>, <c>0x9AC70F</c>,
    /// <c>sub_9D9250</c>, <c>sub_91EDD0</c>) that lets an occupant learn a seat
    /// without leaving and re-entering the room. The same record the install would
    /// have written is reused (<see cref="RoomPlayerInfo.ForSeat"/> with the
    /// seat's own slot, level and team), and the profile map carries every seat so
    /// the avatar gate has the newcomer's character id.
    /// </para>
    /// <para>
    /// The S2C 511 (<c>sub_9DB1E0</c>) was rejected for this job: it writes a seat
    /// too, but in the in-round stage it reaches the destroy-and-respawn branch
    /// that killed a live session, and it carries no profile record at all. The
    /// S2C 118 wait list still follows through
    /// <see cref="RepublishRoomUiAsync(PlayRoom, CancellationToken)"/> when the row
    /// arms it, which is the seatless packet that cannot touch the stage.
    /// </para>
    /// </remarks>
    /// <param name="room">The room the seat joined.</param>
    /// <param name="joined">The new seat to announce.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <summary>
    /// Sends the joiner one S2C 503 per seat after its install: its own seat first, then every
    /// seat already in the room. Each record
    /// goes through <c>sub_9DA720</c>, which writes that seat, folds its profile map and reaches the
    /// character spawner (<c>sub_921130</c>) without switching the stage.
    ///
    /// </summary>
    private async Task SendSeatRecordsToJoinerAsync(PlayRoom room, RoomSeat joined, CancellationToken ct)
    {
        byte[][] bodies;
        lock (room.Gate)
        {
            var entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
            bodies = [.. room.Seats
                .OrderBy(seat => seat.Game.Id == joined.Game.Id ? 0 : 1)
                .ThenBy(seat => seat.Slot)
                .Select(seat => GameToClient.CreateRoomReply(seat.Slot, seat.ToPlayerInfo(), entries))];
        }
        foreach (var body in bodies)
            await joined.Game.SendAsync(InGameOpcodes.S2CCreateRoomReply, body, ct).ConfigureAwait(false);
        // The install lists only occupied seats; closed empty seats follow as their own records.
        int[] closedSlots;
        lock (room.Gate)
            closedSlots = [.. room.ClosedSlots.Order()];
        foreach (var slot in closedSlots)
            await joined.Game.SendAsync(InGameOpcodes.S2CSeatRecord, GameToClient.SeatClosed(slot, true), ct).ConfigureAwait(false);
    }

    private async Task AnnounceSeatToOccupantsAsync(PlayRoom room, RoomSeat joined, CancellationToken ct)
    {
        RoomSeat[] occupants;
        List<ProfileMapEntry> entries;
        lock (room.Gate)
        {
            occupants = [.. room.Seats.Where(seat => seat.Game.Id != joined.Game.Id)];
            entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
        }

        var body = GameToClient.CreateRoomReply(
            joined.Slot,
            joined.ToPlayerInfo(),
            entries);
        foreach (var seat in occupants)
            await seat.Game.SendAsync(InGameOpcodes.S2CCreateRoomReply, body, ct).ConfigureAwait(false);

        // S2C 503 announces the seat but does not fill the profile map the avatar builder
        // reads, so each occupant also gets the newcomer's equipped view as the observer
        // S2C 806 (never 836/837, which rewrite the recipient's own preset).
        if (_equippedView is not null && joined.CharacterId is >= 300000 and <= 301999)
        {
            var joinedEquipped = joined.EquippedItemIds ?? (IReadOnlyList<int>)[joined.CharacterId];
            foreach (var seat in occupants)
                await _equippedView.SendRemoteAsync(
                    seat.Game, joined.UserNum, joined.CharacterId, joinedEquipped, ct)
                    .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Room {Room} announced slot {Slot} ({UserNum}/{Nickname}) to {Occupants} occupant(s) with the S2C 503 seat record instead of a room install.",
            room.Id, joined.Slot, joined.UserNum, joined.Nickname, occupants.Length);
    }

    /// <summary>
    /// Re-issues the room UI for every seat of a live room: the S2C 118 user list (one <c>{nickname,
    /// level}</c> row per seat) followed by one S2C 511 seat record per seat. Used when the client proves the
    /// room stage is live after the create burst has already been consumed - the burst's S2C 118 is dropped by
    /// <c>sub_9ACD00</c>'s <c>RoomStage8vs8</c> cast and the burst's S2C 511 never reaches the spawner, because
    /// the stage factory is only recorded by the S2C 117 handler and consumed later by the frame loop.
    /// </summary> <param name="room">The room whose seats are re-published.</param> <param
    /// name="ct">Cancellation token.</param>
    internal async Task RepublishRoomUiAsync(PlayRoom room, CancellationToken ct) =>
        await RoomPush.RepublishRoomUiAsync(room, _logger, ct).ConfigureAwait(false);

    /// <summary>
    /// S2C 509 <c>colour1</c> (object +84 in <c>sub_10C4A50</c>) for a team line. C2S 502 carries
    /// channel 1 for team chat and the reply carries this value where a normal line carries -1.
    ///
    /// </summary>
    internal const int TeamChatColour = unchecked((int)0xFFFFA1A1);

    /// <summary>C2S 502 channel for team chat; 0 is the open channel.</summary>
    internal const int TeamChatChannel = 1;

    /// <summary>
    /// C2S 502 chat. In a room the line goes to every seat, or to the sender's team only on the
    /// team channel; in the plaza it goes to everyone in the plaza, with the sender's plaza seat as
    /// the actor code (the 509 handler resolves the name from the installed seat table).
    ///
    /// </summary>
    public async ValueTask HandleRoomChatAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = RoomChatRequest.Parse(packet.Payload.Span);
        var message = request.Unknown1.Length > 0 ? request.Unknown1 : request.Unknown2;
        if (Commands is not null && await Commands.TryHandleAsync(session, message, ct).ConfigureAwait(false))
            return;
        if (!_options.BroadcastRoomChat || message.Length == 0)
        {
            _logger.LogInformation(
                "Session {Id} C2S 502 chat channel={Channel} s1Length={L1} s2Length={L2}. Broadcast off or empty; not replied.",
                session.Id, request.Unknown0, request.Unknown1.Length, request.Unknown2.Length);
            return;
        }
        var team = request.Unknown0 == TeamChatChannel;

        int actor;
        string nickname;
        ISession[] recipients;
        var room = _registry.TryGet(session.Id);
        if (room is not null)
        {
            lock (room.Gate)
            {
                var sender = room.FindByGame(session.Id);
                if (sender is null)
                {
                    _logger.LogInformation("Session {Id} C2S 502 chat with no seat in room {Room}; dropped.", session.Id, room.Id);
                    return;
                }
                actor = sender.Slot;
                nickname = sender.Nickname;
                recipients = [.. room.Seats.Where(seat => !team || seat.Team == sender.Team).Select(seat => seat.Game)];
            }
        }
        else if (_plaza.Find(session.Id) is { } plazaSeat)
        {
            actor = plazaSeat.Slot;
            nickname = plazaSeat.Nickname;
            // The plaza has no teams: a team line there reaches the whole plaza like an open one.
            recipients = [.. _plaza.Snapshot().Where(seat => _registry.TryGet(seat.Game.Id) is null).Select(seat => seat.Game)];
        }
        else
        {
            _logger.LogInformation("Session {Id} C2S 502 chat outside a room or the plaza; dropped.", session.Id);
            return;
        }

        // S2C 509 is the chat render frame (bubble via sub_9DF410 + the chat-panel insert sub_4DBBD0). The 509
        // drop branch exempts the sender's seat, so the sender's own line arrives only via this echo - the
        // recipients include the sender.
        var body = GameToClient.ChatPanel(
            checked((short)actor), nickname, 0, 0, message,
            team ? TeamChatColour : -1, 0, 0, string.Empty, 0);
        foreach (var recipient in recipients)
            await recipient.SendAsync(InGameOpcodes.S2CChatPanel, body, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Session {Id} C2S 502 {Scope} {Channel} chat sent to {Count} recipient(s) as S2C 509 from seat {Slot}.",
            session.Id, room is not null ? "room" : "plaza", team ? "team" : "open", recipients.Length, actor);
    }

    public async ValueTask HandleRoomChatTypedAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = RoomChatTypedRequest.Parse(packet.Payload.Span);
        if (Commands is not null && await Commands.TryHandleAsync(session, request.Unknown1, ct).ConfigureAwait(false))
            return;
        var room = _registry.TryGet(session.Id);
        RoomSeat[]? seats = null;
        RoomSeat? sender = null;
        if (room is not null)
        {
            lock (room.Gate)
            {
                sender = room.FindByGame(session.Id);
                if (sender is not null)
                    seats = [.. room.Seats];
            }
        }

        if (!_options.BroadcastRoomChat || sender is null || seats is null || request.Unknown1.Length == 0)
        {
            _logger.LogInformation(
                "Session {Id} C2S 520 room chat type={Type} sLength={Length} u2={U2} u3={U3}. Broadcast off, no seat, or empty message; not replied.",
                session.Id, request.Unknown0, request.Unknown1.Length, request.Unknown2, request.Unknown3);
            return;
        }

        // Same fan-out as C2S 502: the typed builders (types 101/102/103) route to C2S 520 with the same
        // message object, so the room reply is the same S2C 508 record.
        var body = GameToClient.SeatChat(sender.Slot, request.Unknown1);
        foreach (var seat in seats)
            await seat.Game.SendAsync(InGameOpcodes.S2CSeatChat, body, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Session {Id} C2S 520 room chat type={Type} broadcast to {Seats} seat(s) as S2C 508 from slot {Slot}.",
            session.Id, request.Unknown0, seats.Length, sender.Slot);
    }

    /// <summary>
    /// C2S 515 room team-change send. The builders that share the opcode
    /// (<c>sub_4B3AC0</c>, <c>sub_4A1EB0</c>, <c>sub_ADEF30</c>) all stamp the
    /// optional-block tag first (58 = 0x3A block present, 186 = 0xBA block
    /// absent); on a live mode-1 waiting room the click arm sends a 2-byte
    /// frame whose second byte is the requested team id. The handler applies
    /// it to the seat and re-publishes the room UI so every client's team
    /// board re-renders.
    ///
    /// </summary>
    /// <remarks>
    /// Team ids are the values <c>sub_980730</c>'s board switch reads from
    /// <c>seat+4</c> (sub_980730): 1 and 2 are the teamed labels, 0 the
    /// unassigned waiting value. A second byte outside 1..2 cannot be pinned
    /// to a team from the client code, so the handler toggles 1/2 instead and logs
    /// the raw bytes for a future pin.
    /// </remarks>
    /// <param name="session">The sending game session.</param>
    /// <param name="packet">The inbound frame.</param>
    /// <param name="ct">Cancellation.</param>
    public async ValueTask HandleChangeTeamAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var payload = packet.Payload.ToArray();
        var room = _registry.TryGet(session.Id);
        if (room is null)
        {
            _logger.LogInformation(
                "Session {Id} C2S 515 team change with no room; dropped.", session.Id);
            return;
        }

        await room.Executor.ExecuteAsync(
            operationCt => ChangeTeamCoreAsync(session, room, payload, operationCt), ct).ConfigureAwait(false);
    }

    // Runs on the room's executor so the S2C 504 recipients are the members at the moment the team changed.
    private async ValueTask ChangeTeamCoreAsync(ISession session, PlayRoom room, byte[] payload, CancellationToken ct)
    {
        RoomSeat? seat;
        lock (room.Gate)
            seat = room.FindByGame(session.Id);
        if (seat is null)
        {
            _logger.LogInformation(
                "Session {Id} C2S 515 team change with no seat in {Room}; dropped.", session.Id, room.Id);
            return;
        }
        var current = seat.Team;

        byte requested;
        if (payload.Length >= 2 && payload[1] is 1 or 2)
        {
            requested = payload[1];
        }
        else
        {
            // Toggle between the two teamed labels; 0 (unassigned) joins blue.
            requested = current == 1 ? (byte)2 : (byte)1;
        }

        lock (room.Gate)
            seat.Team = requested;
        // The team label lives in the seat record's unknown4, which only the
        // S2C 117 players vector (and the 503 create reply / 504 / 511) write.
        // Re-sending the 117 for that is wrong: its kind-2 arm reaches SetStage,
        // so every team click tears the room down and installs it again - the
        // live report is that a team change re-enters the room, and the client
        // hook log shows a fresh manager object per click. S2C 504 is the packet
        // that would carry one seat, but its body is unrecovered in the client code
        // and inventing a layout is worse than the display lag, so the default
        // publishes the seatless 118 wait list and the full push is opt-in.
        if (_options.RepublishFullRoomOnTeamChange)
        {
            await PushRoomAsync(room, ct).ConfigureAwait(false);
        }
        else
        {
            // S2C 504 writes one seat's label into seat+4 and refreshes every
            // board slot without calling the stage setter, which is exactly what
            // a team click needs: the 117 rebuilds the room unconditionally and
            // the 118 wait list carries no seat fields at all.
            var label = GameToClient.SeatTeamLabel(seat.Slot, requested);
            RoomSeat[] occupants;
            lock (room.Gate)
                occupants = [.. room.Seats];
            foreach (var target in occupants)
                await target.Game.SendAsync(InGameOpcodes.S2CSeatTeamLabel, label, ct).ConfigureAwait(false);
        }
        _logger.LogInformation(
            "Session {Id} C2S 515 team change slot={Slot} {From}->{To} (raw: {Bytes}).",
            session.Id, seat.Slot, current, requested,
            string.Join(' ', payload.ToArray()));
    }

    /// <summary>
    /// C2S 511 character position report. The walk proves the builder <c>sub_913640</c> emits the
    /// periodic in-room character / input relay, and the room twin of C2S 301, so the body is parsed by
    /// <c>CharacterPositionRequest</c>. </summary> <remarks> This is also the reliable post-entry trigger for
    /// the room UI. The S2C 117 handler only STORES the room stage factory (MainApp vtable slot 7 = sub_87C990)
    /// and never constructs the stage, so the 117 -> 118 burst lands before UiRoomStageUserList exists and the
    /// S2C 118 handler (sub_9ACD00) discards the rows. The first C2S 511 from a seated client proves the room
    /// stage is live, so the room UI is re-issued here behind republish_room_ui_on_position: S2C 118 then one
    /// S2C 511 seat record per seat. </remarks>
    public async ValueTask HandleCharacterPositionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = CharacterPositionRequest.Parse(packet.Payload.Span);
        var room = _registry.TryGet(session.Id);
        if (room is null)
        {
            // No room, so this is the plaza twin of the report (C2S 301, built by the same sub_913640 while no
            // room session exists). The plaza's continuous multiplayer surface is this relay: kr-1256
            // sub_91FB80 applies S2C 301 on stage 3 and its gate blocks only stage 5. The sender's identity is
            // prepended to the body exactly as the room relay does, so a peer can attribute the motion.
            if (_options.RelayCharacterPositions)
            {
                var sender = _plaza.Find(session.Id);
                PlazaSeat[] peers;
                lock (_plaza.Gate)
                    peers = [.. _plaza.Others(session.Id)];

                if (sender is not null && peers.Length > 0)
                {
                    var w = PacketWriter.Create();
                    request.WriteReply(w, sender.UserNum);
                    var payload = WireUtil.Written(w);
                    foreach (var peer in peers)
                        await peer.Game.SendAsync(
                            InGameOpcodes.S2CCharacterPositionPlaza, payload, ct).ConfigureAwait(false);
                    _logger.LogDebug(
                        "Session {Id} C2S 301 plaza position counter={Counter} relayed to {Peers} peer(s) as S2C 301.",
                        session.Id, request.Counter, peers.Length);
                }
            }

            _logger.LogDebug(
                "Session {Id} C2S 511 position counter={Counter} without a room.", session.Id, request.Counter);
            return;
        }

        RoomSeat[] seats;
        RoomPlayerIdentity[] identities;
        RoomPlayerInfo[] records;
        lock (room.Gate)
        {
            // The room-exists guard above already lets a session through whose seat
            // is gone, so this is reachable: a replaced room keeps the mapping and
            // drops the seat. The snapshot below is for the whole room anyway and
            // does not depend on the sender, so a missing seat is logged and ignored
            // rather than thrown.
            if (room.FindByGame(session.Id) is null)
            {
                _logger.LogDebug(
                    "Session {Id} C2S 511 position counter={Counter} has no seat; ignoring.",
                    session.Id, request.Counter);
                return;
            }

            seats = [.. room.Seats];
            identities = [.. room.Seats.Select(s => s.ToIdentity())];
            records = [.. room.Seats.Select(
                s => s.ToPlayerInfo())];
        }

        // Peer relay: one 26-byte record per other seat.
        await RelayCharacterPositionAsync(session, request, ct).ConfigureAwait(false);

        // A position report also proves the client consumed the S2C 510 stage
        if (!_options.RepublishRoomUiOnPosition)
        {
            _logger.LogDebug(
                "Session {Id} C2S 511 position counter={Counter}; room UI re-publish off.",
                session.Id, request.Counter);
            return;
        }

        // The room UI re-issue: the S2C 117 handler only records a pending stage factory, so an S2C 118 in the
        // create burst is discarded; a C2S 511 from a seated client proves the room stage is live, which is
        // what makes the re-publish land.
        await RepublishRoomUiAsync(room, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Session {Id} C2S 511 position counter={Counter}; room UI re-published to {Seats} seat(s).",
            session.Id, request.Counter, seats.Length);
    }

    /// <summary>
    /// Character position report (peer relay). The builder <c>sub_913640</c> emits the same 22-byte
    /// body as C2S 301 in the plaza and C2S 511 inside a room, so both opcodes parse identically. When <see
    /// cref="InGameOptions.RelayCharacterPositions"/> is on the body is relayed verbatim to the other seats of
    /// the room with the sender identity prepended (26-byte S2C 301 / S2C 514); the sender is skipped and no
    /// physics is re-derived. </summary> <remarks> Called by <see cref="HandleCharacterPositionAsync"/>, which
    /// adds the room-UI re-publish. </remarks>
    internal async ValueTask RelayCharacterPositionAsync(
        ISession session, CharacterPositionRequest request, CancellationToken ct)
    {
        if (!_options.RelayCharacterPositions)
        {
            _logger.LogDebug(
                "Session {Id} C2S 301/511 position counter={Counter} (relay disabled)",
                session.Id, request.Counter);
            return;
        }

        // A position report from a session with no room is an ordinary outcome - the
        // plaza reports before any room exists, and a session whose room was replaced
        // reports once more before it notices. Throwing here took connections down
        // the same way the relay-endpoint handler's throw did, so it is logged and
        // dropped instead.
        if (_registry.TryGet(session.Id) is not { } room)
        {
            _logger.LogDebug(
                "Session {Id} C2S 301/511 position counter={Counter} with no room; ignoring.",
                session.Id, request.Counter);
            return;
        }
        RoomSeat sender;
        RoomSeat[] peers;
        lock (room.Gate)
        {
            if (room.FindByGame(session.Id) is not { } found)
            {
                _logger.LogDebug(
                    "Session {Id} C2S 301/511 position counter={Counter} has no seat; ignoring.",
                    session.Id, request.Counter);
                return;
            }

            sender = found;
            peers = [.. room.Seats.Where(s => s.Game.Id != session.Id)];
        }

        var identity = sender.UserNum;
        var relayOpcode = room.Setting.Kind == SessionSettingBlob.MainMenuKind
            ? InGameOpcodes.S2CCharacterPositionRoom
            : InGameOpcodes.S2CCharacterPositionPlaza;
        var w = PacketWriter.Create();
        request.WriteReply(w, identity);
        var payload = ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
        foreach (var peer in peers)
            await peer.Game.SendAsync(relayOpcode, payload, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Session {Id} C2S 301/511 position counter={Counter} relayed to {Peers} peer(s) as S2C {Relay}.",
            session.Id, request.Counter, peers.Length, relayOpcode);
    }

    /// <summary>
    /// Pushes a room using the instance's configured vecA shape. Prefer this
    /// over the static form so the weapon-preset choice reaches every entry.
    ///
    /// </summary>
    /// <param name="room">The room to push.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task PushRoomAsync(PlayRoom room, CancellationToken ct) =>
        RoomPush.PushRoomAsync(
            room, null, null, _equippedView, _options.SendSeatRecords, _options.SendPartyUpdate,
            _options.SendWaitUserList, _options.EnterGameStageOnCreate, _options.ModelItemIds,
            _options.RoomState, _options.RoomInstallState, _options.PrimeSessionStateBeforeInstall, null, _logger, ct, _partySender);

    /// <summary>
    /// Pushes the room install to one seat only. The static form's
    /// <paramref name="targetSeat"/> documents why narrowing the delivery list is
    /// what lets a join reach a client that has no room yet without rebuilding the
    /// rooms of the seats that already have one.
    ///
    /// </summary>
    /// <param name="room">The room to push.</param>
    /// <param name="targetSeat">The seat that receives the burst; must belong to <paramref name="room"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task PushRoomToSeatAsync(PlayRoom room, RoomSeat targetSeat, CancellationToken ct) =>
        RoomPush.PushRoomAsync(
            room,
            identityOverride: null,
            profileOverride: null,
            equippedView: _equippedView,
            sendSeatRecords: _options.SendSeatRecords,
            sendPartyUpdate: _options.SendPartyUpdate,
            sendWaitUserList: _options.SendWaitUserList,
            enterGameStage: _options.EnterGameStageOnCreate,
            modelItemIds: _options.ModelItemIds,
            roomState: _options.RoomState,
            installState: _options.RoomInstallState,
            primeInstallState: _options.PrimeSessionStateBeforeInstall,
            targetSeat: targetSeat,
            logger: _logger,
            ct: ct,
            partySender: _partySender);

}
