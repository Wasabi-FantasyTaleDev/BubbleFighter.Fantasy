using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchRelay;

/// <summary>
/// In-round traffic between the players: movement, relay motion, damager spawns, weapon-preset
/// swaps and the reports the client sends without expecting an answer. The rooms row owns the
/// rooms; this row reads their roster and keeps each round's movement state itself.
///
/// </summary>
internal sealed class MatchRelayFlow
{
    private readonly MatchRelayOptions _options;
    private readonly IRoomRoster _roster;
    private readonly RoundStates _rounds;
    private readonly ILogger _logger;
    private readonly IPlayerInventoryState? _playerState;

    /// <summary>Builds the in-round relay for one activated row.</summary>
    /// <param name="options">The row configuration.</param>
    /// <param name="roster">The rooms row's roster.</param>
    /// <param name="rounds">The per-round seat states.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="playerState">
    /// Optional per-player state store the granted weapon-preset slot is persisted through;
    /// without it the swap reply is unchanged and nothing is persisted.
    /// </param>
    public MatchRelayFlow(MatchRelayOptions options, IRoomRoster roster, RoundStates rounds, ILogger logger,
        IPlayerInventoryState? playerState = null)
    {
        _options = options;
        _roster = roster;
        _rounds = rounds;
        _logger = logger;
        _playerState = playerState;
    }

