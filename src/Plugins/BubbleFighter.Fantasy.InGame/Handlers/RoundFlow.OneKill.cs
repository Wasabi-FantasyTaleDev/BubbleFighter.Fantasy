using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal void ValidateOneKill()
    {
        if (_options.NativeOneKill && (_feed is null || FinalZoneEliminated is null))
            throw new ConfigException("native_one_kill requires the native round feed and FinalZone elimination exchange.");
    }

    private async ValueTask HandleOneKillAsync(PlayRoom room, ISession session, RoundFeedSent feed, CancellationToken ct)
    {
        if (!_options.NativeOneKill || !IsFinalZoneMode(room) || room.ModeId != (int)GameModeId.Area32
            || feed.OneKill is not { } action || feed.IncludeSender
            || action.Kind is not (RoundOneKillKind.Request or RoundOneKillKind.Confirmation)) return;
        int generation;
        RoomSeat reporter;
        LifeParticipant attacker;
        LifeParticipant victim;
        long attackerLife;
        long victimLife;
        FinalZoneRoundState finalZone;
        lock (room.Gate)
        {
            generation = room.StartGeneration;
            if (!FinalZoneReportIsCurrent(room, session, generation)
                || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                    || ReferenceEquals(seat.Relay, session)) is not { } sender
                || !OneKillSessionIsCurrent(sender, session, generation)
                || LifeActor(room, action.Attacker) is not { } source
                || LifeActor(room, action.Victim) is not { } target) return;
            reporter = sender;
            attacker = source;
            victim = target;
            attackerLife = source.Life.Generation;
            victimLife = target.Life.Generation;
            finalZone = room.FinalZone!;
        }
        await ExecuteFinalZoneGuardedAsync(room, finalZone, async operationCt =>
        {
            RoundScoreUpdated? update = null;
            OneKillTarget[] participants;
            lock (room.Gate)
            {
                if (!FinalZoneReportIsCurrent(room, session, generation) || !ReferenceEquals(room.FinalZone, finalZone)
                    || room.ModeId != (int)GameModeId.Area32 || !room.Seats.Contains(reporter)
                    || !OneKillSessionIsCurrent(reporter, session, generation)
                    || !ReferenceEquals(LifeActor(room, action.Attacker)?.Life, attacker.Life)
                    || !ReferenceEquals(LifeActor(room, action.Victim)?.Life, victim.Life)
                    || attacker.Life.Generation != attackerLife || victim.Life.Generation != victimLife
                    || action.Attacker == action.Victim || !AreOpponents(room, attacker.Team, victim.Team)
                    || !room.ControlsActor(reporter, action.Kind == RoundOneKillKind.Request ? action.Attacker : action.Victim)
                    || victim.Life.State is not (SeatLife.Alive or SeatLife.Bubble)
                    || action.Kind == RoundOneKillKind.Request && attacker.Life.State != SeatLife.Alive) return;
                participants = room.Seats.Select(member => new OneKillTarget(member,
                    MatchLink(room, member) ?? throw new InvalidOperationException("OneKill participant has no current match transport."),
                    member.Life.Generation)).ToArray();
                RequireOneKillPublication(room, finalZone, participants);
                if (action.Kind == RoundOneKillKind.Confirmation)
                {
                    // kr-1256 sub_6FBA90 applies cssDeadOneKilled through kr-1256 sub_50B730; the request never kills.
                    victim.Life.Die();
                    victim.Life.Burst();
                    (update, _, _) = CreditKill(room, new(attacker.Slot, victim.Slot, KillEvidence.DirectDeath));
                }
            }
            // kr-1256 sub_7AA3D0 queues the sender's own copy; every other participant needs the ordered transition.
            var received = new RoundFeedReceived(feed.Code, feed.Payload);
            foreach (var target in participants.Where(target => !ReferenceEquals(target.Seat, reporter)))
            {
                lock (room.Gate) RequireOneKillPublication(room, finalZone, participants);
                await _feed!.SendAsync(target.Match, received, operationCt).ConfigureAwait(false);
                lock (room.Gate) RequireOneKillPublication(room, finalZone, participants);
            }
            operationCt.ThrowIfCancellationRequested();
            if (update is not null)
                await SendScoreAsync(room, update, operationCt).ConfigureAwait(false);
            operationCt.ThrowIfCancellationRequested();
            lock (room.Gate) RequireOneKillPublication(room, finalZone, participants);
            if (action.Kind == RoundOneKillKind.Confirmation)
                await SynchronizeFinalZoneAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private sealed record OneKillTarget(RoomSeat Seat, ISession Match, long Life);

    private void RequireOneKillPublication(PlayRoom room, FinalZoneRoundState state, OneKillTarget[] participants)
    {
        if (!ReferenceEquals(room.FinalZone, state) || room.StartGeneration != state.Generation
            || room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
            || room.ModeId != (int)GameModeId.Area32 || state.Finished || room.Seats.Count != participants.Length
            || participants.Any(target => !ReferenceEquals(room.FindByGame(target.Seat.Game.Id), target.Seat)
                || target.Seat.Game.Connection.IsClosed || !ReferenceEquals(MatchLink(room, target.Seat), target.Match)
                || target.Seat.Life.Generation != target.Life
                || _options.NativeMatchRelay && !NativeMatchReady(room, target.Seat)))
            throw new InvalidOperationException("OneKill participant or match transport changed during ordered publication.");
    }

    private static bool OneKillSessionIsCurrent(RoomSeat seat, ISession session, int generation) =>
        ReferenceEquals(seat.Game, session)
        || ReferenceEquals(seat.Relay, session) && seat.RelayGeneration == generation;
}
