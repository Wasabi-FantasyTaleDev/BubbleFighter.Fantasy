using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
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
/// The round start: the S2C 510 stage enter, the relay endpoint and its confirmation, each
/// client's load reports, the countdown and the S2C 608 round initialization.
///
/// </summary>
internal sealed class MatchStartFlow
{
    private readonly InGameOptions _options;
    private readonly RoomRegistry _registry;
    private readonly ILogger _logger;
    private readonly MatchResultFlow _result;
    private readonly CancellationToken _lifetime;
    private readonly IPAddress _relayAddress;
    private readonly IMessageSender<MatchSessionSignature>? _signatureSender;
    private readonly MapCatalog? _maps;

    /// <summary>Builds the round start for one activated row.</summary>
    /// <param name="options">The row configuration.</param>
    /// <param name="registry">The room registry.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="result">The round end the armed round clock hands over to.</param>
    /// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
    /// <param name="signatureSender">Main-connection adapter that initializes the relay registration prerequisite.</param>
    /// <param name="maps">The client's map pools a round's map is drawn from; without them a random selection draws from the shipped keys.</param>
    public MatchStartFlow(InGameOptions options, RoomRegistry registry, ILogger logger, MatchResultFlow result,
        CancellationToken lifetime = default, IMessageSender<MatchSessionSignature>? signatureSender = null, MapCatalog? maps = null)
    {
        _options = options;
        _registry = registry;
        _logger = logger;
        _result = result;
        _lifetime = lifetime;
        _relayAddress = IPAddress.Parse(options.RelayAdvertiseIpv4);
        _signatureSender = signatureSender;
        _maps = maps;
    }

    /// <summary>Publishes final teams before the scene move, then waits for each client's load and relay confirmations.</summary>
    public async ValueTask StartMatchAsync(PlayRoom room, CancellationToken ct, long? requesterId = null, bool force = false) =>
        await room.Executor.ExecuteAsync(
            operationCt => StartMatchCoreAsync(room, operationCt, requesterId, force), ct).ConfigureAwait(false);

    // Runs on the room's executor so the start roster and S2C 510 fan-out cannot interleave with a join or leave.
    private async ValueTask StartMatchCoreAsync(PlayRoom room, CancellationToken ct, long? requesterId, bool force)
    {
        if (!_options.StartMatches) throw new InvalidOperationException("Match starting is disabled by this deployment.");
        if (_signatureSender is null) throw new InvalidOperationException("Match starting requires a session signature sender.");
        RoomSeat[] seats;
        ushort gameMode;
        string? roundMap;
        var isMission = false;
        lock (room.Gate)
        {
            if (room.Seats.Count == 0) throw new InvalidOperationException("An empty room cannot start.");
            if (requesterId is { } id && (room.FindByGame(id)?.Slot != room.MasterSlot
                || (!room.CanStart && !force) || room.Phase != RoomPhase.Waiting))
            {
                _logger.LogWarning("Room {Room} changed before session {Id}'s start could be accepted.", room.Id, id);
                return;
            }
            gameMode = room.Setting.Map.Unknown4;
            roundMap = ResolveRoundMap(gameMode, room.Setting.Map);
            if (roundMap is null)
            {
                _logger.LogWarning("Room {Room} cannot start: no map fits mode {Mode} and selection '{Selection}'.",
                    room.Id, gameMode, room.Setting.Map.Unknown68);
                return;
            }
            room.AssignSpawnTeams();
            room.MarkStarting();
            room.StartPending = true;
            room.RoundMapKey = roundMap;
            room.RoundStageKey = _maps?.StageKey(roundMap) ?? roundMap;
            isMission = _maps?.IsDifficultyMode(gameMode) == true;
            seats = room.Seats.ToArray();
        }
        _logger.LogInformation("Room {Room} starting generation={Generation} mode={Mode} selection={Selection} map={Map} stage={Stage} roster={Roster}.",
            room.Id, room.StartGeneration, gameMode, room.Setting.Map.Unknown68, roundMap, room.RoundStageKey,
            string.Join(",", seats.Select(seat => $"{seat.Slot}:{seat.Team}")));
        await SendStartRosterAsync(room, seats, ct).ConfigureAwait(false);
        // A pirate round names the mission in the first and third slots and its stage in the second:
        // the handler sub_9E5E70 tests the first slot's MapMeta isHordeModeMap/isPirateAssault flag
        // to enter the NPC stage, and the host loads resources from the second (session+232).
        // The fourth field carries the mode.
        var stage = GameToClient.EnterGameStage(isMission
            ? EnterGameStageRecord.ForMission(roundMap, room.RoundStageKey, gameMode)
            : EnterGameStageRecord.ForMap(roundMap, 3));
        foreach (var seat in seats)
        {
            // 0x96029D requires config+0x6E4 before relay registration.
            await _signatureSender.SendAsync(seat.Game, new MatchSessionSignature(gameMode), ct).ConfigureAwait(false);
            await seat.Game.SendAsync(InGameOpcodes.S2CEnterGameStage, stage, ct).ConfigureAwait(false);
            ScenePhase.Entered(seat.Game, SessionPhase.Match, _logger);
        }
    }