    /// <summary>Forwards full movement with all three coordinates and its sample clock to other match transports.</summary>
    public async ValueTask HandleMovementReportAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var report = MovementReport.Parse(packet.Payload.Span);
        if (!_options.BroadcastMovementSamples && !_options.EchoMovementReports) return;
        if (_roster.FindBySession(session.Id) is not { } view) return;
        var sender = RequireMovementSeat(view, session, report.ObjectId);
        var round = _rounds.For(view);
        PlayerMovementState? merged;
        lock (round.Gate)
        {
            var state = round.Seat(sender.Slot);
            if (state.Movement is null)
                _logger.LogInformation("Room {Room} generation={Generation} mode={Mode} slot={Slot} team={Team} first movement entity={Entity} xyz=({X},{Y},{Z}) tick={Tick}.",
                    view.RoomId, view.Generation, view.ModeId, sender.Slot, sender.Team,
                    report.ObjectId, report.X, report.Y, report.Z, report.Tick);
            merged = MergeMovement(state, PlayerMovementState.FullMask,
                report.X, report.Y, report.Z, report.Yaw, report.Pitch, report.State);
        }
        if (_options.EchoMovementReports)
            await session.SendAsync(InGameOpcodes.S2CMovementReport, packet.Payload, ct).ConfigureAwait(false);
        else if (merged is not null)
            await BroadcastMovementFrameAsync(view, sender, report.ObjectId, report.Tick, merged, ct).ConfigureAwait(false);
    }

    /// <summary>Merges partial motion into the sending seat's round state and sends a full record to other initialized match transports.</summary>
    public async ValueTask HandleMovementSampleAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (!_options.BroadcastMovementSamples)
            return;
        var sample = MovementSample.Parse(packet.Payload.Span);
        if (_roster.FindBySession(session.Id) is not { } view)
        {
            _logger.LogDebug(
                "Session {Id} C2S 32923 movement sample actor={Actor} with no bound room; ignoring.",
                session.Id, sample.Actor);
            return;
        }

        var sender = RequireMovementSeat(view, session, sample.Actor);
        var round = _rounds.For(view);
        PlayerMovementState? merged;
        lock (round.Gate)
            merged = MergeMovement(round.Seat(sender.Slot), sample.Mask, sample.X, sample.Y, sample.Z,
                sample.Yaw, sample.Pitch, sample.State);
        if (merged is null)
        {
            _logger.LogWarning("Session {Id} sent partial movement before a full record; no coordinates were fabricated.", session.Id);
            return;
        }

        await BroadcastMovementFrameAsync(view, sender, sample.Actor, sample.Tick, merged, ct)
            .ConfigureAwait(false);
    }

    private static RoomSeatView RequireMovementSeat(RoomRosterView view, ISession session, int entity)
    {
        var seat = view.Seats.SingleOrDefault(s => ReferenceEquals(s.Game, session) || ReferenceEquals(s.Relay, session))
            ?? throw new InvalidOperationException("Movement transport has no current seat.");
        if (entity != 1000000 + 100000 * seat.Slot)
            throw new InvalidOperationException("Movement entity does not belong to the sending seat.");
        return seat;
    }

    private static PlayerMovementState? MergeMovement(SeatRoundState seat, byte mask,
        float x, float y, float z, byte yaw, byte pitch, byte state)
    {
        if ((mask & ~PlayerMovementState.FullMask) != 0)
            throw new InvalidOperationException("Unsupported movement component mask.");
        var previous = seat.Movement;
        if (previous is null && mask != PlayerMovementState.FullMask) return null;
        var merged = new PlayerMovementState(
            (mask & 1) != 0 ? x : previous!.X, (mask & 2) != 0 ? y : previous!.Y,
            (mask & 4) != 0 ? z : previous!.Z, (mask & 8) != 0 ? yaw : previous!.Yaw,
            (mask & 16) != 0 ? pitch : previous!.Pitch, (mask & 32) != 0 ? state : previous!.State);
        if (!float.IsFinite(merged.X) || !float.IsFinite(merged.Y) || !float.IsFinite(merged.Z))
            throw new InvalidOperationException("Movement coordinates must be finite.");
        return seat.Movement = merged;
    }

    /// <summary>
    /// Builds the full 21-byte S2C 32885 frame from one actor's merged movement state - a
    /// byte-for-byte transpose, no float re-encoding - and sends it to every other initialized match transport.
    /// The sender's own seat is excluded: the client drops any frame whose entity id equals its own character
    /// before applying anything, and the server never sends one. Shared by the C2S 32923 sample path and the
    /// C2S 32885 full-report path. </summary> <param name="view">The room whose seats receive the
    /// frame.</param> <param name="sender">The seat the movement came from.</param> <param name="actorId">The
    /// sender's client-computed actor id.</param> <param name="tick">The tick stamped into the frame.</param>
    /// <param name="merged">The merged state the frame transposes.</param> <param name="ct">Cancellation
    /// token.</param>
    private async ValueTask BroadcastMovementFrameAsync(
        RoomRosterView view,
        RoomSeatView sender,
        int actorId,
        ushort tick,
        PlayerMovementState merged,
        CancellationToken ct)
    {
        var frame = GameToClient.MovementStateFrame(
            actorId,
            merged.X,
            merged.Y,
            merged.Z,
            merged.Yaw,
            merged.Pitch,
            merged.State,
            tick);
        var peers = view.Seats
            .Where(s => s.Slot != sender.Slot && s.RelayInitialized && s.Relay is not null && !s.Relay.Connection.IsClosed)
            .Select(s => s.Relay!).ToArray();
        foreach (var peer in peers)
            await peer.SendAsync(InGameOpcodes.S2CMovementReport, frame, ct).ConfigureAwait(false);
    }

    /// <summary>Forwards relay motion byte-identically to every other seat's match transport.</summary>
    public async ValueTask HandleRelayMotionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var motion = RelayMotion.Parse(packet.Payload.Span);
        // Motion is the highest-frequency frame on the relay, so an unbound session
        // here is not a rare fault - it is what a session sends before its
        // authentication lands, or after its room was replaced. Throwing would take
        // the connection down mid-round, the same failure the relay-endpoint,
        // relay-authentication and position-report handlers already learned.
        if (_roster.FindBySession(session.Id) is not { } view)
        {
            _logger.LogDebug(
                "Relay session {Id} motion with no room; ignoring.", session.Id);
            return;
        }
        var peers = view.Seats.Where(s => s.Relay is not null && s.Relay.Id != session.Id).Select(s => s.Relay!).ToArray();

        _logger.LogDebug(
            "Relay session {Id} opcode {Opcode} identity={Identity} remainder={Len} peers={Peers}",
            session.Id, packet.Opcode, motion.ObjectIdentity, motion.Remainder.Length, peers.Length);
        foreach (var peer in peers)
            await peer.SendAsync(packet.Opcode, packet.Payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays a damager-spawn frame byte-identically to the other seats. The
    /// eight spawn opcodes (32772, 32877, 32881, 32898, 32899, 32900, 32910,
    /// 33278) mint combat damager keys on receipt as per-slot counters
    /// (<c>100000*ownerSlot + n</c>), so a relayed 38283 hit only resolves on
    /// the receiving client if its counters minted the same keys - which
    /// requires the spawns themselves to be relayed in the same order. The bodies vary per
    /// opcode and are forwarded without parsing.
    ///
    /// </summary>
    /// <param name="session">The sending session.</param>
    /// <param name="packet">The inbound spawn frame.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleDamagerSpawnRelayAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        if (_roster.FindBySession(session.Id) is not { } view)
            return;
        var sender = view.Seats.FirstOrDefault(s =>
            s.Game.Id == session.Id || (s.Relay is not null && s.Relay.Id == session.Id));
        if (sender is null)
            return;

        foreach (var peer in view.Seats.Where(s => s.Slot != sender.Slot))
            await (peer.Relay ?? peer.Game)
                .SendAsync(packet.Opcode, packet.Payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the C2S 832 weapon-preset swap request with the S2C 836 swap-apply reply the client
    /// waits for. The client code proves the swap is server-confirmed, not client-authoritative: <c>sub_808F10</c>
    /// only sends the request and latches <c>dword_18E55D4+208</c> (<c>sub_808F10</c>, <c>:161</c>), and
    /// the dispatcher's S2C 836 case (<c>0x9D5035</c>) is what clears the latch, records the active preset
    /// index at profile +28 and swaps the in-session weapon model - only when the reply's status <c>i32</c> is
    /// zero (<c>sub_9D2FF0:9D5043-9D5110</c>). Without the reply every later hotkey press returns at the latch
    /// check and the character keeps the old weapon for the rest of the round. </summary> <remarks> <para> The
    /// reply goes only to the sender. The S2C 836 case resolves the receiving client's own identity
    /// (<c>*(dword_18E56E8+0x30)</c>) and its own in-session model (<c>sub_9E1580</c>/<c>sub_6D3390</c>), so
    /// echoing the frame to peers would switch their weapons instead; the client code recovers no S2C that carries a
    /// remote actor's weapon swap, so none is invented (the client's own post-swap report is C2S 25,
    /// <c>sub_6DA420</c>, whose peer surface is unrecovered). </para> <para> A granted slot is also
    /// persisted server-side behind the reply: the chain has no save opcode and the client's local +28 store is
    /// overwritten by the leading byte of the next login S2C 837, so the slot the next login echoes back is
    /// whatever this handler recorded. Every 832 is answered - in or out of round - with status 0, so
    /// the slot is player state granted at reply time, not room state. The persist is best-effort by design:
    /// the reply has already gone out, and a missing or failing store is logged, never allowed to break the
    /// swap. </para> <para> Registered on both the game and relay protocols like the movement pair: the
    /// in-session family rides the match connection, which in this deployment dials the game listener, and the
    /// reply is sent back on whichever session delivered the request. </para> </remarks> <param
    /// name="session">The sending session (game or match connection).</param> <param name="packet">The inbound
    /// five-byte swap request.</param> <param name="ct">Cancellation token.</param>
    public async ValueTask HandleWeaponSwapAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var swap = WeaponPresetSwap.Parse(packet.Payload.Span);
        var persisted = await PersistGrantedSlotAsync(session, swap.Slot, ct).ConfigureAwait(false);
        await session.SendAsync(InGameOpcodes.S2CWeaponPresetSwap,
            persisted ? WeaponPresetSwapReply.WriteSuccess(swap.Slot) : WeaponPresetSwapReply.WriteFailure(swap.Slot), ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Session {Id} C2S 832 weapon preset swap to slot {Slot} (unknown1 {Unknown1}); S2C 836 status {Status} sent.",
            session.Id, swap.Slot, swap.Unknown1, persisted ? 0 : 1);
    }

    /// <summary>
    /// Persists one granted swap slot before the reply. A session without a lobby identity, a row without a
    /// published <see cref="IPlayerInventoryState"/>, or a store failure is logged and refuses the swap. </summary>
    /// <param name="session">The session the swap request arrived on.</param>
    /// <param name="slot">The requested slot.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask<bool> PersistGrantedSlotAsync(ISession session, byte slot, CancellationToken ct)
    {
        if (_playerState is null)
        {
            _logger.LogDebug(
                "Session {Id} granted weapon preset slot {Slot} not persisted: no persistence row publishes IPlayerInventoryState.",
                session.Id, slot);
            return false;
        }

        var userNum = session.TryFeature<IPlayerSession>(out var player) ? player.Identity.UserNum : 0;
        if (userNum <= 0)
        {
            _logger.LogDebug(
                "Session {Id} granted weapon preset slot {Slot} not persisted: the session has no lobby identity.",
                session.Id, slot);
            return false;
        }

        try
        {
            await _playerState.SetActiveWeaponPresetSlotAsync(userNum, slot, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Player {User} active weapon preset slot persisted as {Slot}.", userNum, slot);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Session {Id} player {User} weapon preset slot {Slot} could not be persisted; refusing the S2C 836 grant.",
                session.Id, userNum, slot);
            return false;
        }
    }

    /// <summary>
    /// C2S 25, the weapon-preset post-swap report: {i32 actor id, u8 slot}, built inline by
    /// <c>sub_6DA420</c> after a status-zero S2C 836 apply. Logged, never answered - no dispatcher case in
    /// any surface consumes a reply and the builder sets no wait-latch. </summary> <param name="session">The
    /// sending session.</param> <param name="packet">The inbound frame.</param> <param name="ct">Cancellation
    /// token.</param>
    public ValueTask HandlePostSwapReportAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var report = PostSwapReport.Parse(packet.Payload.Span);
        _logger.LogInformation(
            "Session {Id} C2S 25 post-swap report actor={Actor} slot={Slot} (logged, no reply).",
            session.Id, report.ActorId, report.Slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs the C2S 608 gameplay action; no physics is applied.</summary>
    public ValueTask HandleGameplayActionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var action = GameplayAction.Parse(packet.Payload.Span);
        _logger.LogInformation(
            "Session {Id} C2S 608 action type={Type} u1={U1} u2={U2} (no physics applied)",
            session.Id, action.Type, action.Unknown1, action.Unknown2);
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs the C2S 2029 alternate action; no physics is applied.</summary>
    public ValueTask HandleAlternateActionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var action = AlternateAction.Parse(packet.Payload.Span);
        _logger.LogInformation(
            "Session {Id} C2S 2029 alternate action {A},{B},{C} (no physics applied)",
            session.Id, action.Unknown0, action.Unknown1, action.Unknown2);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts the C2S 518 latency sample. The seat index is pinned and logged;
    /// the averaged latency is a client-local clock difference, so nothing is
    /// derived and nothing is replied.
    ///
    /// </summary>
    /// <param name="session">The sending session.</param>
    /// <param name="packet">The inbound packet.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask HandlePingSampleAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var sample = PingSample.Parse(packet.Payload.Span);
        _logger.LogDebug(
            "Session {Id} C2S 518 ping sample seat={Seat} stamp={Stamp} (no reply, latency is client-local)",
            session.Id, sample.Seat, sample.Stamp);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts the C2S 624 death report and logs it with room context. Nothing else happens, because
    /// the client code proves no others-facing death surface: the S2C census has no 624 row (no dispatcher case in
    /// <c>sub_9E1EB0</c>/<c>sub_9E2FE0</c>), so a byte-identical relay under the same opcode - the 38283
    /// pattern - would hand the peers a frame their dispatcher cannot consume. Every candidate surface (other
    /// 38278 event ids, the 38284 render-flag family, the S2C 925 kill delta) unproven for a victim-death
    /// visual. A death also does not end the round - only the round clock does - so the room phase is
    /// untouched; the rooms row's handler of the paired C2S 625 stays the death family's only result trigger.
    /// </summary> <remarks> The body is the verified 8-byte pair {i32, i32} (<c>sub_708B60</c>,
    /// sub_708B60): the death path sends {1, 0} back-to-back with C2S 625 from <c>sub_B38850</c>, the timer
    /// paths send {0, 0}. </remarks> <param name="session">The sending session.</param> <param
    /// name="packet">The inbound eight-byte report.</param> <param name="ct">Cancellation token.</param>
    public ValueTask HandleDeathReportAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var report = DeathReport.Parse(packet.Payload.Span);
        if (_roster.FindBySession(session.Id) is not { } view)
        {
            _logger.LogDebug(
                "Session {Id} C2S 624 death report {A},{B} with no room; ignoring.",
                session.Id, report.Unknown0, report.Unknown1);
            return ValueTask.CompletedTask;
        }

        var seat = view.Seats.FirstOrDefault(s => s.Game.Id == session.Id);
        _logger.LogInformation(
            "Session {Id} C2S 624 death report {A},{B} room={Room} seatSlot={Slot}; no others-facing death surface is proven (no S2C 624 dispatcher case), so nothing is relayed and the round phase is untouched.",
            session.Id, report.Unknown0, report.Unknown1, view.RoomId, seat?.Slot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts an in-session frame whose body has no derived layout, logging the
    /// length and nothing else.
    ///
    /// </summary>
    /// <remarks>
    /// For opcodes the client code recovers a handler for but no body for - C2S 32923
    /// <c>Unknown32923</c> is the case this exists for. The opcode policy requires
    /// unlabeled opcodes to be logged rather than parsed optimistically, and without
    /// a registration every frame lands in the unhandled-opcode warning log.
    /// </remarks>
    /// <param name="session">The session the frame arrived on.</param>
    /// <param name="packet">The inbound packet.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask HandleUnknownInSessionAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        _logger.LogDebug(
            "Session {Id} C2S {Opcode} in-session frame {Length} byte(s); no derived layout, nothing parsed.",
            session.Id, packet.Opcode, packet.Payload.Length);
        return ValueTask.CompletedTask;
    }
}
