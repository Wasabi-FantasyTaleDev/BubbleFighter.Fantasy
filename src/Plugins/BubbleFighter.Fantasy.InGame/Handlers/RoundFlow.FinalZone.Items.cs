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
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive
                || !ledger.TryPickUpBackpack(RoomRegistry.SceneEntity(seat.Slot), request.ObjectId, request.ItemSpecId)) return;
            // kr-1256 sub_6D2040 supplies the room user number through kr-1256 sub_514DE0 to CharacterBase+580.
            await PublishFinalZoneAsync(room, publication, FinalZoneBackpackPicked!,
                new(seat.UserNum, request.ObjectId, request.ItemSpecId), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Arbitrates ground-item claims on the room queue and echoes only the successful transfer.</summary>
    internal ValueTask HandleFinalZoneItemPickupAsync(ISession session, RoundFinalZoneItemPickupRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || request.Actor != RoomRegistry.SceneEntity(seat.Slot)
                || !ledger.TryPickUp(request.Actor, request.ObjectId)) return;
            await PublishFinalZoneAsync(room, publication, FinalZoneItemPicked!,
                new(request.ObjectId, request.Actor), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Publishes live drops only after debiting their exact owner's issued inventory.</summary>
    internal ValueTask HandleFinalZoneItemsDroppedAsync(ISession session, RoundFinalZoneItemsDropped request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive) return;
            var items = ledger.TryDrop(RoomRegistry.SceneEntity(seat.Slot), request.Items);
            if (items is not { Count: > 0 }) return;
            await PublishFinalZoneAsync(room, publication, FinalZoneItemsSpawned!, new(items), operationCt).ConfigureAwait(false);
        }, ct);

    /// <summary>Accepts one final inventory from an eliminated participant, never from a living or foreign actor.</summary>
    internal ValueTask HandleFinalZoneBackpackDroppedAsync(ISession session, RoundFinalZoneBackpackDropped request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Dead || request.OwnerActor != RoomRegistry.SceneEntity(seat.Slot)) return;
            var backpack = ledger.TryDropBackpack(request);
            if (backpack is null) return;
            await PublishFinalZoneAsync(room, publication, FinalZoneBackpackSpawned!, backpack, operationCt).ConfigureAwait(false);
        }, ct);

    private ValueTask ExecuteFinalZoneInventoryAsync(ISession session,
        Func<PlayRoom, RoomSeat, FinalZoneItemLedger, FinalZonePublication, CancellationToken, ValueTask> operation, CancellationToken ct,
        bool allowRelay = false)
    {
        if (_registry.TryGet(session.Id) is not { } room || !IsFinalZoneMode(room)) return ValueTask.CompletedTask;
        FinalZonePublication publication;
        lock (room.Gate)
        {
            if (!FinalZoneReportIsCurrent(room, session, room.StartGeneration)
                || room.Seats.FirstOrDefault(seat => ReferenceEquals(seat.Game, session)
                    || allowRelay && ReferenceEquals(seat.Relay, session)) is not { } owner
                || room.FinalZone is not { Items: { } ledger } state) return ValueTask.CompletedTask;
            publication = new(state, ledger, [.. room.Seats], owner, session, owner.Life.Generation);
        }
        return new(ExecuteAsync());

        async Task ExecuteAsync()
        {
            try
            {
                await room.Executor.ExecuteAsync(async operationCt =>
                {
                    ValueTask pending;
                    lock (room.Gate)
                    {
                        if (!OwnsFinalZonePublication(room, publication)
                            || !FinalZonePublicationActorIsCurrent(room, publication)
                            || !FinalZoneReportIsCurrent(room, session, publication.State.Generation)) return;
                        RequireFinalZonePublication(room, publication, operationCt);
                        pending = operation(room, publication.Actor!, publication.Ledger!, publication, operationCt);
                    }
                    await pending.ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await CancelFinalZonePublicationAsync(room, publication).ConfigureAwait(false);
                throw;
            }
        }
    }
}
