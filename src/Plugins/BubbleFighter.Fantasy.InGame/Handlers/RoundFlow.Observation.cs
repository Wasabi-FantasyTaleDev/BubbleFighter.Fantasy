using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IRoundObservationSender? _observer;

    private bool RespawnIsDisabled(PlayRoom room) =>
        IsFinalZoneMode(room) || !_options.AllowsRespawn(room.ModeId);

    private sealed record ObservationRequestStamp(int Generation, RoomSeat Seat, long Life, ISession? Match);

    // Capture before enqueueing: a previous scene's request cannot observe a replacement life that is already dead.
    private ObservationRequestStamp? CaptureObservationRequest(PlayRoom room, ISession session, RoundSpawnAccepted request)
    {
        lock (room.Gate)
        {
            if (IsFinalZoneMode(room) || !RespawnIsDisabled(room) || room.Phase != RoomPhase.Playing
                || room.RoundCancelled || room.IsStopping || session.Connection.IsClosed
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)) return null;
            var match = request.Rules.Admission == RoundRespawnAdmission.CleanedDeath ? MatchLink(room, seat) : null;
            if (request.Rules.Admission == RoundRespawnAdmission.CleanedDeath && match is null) return null;
            // Cleanup may already be queued ahead of this reset; state admission belongs on the executor.
            return new(room.StartGeneration, seat, seat.Life.Generation, match);
        }
    }

    private bool ObservationAdmitted(PlayRoom room, RoomSeat seat, RoundSpawnAccepted request) =>
        request.Rules.Admission == RoundRespawnAdmission.CleanedDeath
            ? UsesNativeLife(room) && request.Actor == RoomRegistry.SceneEntity(seat.Slot)
                && seat.Life.CanGrantReset && MatchLink(room, seat) is not null
            : OwnsActor(seat, request.Actor, request.ActorSeat) && seat.Life.DeathActor == request.Actor
                && seat.Life.State == SeatLife.Dead;

    private static bool ObservationRequestIsCurrent(PlayRoom room, ISession session, ObservationRequestStamp stamp) =>
        room.Phase == RoomPhase.Playing && !room.RoundCancelled && !room.IsStopping
        && room.StartGeneration == stamp.Generation && !session.Connection.IsClosed
        && ReferenceEquals(room.FindByGame(session.Id), stamp.Seat) && ReferenceEquals(stamp.Seat.Game, session)
        && stamp.Seat.Life.Generation == stamp.Life
        && (stamp.Match is null || ReferenceEquals(MatchLink(room, stamp.Seat), stamp.Match));

    // kr-812 sub_52A640 consumes the legacy death handle; kr-1256 sub_703C80 consumes the native scene actor.
    // Its observation performs the team-elimination check, including the armed bomb's distinct outcome path.
    private async ValueTask<bool> TryObserveInsteadOfRespawnAsync(ISession session, PlayRoom room,
        RoundSpawnAccepted request, ObservationRequestStamp? stamp, CancellationToken ct)
    {
        RoomSeat[] recipients;
        lock (room.Gate)
        {
            // kr-1256 sub_CDC4A0 finalizes Area32 deaths through its own elimination notification.
            if (IsFinalZoneMode(room)) return true;
            if (!RespawnIsDisabled(room)) return false;
            if (stamp is null || !ObservationRequestIsCurrent(room, session, stamp)
                || !ObservationAdmitted(room, stamp.Seat, request) || !stamp.Seat.Life.TryObserve()) return true;
            recipients = [.. room.Seats];
        }
        foreach (var recipient in recipients)
        {
            lock (room.Gate)
            {
                if (!ObservationRequestIsCurrent(room, session, stamp)) return true;
                if (!ReferenceEquals(room.FindByGame(recipient.Game.Id), recipient) || recipient.Game.Connection.IsClosed)
                    continue;
            }
            await _observer!.SendAsync(recipient.Game, new(request.Actor), ct).ConfigureAwait(false);
        }
        return true;
    }
}
