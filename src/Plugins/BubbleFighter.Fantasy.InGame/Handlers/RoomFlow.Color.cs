using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal async ValueTask OnColorChangedAsync(PlayerColorChanged change, CancellationToken ct)
    {
        await _plaza.Executor.ExecuteAsync(async operationCt =>
        {
            PlazaMember? member = null;
            ISession[] observers = [];
            lock (_plaza.Gate)
            {
                var seat = _plaza.Snapshot().FirstOrDefault(candidate => candidate.UserNumber == change.UserNum);
                if (seat is null) return;
                seat.ColorIndex = change.ColorIndex;
                member = seat.ToMember();
                observers = _plaza.Sessions().Where(candidate => candidate.SessionId != seat.SessionId
                    && _registry.TryGet(candidate.SessionId) is null).Select(candidate => candidate.Game).ToArray();
            }
            if (_plazaMemberEntered is null) return;
            foreach (var observer in observers)
            {
                await _plazaMemberEntered.SendAsync(observer, new(member), operationCt).ConfigureAwait(false);
                if (_plazaPositionReceived is not null)
                {
                    var (userNumber, sequence, position) = _plaza.LastPosition(change.UserNum);
                    await _plazaPositionReceived.SendAsync(observer, new(userNumber, sequence, position, member.EntrySeed), operationCt).ConfigureAwait(false);
                }
            }
        }, ct).ConfigureAwait(false);
        foreach (var room in _registry.All())
        {
            await room.Executor.ExecuteAsync(async operationCt =>
            {
                RoomSeat[] members;
                RoomSeatUpdated update;
                lock (room.Gate)
                {
                    var seat = room.Seats.FirstOrDefault(candidate => candidate.UserNum == change.UserNum);
                    if (seat is null) return;
                    seat.ColorIndex = change.ColorIndex;
                    members = [.. room.Seats];
                    update = new(room.Id, View(seat)) { Room = View(room) };
                }
                foreach (var member in members)
                    await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }
}
