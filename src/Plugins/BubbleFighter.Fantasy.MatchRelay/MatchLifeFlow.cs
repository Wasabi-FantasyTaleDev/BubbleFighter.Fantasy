using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Dialects;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.MatchRelay;

/// <summary>Tracks reported lives and confirms native resets for the ordinary timed-score mode.</summary>
/// <param name="roster">The rooms row's roster.</param>
/// <param name="rounds">The per-round seat states.</param>
/// <param name="logger">The row logger.</param>
/// <param name="lifetime">Cancellation when the owning plugin unloads.</param>
internal sealed class MatchLifeFlow(IRoomRoster roster, RoundStates rounds, ILogger logger, CancellationToken lifetime)
{
    /// <summary>
    /// Applies one relayed action to the round's lives and returns the team that scored, or 0.
    /// Called after transport and reporting-actor validation.
    /// </summary>
    /// <param name="view">The room the action was sent in.</param>
    /// <param name="sender">The sending seat.</param>
    /// <param name="packet">The action frame.</param>
    internal byte Observe(RoomRosterView view, RoomSeatView sender, InboundPacket packet)
    {
        if (view.ModeId != (int)GameModeId.Pvp) return 0;
        var body = packet.Payload.Span;
        var round = rounds.For(view);
        lock (round.Gate)
        {
            var self = round.Seat(sender.Slot);
            switch (packet.Opcode)
            {
                case 38282:
                    if (self.Life == CharacterLife.Alive)
                        self.Life = BinaryPrimitives.ReadInt32LittleEndian(body[24..]) == 0
                            ? CharacterLife.Bubble : CharacterLife.Dead;
                    break;
                case 38281:
                    if (self.Life == CharacterLife.Alive) self.Life = CharacterLife.Bubble;
                    break;
                case 38272:
                case 38273:
                    if (Find(view, BinaryPrimitives.ReadInt32LittleEndian(body)) is { } releasedSeat
                        && round.Seat(releasedSeat.Slot) is { Life: CharacterLife.Bubble } released)
                        released.Life = CharacterLife.Alive;
                    break;
                case 38274:
                    if (self.Life == CharacterLife.Bubble) self.Life = CharacterLife.Dead;
                    break;
                case 38277:
                    // 0x6FAC30 applies the victim's death only when the first bool32 succeeds.
                    if (BinaryPrimitives.ReadInt32LittleEndian(body) == 0) return 0;
                    var victim = Find(view, BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
                    var initiator = Find(view, BinaryPrimitives.ReadInt32LittleEndian(body[8..]));
                    if (victim is null || initiator is null || initiator.Slot == victim.Slot
                        || round.Seat(victim.Slot).Life != CharacterLife.Bubble
                        || initiator.Team is not (1 or 2) || victim.Team is not (1 or 2)
                        || initiator.Team == victim.Team) return 0;
                    var victimState = round.Seat(victim.Slot);
                    victimState.Life = CharacterLife.Dead;
                    victimState.DeathCleanupReceived = false;
                    return initiator.Team;
                case 32836:
                    if (self.Life == CharacterLife.Dead) self.DeathCleanupReceived = true;
                    break;
                case 32926:
                    // A reset granted on an earlier match connection died with it.
                    if (self.Life == CharacterLife.ResetPending && ReferenceEquals(self.ResetRelay, sender.Relay))
                    {
                        self.Life = CharacterLife.Alive;
                        self.DeathCleanupReceived = false;
                        self.ResetRelay = null;
                        self.Movement = null;
                    }
                    break;
            }
        }
        return 0;
    }

    /// <summary>
    /// C2S 602, the reset request the client sends after its native death timer. S2C 632 queues the
    /// local 32926 reset (0x6F5E50 -> 0x706770); the main dispatcher accepts it at 0x9D3058.
    /// </summary>
    /// <param name="session">The game session.</param>
    /// <param name="packet">The reset request.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask HandleResetAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        var entity = MatchLife1256.ReadReset(packet.Payload.Span);
        if (roster.FindBySession(session.Id) is not { } view) return;
        var seat = view.Seats.FirstOrDefault(s => ReferenceEquals(s.Game, session));
        if (seat is null || view.Stage != RoomStage.Playing || session.Connection.IsClosed
            || seat.Relay is null || seat.Relay.Connection.IsClosed || !seat.RelayInitialized) return;
        if (entity != checked(1000000 + 100000 * seat.Slot))
        {
            if (Find(view, entity) is not null)
                throw new PacketFormatException("C2S 602 cannot reset another character.", 0);
            logger.LogWarning("Session {Session} reset rejected: entity {Entity} has no registered player ownership.", session.Id, entity);
            return;
        }
        if (view.ModeId != (int)GameModeId.Pvp)
        {
            logger.LogWarning("Room {Room} mode {Mode} has no verified native reset policy.", view.RoomId, view.ModeId);
            return;
        }
        var round = rounds.For(view);
        SeatRoundState state;
        ValueTask send;
        lock (round.Gate)
        {
            state = round.Seat(seat.Slot);
            if (state.Life == CharacterLife.ResetPending && !ReferenceEquals(state.ResetRelay, seat.Relay))
                state.Life = CharacterLife.Dead;
            if (state.Life != CharacterLife.Dead || !state.DeathCleanupReceived)
            {
                logger.LogWarning("Session {Session} reset rejected in character phase {Phase}.", session.Id, state.Life);
                return;
            }
            state.Life = CharacterLife.ResetPending;
            state.ResetRelay = seat.Relay;
            try
            {
                send = session.SendAsync(MatchLife1256.ResetReply, MatchLife1256.ResetReplyForTimedScore(packet.Payload.Span), ct);
            }
            catch
            {
                state.Life = CharacterLife.Dead;
                throw;
            }
        }
        try { await send.ConfigureAwait(false); }
        catch
        {
            lock (round.Gate)
            {
                if (state.Life == CharacterLife.ResetPending && ReferenceEquals(state.ResetRelay, seat.Relay))
                    state.Life = CharacterLife.Dead;
            }
            throw;
        }
        logger.LogInformation("Session {Session} native character reset confirmed in room {Room}.", session.Id, view.RoomId);
    }

    private static RoomSeatView? Find(RoomRosterView view, int entity) =>
        view.Seats.FirstOrDefault(s => entity == checked(1000000 + 100000 * s.Slot));
}
