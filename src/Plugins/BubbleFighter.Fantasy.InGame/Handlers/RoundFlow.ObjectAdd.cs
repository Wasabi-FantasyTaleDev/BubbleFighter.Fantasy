using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    private IMessageSender<RoundObjectAddAcknowledged>? _objectAdded;

    /// <summary>
    /// Acknowledges a starting player's request to add its own character, once per round and only to that player.
    /// The client sends it right after its map report, with its character's scene object (kr-812 sub_661AF0 and
    /// kr-1256 sub_755330); the object must be the one the seat's scene identity carries.
    /// </summary>
    public async ValueTask HandleObjectAddRequestedAsync(ISession session, RoundObjectAddRequested request, CancellationToken ct)
    {
        if (FindGameReportRoom(session) is not { } room) return;
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            lock (room.Gate)
            {
                var seat = room.FindByGame(session.Id);
                if (seat is not null && IsSoccerBall(room, seat, request.ObjectId))
                    room.SoccerBall = request.ObjectId;
                else if (seat is null || room.Phase != RoomPhase.Starting || seat.ObjectAdded)
                {
                    _logger.LogInformation("Session {Id} object {Object} add request outside its seat's start ignored.", session.Id, request.ObjectId);
                    return;
                }
                // A client cannot add another seat's character; the request is dropped like any other stray report.
                else if (request.ObjectId != RoomRegistry.SceneEntity(seat.Slot))
                {
                    _logger.LogWarning("Session {Id} slot {Slot} asked to add object {Object}, which is not its own; ignored.",
                        session.Id, seat.Slot, request.ObjectId);
                    return;
                }
                else
                    seat.ObjectAdded = true;
            }
            if (_objectAdded is not null)
                await _objectAdded.SendAsync(session, new RoundObjectAddAcknowledged(request.ObjectId), operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // The soccer host adds one ball besides its character: kr-1256 sub_6E63C0 builds the SoccerBall and
    // kr-1256 sub_6DDE20 requests its add with the ball's handle, an object the host allocates from its own seat's
    // scene range. Any other extra object, or a second ball, stays refused.
    private static bool IsSoccerBall(PlayRoom room, RoomSeat seat, int objectId)
    {
        if (room.ModeId != (int)GameModeId.EventSoccer || seat.Slot != room.MasterSlot
            || room.Phase is not (RoomPhase.Starting or RoomPhase.Playing))
            return false;
        return PlayRoom.OwnsSceneObject(seat, objectId) && (room.SoccerBall is null || room.SoccerBall == objectId);
    }
}