    // The map a round is played on: drawn from the client's pools when the client data is loaded,
    // otherwise the stored concrete selection, or for a random marker one of the shipped keys.
    private string? ResolveRoundMap(int mode, MapMetaBlob selection)
    {
        if (_maps is not null)
            return _maps.Resolve(mode, selection, Random.Shared);
        return MapCatalog.ConcreteSelection(selection)
            ?? (MapCatalog.HasSelection(selection) ? KnownMapKeys.All[Random.Shared.Next(KnownMapKeys.All.Count)] : null);
    }

    /// <summary>Publishes the final roster before stage creation so 0xC80220 can select team spawn positions.</summary>
    private async Task SendStartRosterAsync(
        PlayRoom room, IReadOnlyList<RoomSeat> seats, CancellationToken ct)
    {
        List<ProfileMapEntry> entries;
        lock (room.Gate)
            entries = RoomPush.ProfileEntries(room, _options.ModelItemIds);
        foreach (var row in seats)
        {
            var body = GameToClient.CreateRoomReply(
                row.Slot,
                row.ToPlayerInfo(),
                entries);
            foreach (var target in seats)
                await target.Game.SendAsync(InGameOpcodes.S2CCreateRoomReply, body, ct)
                    .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Room {Room} published the pre-stage S2C 503 roster for {Seats} seat(s).",
            room.Id, seats.Count);
    }

