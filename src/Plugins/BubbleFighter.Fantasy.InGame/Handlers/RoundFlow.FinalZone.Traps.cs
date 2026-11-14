using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundFinalZoneTrapSpawned>? FinalZoneTrapSpawned { get; set; }
    internal IMessageSender<RoundFinalZoneTrapLinked>? FinalZoneTrapLinked { get; set; }

    internal ValueTask HandleFinalZoneTrapRequestedAsync(ISession session, RoundFinalZoneTrapRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || request.Actor != RoomRegistry.SceneEntity(seat.Slot)
                || ledger.TryPlaceTrap(request) is not { } spawned) return;
            // kr-1256 sub_501120 advances placement and emits the endpoint link only after this acknowledgement.
            await PublishFinalZoneAsync(room, publication, FinalZoneTrapSpawned!, spawned, operationCt).ConfigureAwait(false);
        }, ct);

    internal ValueTask HandleFinalZoneTrapLinkedAsync(ISession session, RoundFinalZoneTrapLinked request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || request.Actor != RoomRegistry.SceneEntity(seat.Slot)
                || !ledger.TryLinkTrap(request)) return;
            // kr-1256 sub_6FBDF0 creates the linked trap and resets its local owner's placement state on receipt.
            await PublishFinalZoneAsync(room, publication, FinalZoneTrapLinked!, request, operationCt).ConfigureAwait(false);
        }, ct, allowRelay: true);
}
