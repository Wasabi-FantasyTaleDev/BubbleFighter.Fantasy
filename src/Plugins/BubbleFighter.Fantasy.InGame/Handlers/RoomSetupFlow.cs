using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Sessions;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// A room's own settings and seats outside the round: ready / shop / inventory flags (C2S 505),
/// the create and start click (C2S 503), quick create (C2S 107), the training ground (C2S 108),
/// the host's map choice (C2S 513), leaving (C2S 506) and a closed connection's departure.
///
/// </summary>
internal sealed class RoomSetupFlow
{
    private readonly InGameOptions _options;
    private readonly RoomRegistry _registry;
    private readonly ILogger _logger;
    private readonly MatchStartFlow _start;
    private readonly ISeatCharacterResolver? _characters;
    private readonly IEquipmentSelection? _selection;
    private readonly IEquippedViewPush? _equippedView;
    private readonly IMessageSender<PartyUpdated>? _partySender;

    /// <summary>Builds the room-setting handlers for one activated row.</summary>
    /// <param name="options">The row configuration.</param>
    /// <param name="registry">The room registry.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="start">The round start the start click and the training ground hand over to.</param>
    /// <param name="characters">Optional equipped-character resolver.</param>
    /// <param name="selection">Current persisted equipment used for every newly created scene.</param>
    /// <param name="equippedView">Optional publisher for the S2C 806 equipped view sent with the room push.</param>
    /// <param name="partySender">The dialect sender that retains membership during scene appearance refreshes.</param>
    public RoomSetupFlow(InGameOptions options, RoomRegistry registry, ILogger logger, MatchStartFlow start,
        ISeatCharacterResolver? characters = null, IEquipmentSelection? selection = null, IEquippedViewPush? equippedView = null,
        IMessageSender<PartyUpdated>? partySender = null)
    {
        _options = options;
        _registry = registry;
        _logger = logger;
        _start = start;
        _characters = characters;
        _selection = selection;
        _equippedView = equippedView;
        _partySender = partySender;
    }

    /// <summary>
    /// Resolves the current worn character for the profile map, sharing the room/plaza policy.
    ///
    /// </summary>
    private ValueTask<SeatEquipment> ResolveSeatEquipmentAsync(ISession game, int userNum, CancellationToken ct) =>
        SeatCharacter.ResolveEquipmentAsync(game, userNum, _selection, _characters, ct);

