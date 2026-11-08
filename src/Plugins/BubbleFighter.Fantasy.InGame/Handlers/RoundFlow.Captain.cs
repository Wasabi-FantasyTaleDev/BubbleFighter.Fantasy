using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>
    /// Gets or sets the build's Captain announcement encoder; null leaves mode 20 an ordinary team round. The client
    /// makes the named character a Captain itself (kr-812 sub_4304C0 and the Captain manager's events), so the
    /// round only chooses the seats.
    /// </summary>
    internal IMessageSender<RoundCaptainAppointed>? CaptainAppointed { get; set; }

    private bool IsCaptainMode(PlayRoom room) => CaptainAppointed is not null && room.ModeId == (int)GameModeId.Captain;

    // Called on the executor once the round is playing: kr-812 sub_4304C0 ignores a seat without a character,
    // so the announcement waits until every member's client has built them.
    private async ValueTask AppointCaptainsAsync(PlayRoom room, CancellationToken ct)
    {
        if (!IsCaptainMode(room)) return;
        List<int> appointed = [];
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled) return;
            foreach (var team in new byte[] { 1, 2 })
            {
                if (PickCaptain(room, team, excluded: null) is not { } captain) continue;
                room.Captains.Appoint(team, captain);
                appointed.Add(captain.Slot);
            }
            members = [.. room.Seats];
        }
        foreach (var slot in appointed)
            await AnnounceCaptainAsync(room, members, slot, ct).ConfigureAwait(false);
    }

    private readonly record struct CaptainReportStamp(int RoundGeneration, RoomSeat Reporter, long ReporterLife,
        RoomSeat? Victim, long? VictimLife);

    private static CaptainReportStamp? CaptureCaptainReport(PlayRoom room, ISession session, int? victimSlot)
    {
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled
                || room.FindByGame(session.Id) is not { } reporter || !ReferenceEquals(reporter.Game, session)) return null;
            var victim = room.Seats.FirstOrDefault(seat => seat.Slot == victimSlot);
            return new(room.StartGeneration, reporter, reporter.Life.Generation, victim, victim?.Life.Generation);
        }
    }

    // The native reports have no round or life token; fence queued work to the incarnation observed at admission.
    private static bool CaptainReportIsCurrent(PlayRoom room, CaptainReportStamp stamp) =>
        room.Phase == RoomPhase.Playing && !room.RoundCancelled && room.StartGeneration == stamp.RoundGeneration
        && ReferenceEquals(room.FindByGame(stamp.Reporter.Game.Id), stamp.Reporter)
        && stamp.Reporter.Life.Generation == stamp.ReporterLife
        && (stamp.Victim is null || room.Seats.Contains(stamp.Victim) && stamp.Victim.Life.Generation == stamp.VictimLife);

    /// <summary>Accepts the current Captain's own death once and appoints a living teammate when one is available.</summary>
    internal async ValueTask HandleCaptainDroppedAsync(ISession session, RoundCaptainDropped report, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room || !IsCaptainMode(room)
            || CaptureCaptainReport(room, session, report.Slot) is not { } stamp) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || !CaptainReportIsCurrent(room, stamp)) return;
                var seat = stamp.Reporter;
                if (report.Slot != seat.Slot || seat.Life.State == SeatLife.ResetPending
                    || !room.Captains.TryDrop(seat.Team, seat)) return;
                // kr-812 sub_57E200 -> event 311 -> kr-812 sub_42BCE0 is local death evidence, even before the respawn wait.
                seat.Life.Die();
                seat.Life.Burst();
            }
            await ReplaceDepartedCaptainsAsync(room, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // Runs after a departure, accepted Captain death or respawn. Dead characters wait for the common spawn path.
    private async ValueTask ReplaceDepartedCaptainsAsync(PlayRoom room, CancellationToken ct)
    {
        if (!IsCaptainMode(room)) return;
        List<int> appointed = [];
        RoomSeat[] members;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled) return;
            foreach (var team in new byte[] { 1, 2 })
            {
                if (!room.Captains.NeedsAppointment(team, room.Seats)) continue;
                if (!room.Seats.Any(seat => seat.Team == team))
                {
                    room.Captains.Vacate(team);
                    continue;
                }
                if (PickCaptain(room, team, room.Captains.CaptainOf(team)) is not { } successor) continue;
                room.Captains.Appoint(team, successor);
                appointed.Add(successor.Slot);
            }
            members = [.. room.Seats];
        }
        foreach (var slot in appointed)
            await AnnounceCaptainAsync(room, members, slot, ct).ConfigureAwait(false);
    }

    // Identity admission does not spend score credit: ordinary and Captain reports share ParticipantLife.CreditDeath.
    private bool CreditsCaptainKill(PlayRoom room, int killerSlot, int? victimSlot) =>
        IsCaptainMode(room) && victimSlot is { } slot
        && room.Seats.FirstOrDefault(seat => seat.Slot == slot) is { } victim
        && room.Seats.FirstOrDefault(seat => seat.Slot == killerSlot) is { } killer
        && AreOpponents(room, killer.Team, victim.Team) && room.Captains.WasCaptain(victim.Team, victim);

    // kr-812 sub_4304C0 marks an existing character; do not appoint a dead character awaiting respawn.
    private static RoomSeat? PickCaptain(PlayRoom room, byte team, int? excluded)
    {
        var standing = room.Seats.Where(seat => seat.Team == team && !seat.Game.Connection.IsClosed
            && seat.Life.State is SeatLife.Alive or SeatLife.Bubble).ToArray();
        var teammates = standing.Where(seat => seat.Slot != excluded).ToArray();
        var pool = teammates.Length > 0 ? teammates : standing;
        return pool.Length == 0 ? null : pool[Random.Shared.Next(pool.Length)];
    }

    /// <summary>
    /// Answers the Captain's ULTRA: the client checks its own gauge and its own Captain identity before it sends the
    /// request (cn-706 sub_428D10 tests the gauge's ready byte and cn-706 sub_421CB0), so the round only checks that
    /// the sender holds its team's current appointment and confirms the item effect to that player alone. The
    /// confirmation's consumer (cn-706 sub_4D0B70, kr-812 sub_517D60 -> kr-812 sub_573730) sends the client's own
    /// direct effect, which the round feed passes to the other players; no appointment is repeated.
    /// </summary>
    private async ValueTask HandleCaptainUltraAsync(PlayRoom room, ISession session, CancellationToken ct)
    {
        if (CaptureCaptainReport(room, session, victimSlot: null) is not { } stamp) return;
        lock (room.Gate)
        {
            if (!CaptainReportIsCurrent(room, stamp) || !CanUseCaptainUltra(room, stamp.Reporter)) return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                if (!CaptainReportIsCurrent(room, stamp) || !CanUseCaptainUltra(room, stamp.Reporter)) return;
            }
            var slot = stamp.Reporter.Slot;
            var sender = _countedItemUsed
                ?? throw new InvalidOperationException("The Captain's ULTRA requires the build's direct item-effect sender.");
            await sender.SendAsync(session, new RoundCountedItemUsed(slot, _options.CaptainUltraItemKind), operationCt).ConfigureAwait(false);
            _logger.LogInformation("Room {Room}: the Captain (seat {Seat}) used its ULTRA.", room.Id, slot);
        }, ct).ConfigureAwait(false);
    }

    private static bool CanUseCaptainUltra(PlayRoom room, RoomSeat seat) =>
        seat.Life.State is (SeatLife.Alive or SeatLife.Bubble) && room.Captains.Serves(seat);

    private async ValueTask AnnounceCaptainAsync(PlayRoom room, RoomSeat[] members, int slot, CancellationToken ct)
    {
        var appointed = new RoundCaptainAppointed(slot);
        foreach (var member in members)
            if (!member.Game.Connection.IsClosed)
                await CaptainAppointed!.SendAsync(member.Game, appointed, ct).ConfigureAwait(false);
        _logger.LogInformation("Room {Room}: seat {Seat} announced as Captain to {Count} members.", room.Id, slot, members.Length);
    }
}
