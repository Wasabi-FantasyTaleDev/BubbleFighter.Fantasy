using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    /// <summary>Releases the map-contact wait for an authenticated member of the current playing round.</summary>
    internal async ValueTask HandlePortalMoveAsync(ISession session, RoundPortalMoveRequested request,
        IMessageSender<RoundPortalMoved> sender, CancellationToken ct)
    {
        if (!float.IsFinite(request.X) || !float.IsFinite(request.Y) || !float.IsFinite(request.Z)
            || _registry.TryGet(session.Id) is not { } room) return;
        int generation;
        RoomSeat member;
        lock (room.Gate)
        {
            if (room.Phase != RoomPhase.Playing || room.RoundCancelled
                || room.FindByGame(session.Id) is not { } seat || !ReferenceEquals(seat.Game, session)) return;
            generation = room.StartGeneration;
            member = seat;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            ISession[] targets;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.StartGeneration != generation
                    || !ReferenceEquals(room.FindByGame(session.Id), member)) return;
                targets = [.. room.Seats.Select(seat => seat.Game).Where(game => !game.Connection.IsClosed)];
            }
            // cn-706 sub_7C6B60 resolves the region in the map and starts spring/portal motion on its local owner.
            var moved = new RoundPortalMoved(request.PortalId, member.Slot);
            foreach (var target in targets)
                await sender.SendAsync(target, moved, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