    public async ValueTask HandleRelayEndpointAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        RelayEndpointRequest.Parse(packet.Payload.Span);
        var (room, seat) = _registry.RequireGame(session);
        lock (room.Gate)
        {
            if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                throw new InvalidOperationException("Match endpoint requested outside a match.");
            // A duplicate 571 clears the client's relay pointer (0x9A7250).
            if (seat.EndpointAdvertised) return;
            seat.EndpointAdvertised = true;
        }
        try
        {
            await session.SendAsync(InGameOpcodes.S2CConnectRelay,
                GameToClient.ConnectRelay(_relayAddress, (ushort)_options.RelayAdvertisePort, seat.Key), ct).ConfigureAwait(false);
        }
        catch
        {
            lock (room.Gate) seat.EndpointAdvertised = false;
            throw;
        }
    }

    public async ValueTask HandleRelayConnectResultAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var result = Int32Payload.Parse(packet.Payload.Span, "C2S 571").Value;
        if (result is not (0 or 1)) throw new InvalidOperationException("C2S 571 result must be a boolean.");
        // 0x960050 reports a closed relay without a generation. A replaced transport can report late.
        // Only the authenticated transport lifecycle may revoke its own binding.
        if (result == 0)
        {
            _logger.LogInformation("Session {Id} reported a closed match transport; authenticated binding state is unchanged.", session.Id);
            return;
        }
        var (room, seat) = _registry.RequireGame(session);
        lock (room.Gate)
        {
            if (room.Phase is RoomPhase.Waiting or RoomPhase.Result)
            {
                _logger.LogWarning("Session {Id} reported late match initialization in {Phase}.", session.Id, room.Phase);
                return;
            }
            // 0x960880 emits true only after relay S2C 13; the current transport must independently be ready.
            if (room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                throw new InvalidOperationException("Match connection confirmation arrived outside an active match.");
            if (!seat.RelayInitialized || seat.Relay is null || seat.Relay.Connection.IsClosed)
                throw new InvalidOperationException("Match connection was not successfully initialized.");
            seat.RelayConfirmed = true;
        }
        await FlushPendingStartAsync(room, ct).ConfigureAwait(false);
    }

    public ValueTask HandleRelayAckAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 577");
        _logger.LogInformation("Session {Id} C2S 577 relay ack", session.Id);
        return ValueTask.CompletedTask;
    }

    /// <summary>Records the load report; scene identity and authenticated relay success release the countdown separately.</summary>
    public ValueTask HandleStageMoveConsumedAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _registry.RequireGame(session);
        _logger.LogDebug("Session {Id} C2S 579 loading report, {Length} bytes.", session.Id, packet.Payload.Length);
        return ValueTask.CompletedTask;
    }

    public async ValueTask HandleSceneObjectReadyAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var entity = Int32Payload.Parse(packet.Payload.Span, "C2S 607").Value;
        var (room, seat) = _registry.RequireGame(session);
        if (entity != 1000000 + 100000 * seat.Slot) throw new InvalidOperationException("Scene entity does not belong to this seat.");
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Starting || seat.SceneReady) return;
            seat.SceneReady = true;
        }
        // Acknowledge loading without starting the countdown.
        await session.SendAsync(InGameOpcodes.S2CStartTimer, GameToClient.StartTimer(0), ct).ConfigureAwait(false);
        await FlushPendingStartAsync(room, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// C2S 941, the one-shot resolved-map-key report: the body is an i32 wchar count plus the
    /// UTF-16LE key, seeded from the S2C 510/117 map strings (session+232). Logged decoded so a mismatch with
    /// our seeded key flags an unresolvable scene key; no reply exists in any dispatcher surface.
    /// </summary>
    /// <param name="session">The sending session.</param> <param name="packet">The inbound frame.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask HandleMapKeyReportAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var r = new PacketReader(packet.Payload.Span);
        var chars = r.ReadInt32();
        var key = r.ReadWideStringPrefixed();
        _logger.LogInformation(
            "Session {Id} C2S 941 map-key report: '{Key}' ({Chars} chars).",
            session.Id, key, chars);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// C2S 17 round-ready confirmation. The client writes it after the start
    /// chain's S2C 608 (sub_9E7570); an earlier acknowledgement is rejected.
    ///
    /// </summary>
    public ValueTask HandleGameStartReadyAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        EmptyPayload.Parse(packet.Payload.Span, "C2S 17");

        // The client sends this on the RELAY connection, so the session that
        // carries it is the relay session, not the game one. Resolve the room
        // through whichever seat this session actually is; a game session still
        // works because FindByGame is tried first.
        var room = _registry.TryGet(session.Id);
        if (room is null)
        {
            _logger.LogDebug(
                "Session {Id} C2S 17 round-ready with no bound room; ignored.", session.Id);
            return ValueTask.CompletedTask;
        }

        RoomSeat? seat;
        lock (room.Gate)
        {
            seat = room.Seats.FirstOrDefault(s =>
                s.Game.Id == session.Id || (s.Relay is not null && s.Relay.Id == session.Id));
            if (seat is null) return ValueTask.CompletedTask;
            if (!seat.RoundInitialized || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
                throw new InvalidOperationException("Round-ready arrived before round initialization.");
            seat.RoundReady = true;
        }

        _logger.LogInformation("Session {Id} C2S 17 round-ready room={Room} slot={Slot}", session.Id, room.Id, seat.Slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Arms one countdown only after every seated client has loaded and authenticated its match transport.</summary>
    internal async ValueTask FlushPendingStartAsync(PlayRoom room, CancellationToken ct)
    {
        try
        {
            await room.Executor.ExecuteAsync(operationCt => FlushPendingStartCoreAsync(room, operationCt), ct)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The room closed after the report arrived; there is no start left to release.
        }
    }

    // Runs on the room's executor so the 585/607 fan-out and the countdown arm see one roster.
    private async ValueTask FlushPendingStartCoreAsync(PlayRoom room, CancellationToken ct)
    {
        RoomSeat[] seats;
        int generation;
        lock (room.Gate)
        {
            if (!room.StartPending || room.Phase != RoomPhase.Starting || room.Seats.Count == 0) return;
            if (room.Seats.Any(seat => !seat.SceneReady || !seat.RelayConfirmed || !seat.RelayInitialized
                || seat.Relay is null || seat.Relay.Connection.IsClosed)) return;
            room.ClearStartPending();
            seats = room.Seats.ToArray();
            generation = room.StartGeneration;
        }
        foreach (var seat in seats)
        {
            await seat.Game.SendAsync(InGameOpcodes.S2CStartFlags,
                GameToClient.StartFlags(_options.StartFlag0, _options.StartFlag1, _options.StartFlag2), ct).ConfigureAwait(false);
            await seat.Game.SendAsync(InGameOpcodes.S2CStartTimer, GameToClient.StartTimer(_options.StartTimerMs), ct).ConfigureAwait(false);
        }
        // The delay runs off the executor; only its expiry is queued as an operation.
        room.CountdownTask = BackgroundWork.Detached(() => FinishCountdownAsync(room, generation));
    }

    private async Task FinishCountdownAsync(PlayRoom room, int generation)
    {
        RoomSeat[] broken = [];
        try
        {
            await Task.Delay(_options.StartTimerMs, _lifetime).ConfigureAwait(false);
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] seats;
                lock (room.Gate)
                {
                    // A restart or a closed room makes this expiry stale.
                    if (room.StartGeneration != generation || room.Phase != RoomPhase.Starting || room.Seats.Count == 0)
                        return;
                    // A player who left during the countdown is already out of the roster, and a member whose
                    // transport broke cannot play; the round goes on for the others.
                    broken = room.Seats.Where(seat => !seat.RelayConfirmed || !seat.RelayInitialized
                        || seat.Game.Connection.IsClosed || seat.Relay is null || seat.Relay.Connection.IsClosed).ToArray();
                    seats = room.Seats.Except(broken).ToArray();
                    if (seats.Length == 0)
                        return;
                }
                var roundMs = RoundLengthMs(room);
                foreach (var seat in seats)
                {
                    lock (room.Gate) seat.RoundInitialized = true;
                    await seat.Game.SendAsync(InGameOpcodes.S2CRoundInitialize,
                        GameToClient.RoundInitialize(roundMs, _options.RoundUnknown1), operationCt).ConfigureAwait(false);
                    if (roundMs == NoTimeLimitMs)
                        await seat.Game.SendAsync(InGameOpcodes.S2CRoundClockUnbounded,
                            GameToClient.RoundClockUnbounded(), operationCt).ConfigureAwait(false);
                }
                lock (room.Gate) room.MarkPlaying();
                _result.ArmRoundClock(room, roundMs);
            }, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {Room} could not finish its match countdown.", room.Id);
        }
        // Closed outside the operation: the close raises the departure, which queues its own operation.
        foreach (var seat in broken)
        {
            _logger.LogWarning("Room {Room} slot {Slot} lost its match transport during the countdown; closing it.",
                room.Id, seat.Slot);
            try { await seat.Game.Connection.CloseAsync("match transport lost during countdown").ConfigureAwait(false); }
            catch (Exception closeError) { _logger.LogError(closeError, "Closing failed match seat {Slot} failed.", seat.Slot); }
        }
    }

    /// <summary>
    /// Resolves the round length the S2C 608 body carries (<c>sub_6FEFC0</c> stores it as the
    /// round duration, <c>sub_6FEFC0</c>). The rule record the client sends with its start
    /// click (C2S 503, built by <c>sub_AADD50</c> from the map's limits) holds the per-mode values:
    /// time-attack modes (the zombie and pirate families) play <c>TimeAttackTime</c>, the others
    /// <c>DeathMatchTime</c> (mode 1 plays 240000 with TimeAttack 241000, mode 43 plays
    /// 180000 and mode 32 plays 600000, their TimeAttack values). The training ground has no limit: <c>0x3FFFFFFF</c>, the
    /// value <c>sub_6FEFC0</c> itself writes as a timer ceiling (<c>kr-1256 sub_6FEFC0</c>).
    ///
    /// </summary>
    /// <param name="room">The room being started.</param>
    /// <returns>The round length in milliseconds.</returns>
    private int RoundLengthMs(PlayRoom room)
    {
        if (room.ModeId == RoomRegistry.TrainingModeId)
            return NoTimeLimitMs;

        var rules = room.RoundRules ?? room.CreateRules;
        if (rules is null)
            return _options.RoundUnknown0;
        var mode = GameMode.Resolve(room.ModeId);
        var timeAttack = mode.Family is GameModeFamily.Zombie or GameModeFamily.Horde;
        var length = timeAttack ? rules.TimeAttackTimeMs : rules.DeathMatchTimeMs;
        return length > 0 ? length : _options.RoundUnknown0;
    }

    /// <summary>
    /// The no-time-limit value <c>sub_6FEFC0</c> itself writes as the second
    /// timer's ceiling (<c>sub_6FEFC0</c>), reused here as the round length
    /// so a round with no limit does not end. Protocol constant, not a tunable.
    ///
    /// </summary>
    internal const int NoTimeLimitMs = 0x3FFFFFFF;
}
