using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    internal async ValueTask OnEquipmentAttributesPublishedAsync(EquipmentViewPublished publication, CancellationToken ct)
    {
        if (publication.IsSceneReplay || publication.Protocol != _options?.GameProtocol)
            return;
        if (_registry.TryGet(publication.Session.Id) is not { } room)
        {
            await OnPlazaEquipmentPublishedAsync(publication, ct).ConfigureAwait(false);
            return;
        }
        await room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat[] members;
            RoomSeatUpdated update;
            lock (room.Gate)
            {
                var seat = room.FindByGame(publication.Session.Id);
                if (seat is null || seat.UserNum != publication.View.UserNum || room.Phase != RoomPhase.Waiting
                    || !ReferenceEquals(seat.Game, publication.Session)) return;
                var currentIds = seat.EquippedItemIds ?? [];
                var next = publication.View.ItemAttributes.Where(attribute => currentIds.Contains(attribute.ItemId)).ToArray();
                if (Equal(seat.ItemAttributes, next)) return;
                seat.ItemAttributes = next;
                members = [.. room.Seats];
                update = new RoomSeatUpdated(room.Id, View(seat)) { Room = View(room) };
            }
            foreach (var member in members)
                await _seatUpdated.SendAsync(member.Game, update, operationCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static bool Equal(IReadOnlyList<RoomItemAttribute> left, IReadOnlyList<RoomItemAttribute> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.ItemId == pair.Second.ItemId
            && pair.First.Kind == pair.Second.Kind && pair.First.Unknown28 == pair.Second.Unknown28
            && pair.First.Unknown32 == pair.Second.Unknown32 && pair.First.Values.SequenceEqual(pair.Second.Values));
}
