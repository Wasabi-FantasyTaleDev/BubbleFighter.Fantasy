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
        int? finalZoneGeneration = IsFinalZoneMode(room) ? room.StartGeneration : null;
        int? scriptZombieGeneration;
        lock (room.Gate) scriptZombieGeneration = (_options.ScriptZombies || _options.GhostZombies || ZombieEscapeEnabled) ? room.StartGeneration : null;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoundScoreUpdated? update = null;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || (!UsesNativeLife(room) && !_options.ZombiePvpModeIds.Contains(room.ModeId)))
                    return;
                if (finalZoneGeneration is { } generation && !FinalZoneReportIsCurrent(room, session, generation)) return;
                if (IsScriptZombieMode(room) && (scriptZombieGeneration is not { } zombieGeneration
                    || !ScriptZombieReportIsCurrent(room, session, zombieGeneration))) return;
                if (IsZombieEscapeMode(room) && (scriptZombieGeneration is not { } escapeGeneration
                    || !ZombieEscapeReportIsCurrent(room, session, escapeGeneration))) return;
                var sender = room.Seats.FirstOrDefault(seat => seat.Game.Id == session.Id || seat.Relay?.Id == session.Id);
                if (sender is null)
                    return;
                var own = room.ControlsActor(sender, report.Actor);
                var target = LifeActor(room, report.Actor);
                var actor = own ? target! : new LifeParticipant(sender, null);
                var initiator = report.Initiator is { } handle ? LifeActor(room, handle) : null;
                LifeCheck? ignored = null;
                switch (report.Event)
                {
                    case RoundLifeEvent.Bubbled or RoundLifeEvent.Died or RoundLifeEvent.BubbleExpired
                        or RoundLifeEvent.DeathCleaned or RoundLifeEvent.ResetConfirmed when !own:
                        ignored = LifeCheck.OwnEventForeignActor;
                        break;
                    case RoundLifeEvent.Bubbled:
                        if (!actor.Life.Bubble()) ignored = LifeCheck.BubbledWhileNotAlive;
                        update = CreditAction(room, initiator?.Slot, actor.Slot, rescue: false, lifeEvidence: true);
                        break;
                    case RoundLifeEvent.Died:
                        if (!actor.Life.Die() && !actor.Life.Burst()) ignored = LifeCheck.DiedWhileNotAlive;
                        (update, _, _) = CreditKill(room, new(initiator?.Slot, actor.Slot, KillEvidence.DirectDeath,
                            InitiatorNamed: report.Initiator is not null));
                        break;
                    case RoundLifeEvent.BubbleExpired:
                        if (!actor.Life.Burst()) ignored = LifeCheck.ExpiredWhileNotBubbled;
                        break;
                    case RoundLifeEvent.Released or RoundLifeEvent.Finished when !own
                        && !(report.Initiator is { } contact && (room.ControlsActor(sender, contact)
                            || report.Event == RoundLifeEvent.Finished && contact >= 0 && !room.IsMemberCharacter(contact)
                                && sender.Slot == room.MasterSlot)):
                        ignored = LifeCheck.OwnEventForeignActor;
                        break;
                    case RoundLifeEvent.Released:
                        if (target?.Life.Release() != true) ignored = LifeCheck.ReleasedNotBubbled;
                        update = CreditAction(room, initiator?.Slot, target?.Slot, rescue: true, lifeEvidence: true);
                        break;
                    case RoundLifeEvent.DeathCleaned:
                        // cn-324 sub_4757B0 / kr-812 sub_51E920 finalize the named actor even without an earlier score.
                        // This establishes no attacker or reward and does not alter ordinary reset admission.
                        // The ghost king cleanup itself ends the native round (kr-1256 sub_AC5110 /
                        // cn-774 sub_CA7D50); it need not follow a separate server-observed death frame.
                        if (_options.ZombiePvpModeIds.Contains(room.ModeId)
                            || IsScriptZombieMode(room) && room.ScriptZombies?.KingSlot == actor.Slot)
                        {
                            actor.Life.Die();
                            actor.Life.Burst();
                        }
                        if (!actor.Life.CleanDeath()) ignored = LifeCheck.CleanedWhileNotDead;
                        break;
                    case RoundLifeEvent.ResetConfirmed when RespawnIsDisabled(room):
                        ignored = LifeCheck.ResetConfirmedWhileRespawnDisabled;
                        break;
                    // The confirmation must come on the match link the reset was granted on.
                    case RoundLifeEvent.ResetConfirmed when actor.Life.ConfirmsGrant(session):
                        actor.Life.Revive();
                        actor.ClearMovement();
                        break;
                    case RoundLifeEvent.ResetConfirmed:
                        ignored = actor.Life.State == SeatLife.ResetPending ? LifeCheck.ResetConfirmedOnOtherLink : LifeCheck.ResetConfirmedWithoutGrant;
                        break;
                    case RoundLifeEvent.Finished:
                        (update, ignored, _) = CreditKill(room, new(initiator?.Slot, target?.Slot,
                            KillEvidence.BubbleContact, InitiatorNamed: report.Initiator is not null));
                        break;
                }
                if (ignored is { } check)
                    Observe(room, sender, report, check);
            }
            await ReplaceDepartedCaptainsAsync(room, operationCt).ConfigureAwait(false);
            if (update is not null)
                await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
            if (_options.ZombiePvpModeIds.Contains(room.ModeId))
                await ZombiePvpOutcomeAsync(room, operationCt).ConfigureAwait(false);
            await SynchronizeFinalZoneAsync(room, operationCt).ConfigureAwait(false);
            await ScriptZombieOutcomeAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Counts an ignored life report; the round's first of each kind is logged at information level, the rest at
    // debug, and the round end logs the totals. Nothing is refused. Called under the room gate.
    private void Observe(PlayRoom room, RoomSeat sender, RoundLifeReported report, LifeCheck check)
    {
        var level = room.LifeChecks.Note(check) ? LogLevel.Information : LogLevel.Debug;
        _logger.Log(level, "Room {Room} slot {Slot}: life report {Event} for actor {Actor} ignored ({Check}); the seat is {Life}.",
            room.Id, sender.Slot, report.Event, report.Actor, check, sender.Life.State);
    }
}
