using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>
    /// Applies one life report to the seats of a native-life round (<see cref="InGameOptions.NativeLifeModeIds"/>).
    /// A report about the sender's own character must name it; a pop needs a bubbled enemy and scores one point for
    /// the popper's team, so a pop both clients report counts once.
    /// </summary>
    public async ValueTask HandleLifeReportedAsync(ISession session, RoundLifeReported report, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundScoreUpdated? update = null;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !UsesNativeLife(room))
                    return;
                var sender = room.Seats.FirstOrDefault(seat => seat.Game.Id == session.Id || seat.Relay?.Id == session.Id);
                if (sender is null)
                    return;
                var own = room.ControlsActor(sender, report.Actor);
                var target = LifeActor(room, report.Actor);
                var actor = own ? target! : new LifeParticipant(sender, null);
                LifeCheck? ignored = null;
                switch (report.Event)
                {
                    case RoundLifeEvent.Bubbled or RoundLifeEvent.Died or RoundLifeEvent.BubbleExpired
                        or RoundLifeEvent.DeathCleaned or RoundLifeEvent.ResetConfirmed when !own:
                        ignored = LifeCheck.OwnEventForeignActor;
                        break;
                    case RoundLifeEvent.Bubbled when actor.Life == SeatLife.Alive:
                        actor.Life = SeatLife.Bubble;
                        break;
                    case RoundLifeEvent.Bubbled:
                        ignored = LifeCheck.BubbledWhileNotAlive;
                        break;
                    case RoundLifeEvent.Died when actor.Life == SeatLife.Alive:
                    case RoundLifeEvent.BubbleExpired when actor.Life == SeatLife.Bubble:
                        actor.Life = SeatLife.Dead;
                        actor.DeathCleaned = false;
                        break;
                    case RoundLifeEvent.Died:
                        ignored = LifeCheck.DiedWhileNotAlive;
                        break;
                    case RoundLifeEvent.BubbleExpired:
                        ignored = LifeCheck.ExpiredWhileNotBubbled;
                        break;
                    case RoundLifeEvent.Released when target is { Life: SeatLife.Bubble }:
                        target.Life = SeatLife.Alive;
                        break;
                    case RoundLifeEvent.Released:
                        ignored = LifeCheck.ReleasedNotBubbled;
                        break;
                    case RoundLifeEvent.DeathCleaned when actor.Life == SeatLife.Dead:
                        actor.DeathCleaned = true;
                        break;
                    case RoundLifeEvent.DeathCleaned:
                        ignored = LifeCheck.CleanedWhileNotDead;
                        break;
                    // The confirmation must come on the match link the reset was granted on.
                    case RoundLifeEvent.ResetConfirmed when actor.Life == SeatLife.ResetPending
                        && ReferenceEquals(actor.ResetRelay, session):
                        actor.Life = SeatLife.Alive;
                        actor.DeathCleaned = false;
                        actor.ResetRelay = null;
                        actor.ClearMovement();
                        break;
                    case RoundLifeEvent.ResetConfirmed:
                        ignored = actor.Life == SeatLife.ResetPending ? LifeCheck.ResetConfirmedOnOtherLink : LifeCheck.ResetConfirmedWithoutGrant;
                        break;
                    case RoundLifeEvent.Finished:
                        (update, ignored) = Finish(room, target, report.Initiator);
                        break;
                }
                if (ignored is { } check)
                    Observe(room, sender, report, check);
            }
            if (update is not null)
                await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // A pop kills a bubbled enemy: the victim dies and the popper scores. Called under the room gate. A pop that does
    // not fit returns why, for the observation counts.
    private (RoundScoreUpdated? Update, LifeCheck? Ignored) Finish(PlayRoom room, LifeParticipant? victim, int? initiatorActor)
    {
        var initiator = initiatorActor is { } actor ? LifeActor(room, actor) : null;
        LifeCheck? ignored = victim is null ? LifeCheck.PopWithoutVictim
            : initiator is null ? LifeCheck.PopWithoutInitiator
            : initiator.Slot == victim.Slot ? LifeCheck.PopOfSelf
            : victim.Life != SeatLife.Bubble ? LifeCheck.PopOfUnbubbled
            : initiator.Team is not (1 or 2) || victim.Team is not (1 or 2) || initiator.Team == victim.Team
                ? LifeCheck.PopWithoutOpposingTeams
            : null;
        if (ignored is not null)
            return (null, ignored);
        victim!.Life = SeatLife.Dead;
        victim.DeathCleaned = false;
        if (room.Scoring.AddKill(initiator!.Slot, initiator.Team, victim.Slot) is not { } stats)
            return (null, null);
        _logger.LogInformation("Room {Room} slot {Slot} popped slot {Victim}; team totals {Totals}.",
            room.Id, initiator.Slot, victim.Slot, string.Join(':', room.Scoring.TeamTotals));
        return (new RoundScoreUpdated(room.Id, initiator.Team, [.. room.Scoring.TeamTotals],
            [stats, room.Scoring.Stats(victim.Slot)], LiveScoreboard(room)), null);
    }

    // Counts an ignored life report; the round's first of each kind is logged at information level, the rest at
    // debug, and the round end logs the totals. Nothing is refused. Called under the room gate.
    private void Observe(PlayRoom room, RoomSeat sender, RoundLifeReported report, LifeCheck check)
    {
        var level = room.LifeChecks.Note(check) ? LogLevel.Information : LogLevel.Debug;
        _logger.Log(level, "Room {Room} slot {Slot}: life report {Event} for actor {Actor} ignored ({Check}); the seat is {Life}.",
            room.Id, sender.Slot, report.Event, report.Actor, check, sender.Life);
    }

    /// <summary>
    /// Grants a dead player's reset once its death presentation is over: the grant goes to the requester, and its
    /// relay's confirmation then brings the character back. A request naming another seat's character is refused
    /// loudly; one outside a native-life round, or too early, is dropped.
    /// </summary>
    public async ValueTask HandleResetRequestedAsync(ISession session, RoundResetRequested request,
        IMessageSender<RoundResetGranted> sender, CancellationToken ct)
    {
        if (_registry.TryGet(session.Id) is not { } room)
            return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            LifeParticipant seat;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } found)
                    return;
                if (!room.ControlsActor(found, request.Actor))
                {
                    if (LifeActor(room, request.Actor) is not null)
                        throw new InvalidOperationException($"Slot {found.Slot} asked to reset another seat's character.");
                    _logger.LogWarning("Session {Id} reset of actor {Actor}, which belongs to no seat; dropped.", session.Id, request.Actor);
                    return;
                }
                seat = LifeActor(room, request.Actor)!;
                if (!UsesNativeLife(room) || MatchLink(room, found) is not { } relay)
                    return;
                if (seat.Life == SeatLife.ResetPending && !ReferenceEquals(seat.ResetRelay, relay))
                    seat.Life = SeatLife.Dead;
                if (seat.Life != SeatLife.Dead || !seat.DeathCleaned)
                {
                    _logger.LogInformation("Session {Id} reset refused while its character is {Life}.", session.Id, seat.Life);
                    return;
                }
                seat.Life = SeatLife.ResetPending;
                seat.ResetRelay = relay;
            }
            try
            {
                await sender.SendAsync(session, new RoundResetGranted(request.Request), operationCt).ConfigureAwait(false);
            }
            catch
            {
                lock (room.Gate)
                    if (seat.Life == SeatLife.ResetPending)
                        seat.Life = SeatLife.Dead;
                throw;
            }
        }, ct).ConfigureAwait(false);
    }
}
