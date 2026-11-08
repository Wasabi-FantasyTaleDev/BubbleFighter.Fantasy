using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundFinalZoneItemsSpawned>? FinalZoneItemsSpawned { get; set; }
    internal IMessageSender<RoundFinalZoneItemPicked>? FinalZoneItemPicked { get; set; }
    internal IMessageSender<RoundFinalZoneBackpackSpawned>? FinalZoneBackpackSpawned { get; set; }
    internal IMessageSender<RoundFinalZoneBackpackPicked>? FinalZoneBackpackPicked { get; set; }

    /// <summary>Transfers one backpack entry and identifies the receiving player independently of its scene handle.</summary>
    internal ValueTask HandleFinalZoneBackpackPickupAsync(ISession session, RoundFinalZoneBackpackPickupRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive
                || !ledger.TryPickUpBackpack(RoomRegistry.SceneEntity(seat.Slot), request.ObjectId, request.ItemSpecId)) return;
            // kr-1256 sub_6D2040 supplies the room user number through kr-1256 sub_514DE0 to CharacterBase+580.
            await PublishFinalZoneAsync(room, FinalZoneBackpackPicked!,
                new(seat.UserNum, request.ObjectId, request.ItemSpecId), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Arbitrates ground-item claims on the room queue and echoes only the successful transfer.</summary>
    internal ValueTask HandleFinalZoneItemPickupAsync(ISession session, RoundFinalZoneItemPickupRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || request.Actor != RoomRegistry.SceneEntity(seat.Slot)
                || !ledger.TryPickUp(request.Actor, request.ObjectId)) return;
            await PublishFinalZoneAsync(room, FinalZoneItemPicked!,
                new(request.ObjectId, request.Actor), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Publishes live drops only after debiting their exact owner's issued inventory.</summary>
    internal ValueTask HandleFinalZoneItemsDroppedAsync(ISession session, RoundFinalZoneItemsDropped request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive) return;
            var items = ledger.TryDrop(RoomRegistry.SceneEntity(seat.Slot), request.Items);
            if (items is not { Count: > 0 }) return;
            await PublishFinalZoneAsync(room, FinalZoneItemsSpawned!, new(items), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Accepts one final inventory from an eliminated participant, never from a living or foreign actor.</summary>
    internal ValueTask HandleFinalZoneBackpackDroppedAsync(ISession session, RoundFinalZoneBackpackDropped request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Dead || request.OwnerActor != RoomRegistry.SceneEntity(seat.Slot)) return;
            var backpack = ledger.TryDropBackpack(request);
            if (backpack is null) return;
            await PublishFinalZoneAsync(room, FinalZoneBackpackSpawned!, backpack, operationCt).ConfigureAwait(false);
        }, ct);

    private ValueTask ExecuteFinalZoneInventoryAsync(ISession session,
        Func<PlayRoom, RoomSeat, FinalZoneItemLedger, CancellationToken, ValueTask> operation, CancellationToken ct,
        bool allowRelay = false)
    {
        if (_registry.TryGet(session.Id) is not { } room || !IsFinalZoneMode(room)) return ValueTask.CompletedTask;
        int generation;
        lock (room.Gate) generation = room.StartGeneration;
        return new ValueTask(room.Executor.ExecuteAsync(async operationCt =>
        {
            RoomSeat owner;
            FinalZoneItemLedger ledger;
            lock (room.Gate)
            {
                if (!FinalZoneReportIsCurrent(room, session, generation)
                    || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                        || allowRelay && ReferenceEquals(seat.Relay, session)) is not { } seat
                    || room.FinalZone?.Items is not { } items) return;
                owner = seat;
                ledger = items;
            }
            await operation(room, owner, ledger, operationCt).ConfigureAwait(false);
        }, ct));
    }

    private static async ValueTask PublishFinalZoneAsync<T>(PlayRoom room, IMessageSender<T> sender,
        T message, CancellationToken ct) where T : IMessage
    {
        RoomSeat[] recipients;
        lock (room.Gate) recipients = [.. room.Seats];
        foreach (var member in recipients)
            if (!member.Game.Connection.IsClosed)
                await sender.SendAsync(member.Game, message, ct).ConfigureAwait(false);
    }
}