    public async ValueTask HandleRoomStatusAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = RoomStatusRequest.Parse(packet.Payload.Span);
        var room = _registry.TryGet(session.Id)
            ?? throw new InvalidOperationException("C2S 505 requires a waiting-room seat.");
        await room.Executor.ExecuteAsync(
            operationCt => RoomStatusCoreAsync(session, room, request, operationCt), ct).ConfigureAwait(false);
    }

    // Runs on the room's executor so the S2C 503 fan-out matches the member table the flag was written into.
    private async ValueTask RoomStatusCoreAsync(ISession session, PlayRoom room, RoomStatusRequest request, CancellationToken ct)
    {
        RoomSeat[] recipients;
        byte[] body;
        int slot;
        lock (room.Gate)
        {
            var seat = room.FindByGame(session.Id)
                ?? throw new InvalidOperationException("C2S 505 requires the seat's game connection.");
            if (room.Phase != RoomPhase.Waiting)
            {
                _logger.LogInformation("Session {Id} C2S 505 ignored in room phase {Phase}.", session.Id, room.Phase);
                return;
            }
            // 0x96DD20 exempts the master; 0x9DA3C0 clears a promoted master's ready bit.
            seat.Ready = seat.Slot != room.MasterSlot && request.Ready;
            seat.InShop = request.InShop;
            seat.InInventory = request.InInventory;
            slot = seat.Slot;
            recipients = [.. room.Seats];
            body = GameToClient.CreateRoomReply(slot, seat.ToPlayerInfo(),
                RoomPush.ProfileEntries(room, _options.ModelItemIds));
        }
        // Ready reports update the same slot through S2C 503.
        foreach (var recipient in recipients)
            await recipient.Game.SendAsync(InGameOpcodes.S2CCreateRoomReply, body, ct).ConfigureAwait(false);
        _logger.LogInformation("Room {Room} slot {Slot} status ready={Ready} shop={Shop} inventory={Inventory}.",
            room.Id, slot, request.Ready, request.InShop, request.InInventory);
    }

    public async ValueTask HandleCreateRoomAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = CreateRoomRequest.Parse(packet.Payload.Span);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);

        // The live client re-sends C2S 503 while it waits for the room it just
        // created (the same probe loop C2S 517 rides on). Refusing the second
        // send killed the connection three times in one live session, so a
        // session that already hosts a room gets the room state re-pushed
        // instead - the create is idempotent.
        var existing = _registry.TryGet(session.Id);
        if (existing is not null)
        {
            lock (existing.Gate)
            {
                var requester = existing.FindByGame(session.Id);
                if (requester is null || requester.Slot != existing.MasterSlot || !existing.CanStart)
                {
                    _logger.LogWarning("Session {Id} C2S 503 rejected: only the master may start a ready room {Room}.",
                        session.Id, existing.Id);
                    return;
                }
            }
            // A frame from a session that already hosts the room is the START click, not a create probe:
            // sub_96DD20 builds it once the start arm's room-state gate passes (kr-1256 sub_970590 -> kr-1256
            // sub_96DD20), carrying the client's final map selection in the same three strings sub_9A0880
            // copies from session+208/+184. The earlier reading - re-pushing the room - is what made the click
            // look like a re-entry. One start per room: a second accepted 503 after the phase left Waiting
            // re-armed the whole chain and the round restarted under the operator. Once the room is starting or
            // playing the frame is only recorded.
            if (existing.Phase is not RoomPhase.Waiting)
            {
                _logger.LogInformation(
                    "Session {Id} C2S 503 start of room {Room} ignored - phase {Phase}.",
                    session.Id, existing.Id, existing.Phase);
                return;
            }
            var startMap = existing.Setting.Map with
            {
                Unknown44 = request.Unknown3,
                Unknown68 = request.Unknown1,
                Unknown92 = request.Unknown1,
                Unknown116 = request.Unknown2,
            };
            existing.UpdateMap(startMap);
            lock (existing.Gate)
                existing.RoundRules = request.Rules;
            // A random marker is a selection too; the round's map is drawn when the start runs.
            var mapKey = MapCatalog.HasSelection(startMap) ? startMap.Unknown68 : string.Empty;

            if (mapKey.Length > 0 && _options.StartMatches)
                await _start.StartMatchAsync(existing, ct, session.Id).ConfigureAwait(false);
            else
            {
                _logger.LogInformation(
                    "Session {Id} C2S 503 start of room {Room}: map='{Map}' start_matches={Starts} - stage enter withheld.",
                    session.Id, existing.Id, mapKey, _options.StartMatches);
            }
            return;
        }

        // The three wstrings are the client's map selection, in the order
        // sub_9D7570 writes them: the resolved map key (MapMeta unknown68,
        // session+208), the map record's MapName, and the raw selection
        // (unknown44, session+184, possibly a Random* marker). sub_9A0880 fills
        // them from those slots and resolves them through sub_10E48A0, so
        // echoing them back is what gives the room a scene key - the S2C 510
        // stage enter copies its own three map keys into the same slots, and an
        // empty triple would erase whatever the S2C 117 install wrote.
        var map = MapMetaBlob.Empty() with
        {
            Unknown44 = request.Unknown3,
            Unknown68 = request.Unknown1,
            Unknown92 = request.Unknown1,
            Unknown116 = request.Unknown2,
        };
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        var (room, seat) = _registry.CreateFromRules(
            session, request.Rules, userNum, nickname, roomName: string.Empty, map, listed: false);
        equipment.Apply(seat);

        _logger.LogInformation(
            "Session {Id} C2S 503 created room {Room} slot={Slot} character={Character} rules: EndGameTimeOver={Over} DeathMatchKill={Kill} DeathMatchTimeMs={DmMs} TimeAttackTimeMs={TaMs} ConquerTimeLimitMs={CqMs} StarModeTimeLimitMs={StMs} flags52=0x{Flags:X2}; u0={U0} s1='{S1}' s2='{S2}' s3='{S3}' u4={U4} u5={U5} mapKey={MapKey}.",
            session.Id, room.Id, seat.Slot, seat.CharacterId, request.Rules.EndGameTimeOver, request.Rules.DeathMatchKill,
            request.Rules.DeathMatchTimeMs, request.Rules.TimeAttackTimeMs, request.Rules.ConquerTimeLimitMs,
            request.Rules.StarModeTimeLimitMs, request.Rules.Flags52,
            request.Unknown0, request.Unknown1, request.Unknown2, request.Unknown3, request.Unknown4, request.Unknown5,
            KnownMapKeys.Contains(request.Unknown1) ? "shipped" : "not in shipped data");
        // The 501 snapshot installs the room session and its seat arrays; the
        // S2C 503 create reply writes the host seat record into them, so the
        // push must land first.
        await room.Executor.ExecuteAsync(
            operationCt => CreatePushCoreAsync(session, room, seat, operationCt), ct).ConfigureAwait(false);
        _registry.Publish(room);
    }

    public async ValueTask HandleQuickCreateRoomAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = QuickCreateRoomRequest.Parse(packet.Payload.Span);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);

        // The three wstrings are the quick-create map selection, in the order sub_9D7440 writes them: the
        // resolved key sub_99F9C0 built and sub_10E48A0 resolved, the map record's MapName from sub_10E49C0,
        // and the key again. sub_99F580 abandons the send when the key does not resolve, so echoing them back
        // is what gives the room a scene key - the S2C 510 stage enter copies its own three map keys into the
        // same slots.
        var map = MapMetaBlob.Empty() with
        {
            Unknown44 = request.Unknown3,
            Unknown68 = request.Unknown1,
            Unknown92 = request.Unknown1,
            Unknown116 = request.Unknown2,
        };
        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        var (room, seat) = _registry.CreateFromRules(
            session, request.Rules, userNum, nickname, roomName: string.Empty, map, listed: false);
        equipment.Apply(seat);

        _logger.LogInformation(
            "Session {Id} C2S 107 quick-created room {Room} slot={Slot} character={Character} rules: EndGameTimeOver={Over} DeathMatchKill={Kill} TimeAttackTimeMs={TaMs} flags52=0x{Flags:X2}; s1='{S1}' s2='{S2}' s3='{S3}' u4={U4}.",
            session.Id, room.Id, seat.Slot, seat.CharacterId, request.Rules.EndGameTimeOver, request.Rules.DeathMatchKill,
            request.Rules.TimeAttackTimeMs, request.Rules.Flags52,
            request.Unknown1, request.Unknown2, request.Unknown3, request.Unknown4);
        // The 501 snapshot installs the room session and its seat arrays; the
        // S2C 503 create reply writes the host seat record into them, so the
        // push must land first.
        await room.Executor.ExecuteAsync(
            operationCt => CreatePushCoreAsync(session, room, seat, operationCt), ct).ConfigureAwait(false);
        _registry.Publish(room);
    }

    // Runs on the new room's executor before the room is published, so no join can precede the host's install.
    private async ValueTask CreatePushCoreAsync(ISession session, PlayRoom room, RoomSeat seat, CancellationToken ct)
    {
        await PushRoomAsync(room, ct).ConfigureAwait(false);
        List<ProfileMapEntry> entries;
        lock (room.Gate)
            entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
        await session.SendAsync(
            InGameOpcodes.S2CCreateRoomReply,
            GameToClient.CreateRoomReply(seat.Slot, seat.ToPlayerInfo(), entries),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C2S 108 single-player training-ground create. The body is only the 56-byte rule record, so the
    /// mode and the map name are server-synthesized as mode 40 and <c>rel_trainingRoom01</c>. The reply set is
    /// the walked room push: the S2C 117 install (kind 2, room state zero so the kind-2 branch does not push
    /// stage 4 for mode 40), the per-seat S2C 511 records that reach the spawner, and the S2C 118 user list.
    ///
    /// </summary>
    public async ValueTask HandleTrainingRoomAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = TrainingRoomRequest.Parse(packet.Payload.Span);
        var (userNum, nickname) = SessionPlayer.RequireIdentity(session);

        // Idempotent: the training entry is a MainMenuStage control, so a second
        // click while already hosting re-pushes the room instead of refusing.
        var existing = _registry.TryGet(session.Id);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Session {Id} C2S 108 re-send while hosting room {Room}; re-pushing the room state.",
                session.Id, existing.Id);
            await existing.Executor.ExecuteAsync(async operationCt =>
            {
                await PushRoomAsync(existing, operationCt).ConfigureAwait(false);
                if (_options.RepublishRoomUiOnJoin)
                    await RoomPush.RepublishRoomUiAsync(existing, _logger, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return;
        }

        var equipment = await ResolveSeatEquipmentAsync(session, userNum, ct).ConfigureAwait(false);
        var (room, seat) = _registry.CreateTrainingRoom(session, request.Rules, userNum, nickname);
        equipment.Apply(seat);

        _logger.LogInformation(
            "Session {Id} C2S 108 created training room {Room} slot={Slot} character={Character} mode={Mode} rules: EndGameTimeOver={Over} DeathMatchKill={Kill} DeathMatchTimeMs={DmMs} TimeAttackTimeMs={TaMs} flags52=0x{Flags:X2}.",
            session.Id, room.Id, seat.Slot, seat.CharacterId, room.ModeId, request.Rules.EndGameTimeOver,
            request.Rules.DeathMatchKill, request.Rules.DeathMatchTimeMs, request.Rules.TimeAttackTimeMs,
            request.Rules.Flags52);
        // The training room is never published, so the create push runs on its executor only to keep
        // it serial with the match start queued after it.
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            await PushRoomAsync(room, operationCt).ConfigureAwait(false);
            List<ProfileMapEntry> entries;
            lock (room.Gate)
                entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
            await session.SendAsync(
                InGameOpcodes.S2CCreateRoomReply,
                GameToClient.CreateRoomReply(seat.Slot, RoomPlayerInfo.Default(userNum, nickname), entries),
                operationCt).ConfigureAwait(false);
            if (_options.RepublishRoomUiOnJoin)
                await RoomPush.RepublishRoomUiAsync(room, _logger, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Training uses the same scene-load and authenticated-relay gates as a normal match.
        if (_options.StartTrainingImmediately && _options.StartMatches)
            await _start.StartMatchAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the C2S 513 map-list report. The three fields the client code reads
    /// are pinned and logged; the trailing bytes after them are not consumed by
    /// any reader, so nothing is derived and no reply is invented.
    ///
    /// </summary>
    /// <param name="session">The sending session.</param>
    /// <param name="packet">The inbound packet.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleMapListReportAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var report = MapListReport.Parse(packet.Payload.Span);
        var room = _registry.TryGet(session.Id);
        if (room is null)
        {
            _logger.LogDebug(
                "Session {Id} C2S 513 map list with no room; name='{Name}' key='{Key}' scene='{Scene}' dropped.",
                session.Id, report.Name, report.MapKey, report.SceneKey);
            return;
        }

        // The host's map selection. The three strings mirror the C2S 503 triple sub_9A0880 copies from
        // session+208/+184: the resolved key, the display name and the raw selection. Updating the room here
        // keeps the later start click's S2C 510 on the map the host actually chose; the room UI re-publish
        // carries the new map to every seat. A raw Random/Random_Hot selection is resolved to one concrete key
        // first: the client keys the round world on the stored string, and a marker loads no scene. A
        // Random/Random_Hot marker stays in the room (S2C 505 echoes it);
        // the concrete map is drawn at each start. A pirate map report carries the mission key in its scene
        // slot (rel_ stage, npc_ scene); the room keeps the mission in every map slot, as S2C 505 then shows.
        string? mission = report.SceneKey.StartsWith("npc_", StringComparison.OrdinalIgnoreCase) ? report.SceneKey : null;
        var map = room.Setting.Map with
        {
            Unknown44 = mission ?? (string.IsNullOrEmpty(report.SceneKey) ? report.MapKey : report.SceneKey),
            Unknown68 = mission ?? report.MapKey,
            Unknown92 = mission ?? report.MapKey,
            Unknown116 = string.IsNullOrEmpty(report.Name) ? report.MapKey : report.Name,
        };
        room.UpdateMap(map);
        // The map change travels on S2C 505: the dispatcher case at sub_9D9930 (0x1F9) deep-copies the
        // MapMeta into session+140 and fires the mode/map board redraw via sub_96BA90. The client updates its
        // own map display locally, so 505 confirms the change and carries the resolved key to every seat.
        RoomSeat[] mapSeats;
        lock (room.Gate)
            mapSeats = [.. room.Seats];
        await RoomPush.SendMapUpdateAsync(map, mapSeats, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} C2S 513 map selection of room {Room}: key='{Key}' name='{Name}' scene='{Scene}' - S2C 505 sent to {Seats} seat(s).",
            session.Id, room.Id, report.MapKey, report.Name, report.SceneKey, mapSeats.Length);
    }

    public async ValueTask HandleLeaveAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 506");
        await session.SendAsync(InGameOpcodes.S2CGotoMenu, GameToClient.GotoMenu(0), ct).ConfigureAwait(false);
        if (_registry.TryGet(session.Id) is { } room)
        {
            try
            {
                await room.Executor.ExecuteAsync(
                    operationCt => SeatDeparture.LeaveAsync(_registry, room, session.Id, _options.ModelItemIds, _logger, operationCt),
                    ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The room was deleted after the lookup; only the session mapping can remain.
                _registry.Detach(session.Id);
            }
        }
        _logger.LogInformation("Session {Id} C2S 506 left room", session.Id);
    }

    public void OnConnectionClosed(ConnectionClosedEvent e) => _ = PostDetach(e.Connection.Id);

    /// <summary>
    /// Queues the close-time detach on the seat's room executor without waiting, so the event bus is never
    /// blocked behind room work. Failures, including a full queue, are logged; the returned task never faults.
    ///
    /// </summary>
    /// <param name="connectionId">The closed connection.</param>
    internal Task PostDetach(long connectionId)
    {
        var room = _registry.FindByConnection(connectionId);
        if (room is null)
        {
            // No seated session: this only drops the connection mapping and touches no room state.
            _registry.DetachConnection(connectionId);
            return Task.CompletedTask;
        }

        Task Post()
        {
            try
            {
                return room.Executor.ExecuteAsync(async operationCt =>
                {
                    if (_registry.TryGetSessionOfConnection(connectionId, out var sessionId))
                        await SeatDeparture.LeaveAsync(_registry, room, sessionId, _options.ModelItemIds, _logger, operationCt)
                            .ConfigureAwait(false);
                    _registry.DetachConnection(connectionId);
                });
            }
            catch (ObjectDisposedException)
            {
                // The room was deleted after the lookup, so no seat is left; only the mapping remains.
                _registry.DetachConnection(connectionId);
                return Task.CompletedTask;
            }
        }

        // The close event can be raised inline from a send inside another room's operation; posting from a
        // fresh execution context keeps that from tripping the executor's reentry guard. Nothing waits here.
        Task posted;
        if (ExecutionContext.IsFlowSuppressed())
            posted = Task.Run(Post);
        else
        {
            using (ExecutionContext.SuppressFlow())
                posted = Task.Run(Post);
        }
        return posted.ContinueWith(completed =>
        {
            if (completed.Exception is { } failure)
                _logger.LogError(failure.GetBaseException(),
                    "Room {Room} could not detach closed connection {Connection}; the seat stays until the room closes.",
                    room.Id, connectionId);
            else if (completed.IsCanceled)
                _logger.LogError("Room {Room} canceled the detach of closed connection {Connection}.", room.Id, connectionId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void DetachSession(ISession session) => _registry.Detach(session.Id);

    /// <summary>
    /// Pushes a room with this instance's configured vecA shape and equipped-view
    /// publisher, so the profile entries and the S2C 806 record both carry what
    /// the deployment configured. The install target stays <see langword="null"/>,
    /// so the burst reaches every seat: the single-seat install is the join path's
    /// shape, not any of these.
    ///
    /// </summary>
    /// <param name="room">The room to push.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task PushRoomAsync(PlayRoom room, CancellationToken ct) =>
        RoomPush.PushRoomAsync(
            room, null, null, _equippedView, _options.SendSeatRecords, _options.SendPartyUpdate, _options.SendWaitUserList, _options.EnterGameStageOnCreate, _options.ModelItemIds, _options.RoomState, _options.RoomInstallState, _options.PrimeSessionStateBeforeInstall, null, _logger, ct, _partySender);
}
