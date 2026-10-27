using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    /// <summary>Delegates a waiting room under its executor and acknowledges native refusal states.</summary>
    public async ValueTask HandleMasterChangeAsync(ISession session, RoomMasterChangeRequested request, CancellationToken ct)
    {
        var replies = _masterChangeReply
            ?? throw new InvalidOperationException("Room master changes require a reply sender.");
        var (room, _) = _registry.RequireGame(session);
        await room.Executor.ExecuteAsync(async token =>
        {
            RoomSeat[] members = [];
            var result = 0;
            var nextMaster = -1;
            int? previousAiController = null;
            lock (room.Gate)
            {
                var requester = room.FindByGame(session.Id);
                var target = room.Seats.FirstOrDefault(member => string.Equals(member.Nickname, request.Nickname, StringComparison.Ordinal));
                // kr-812 sub_801D00's native notices identify results 1/2/3; modern consumers retain the same cases.
                if (requester is null || !ReferenceEquals(requester.Game, session) || requester.Slot != room.MasterSlot)
                    result = 2;
                else if (room.Phase != RoomPhase.Waiting || target is null)
                    result = 1;
                else if (target.Slot == room.MasterSlot)
                    result = 3;
                else
                {
                    previousAiController = room.Bots.Count > 0 ? requester.UserNum : null;
                    if (!room.TransferMaster(target.Slot))
                        throw new InvalidOperationException("The selected room master disappeared under the room gate.");
                    // kr-812 sub_7FD460 and kr-1256 sub_9DA3C0 clear the new master's ready flag.
                    target.Ready = false;
                    room.TransferBots(target);
                    nextMaster = target.Slot;
                    members = [.. room.Seats];
                }
            }
            // kr-812 sub_801D00 clears the outstanding-request flag even for result zero.
            if (result != 0)
            {
                await replies.SendAsync(session, new(request.Nickname, result), token).ConfigureAwait(false);
                return;
            }
            // A disconnected requester cannot prevent the committed owner reaching the surviving members.
            await NotifyScenePeerAsync(session, replies, new(request.Nickname, 0), token).ConfigureAwait(false);
            var changed = new RoomMasterChanged(room.Id, nextMaster) { PreviousAiControllerUserNum = previousAiController };
            foreach (var member in members)
                await NotifyScenePeerAsync(member.Game, _masterChanged, changed, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
