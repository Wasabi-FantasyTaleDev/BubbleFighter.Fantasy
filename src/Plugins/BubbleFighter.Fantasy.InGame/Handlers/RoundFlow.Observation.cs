using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private readonly IRoundObservationSender? _observer;

    private bool RespawnIsDisabled(PlayRoom room) =>
        IsFinalZoneMode(room) || !_options.AllowsRespawn(room.ModeId);

    // kr-812 sub_43C340 reports death; kr-812 sub_43DD80 requests respawn after the death wait.
    // kr-812 sub_52A640 consumes observation instead, resolves the actor and checks team elimination.
    private async ValueTask<bool> TryObserveInsteadOfRespawnAsync(ISession session, PlayRoom room,
        RoundSpawnAccepted request, CancellationToken ct)
    {
        ISession[] recipients;
        lock (room.Gate)
        {
            // kr-1256 sub_CDC4A0 finalizes Area32 deaths through its own elimination notification.
            if (IsFinalZoneMode(room)) return true;
            if (!RespawnIsDisabled(room)) return false;
            if (room.Phase != RoomPhase.Playing || room.FindByGame(session.Id) is not { } seat
                || !OwnsActor(seat, request.Actor, request.ActorSeat)
                || seat.Life.DeathActor != request.Actor || !seat.Life.TryObserve())
                return true;
            recipients = [.. room.Seats.Select(member => member.Game)];
        }
        foreach (var recipient in recipients)
            if (!recipient.Connection.IsClosed)
                await _observer!.SendAsync(recipient, new(request.Actor), ct).ConfigureAwait(false);
        return true;
    }
}
