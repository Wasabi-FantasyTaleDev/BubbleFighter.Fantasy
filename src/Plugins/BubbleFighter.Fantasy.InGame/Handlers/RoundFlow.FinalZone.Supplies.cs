using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundFinalZoneDroneSpawned>? FinalZoneDroneSpawned { get; set; }
    internal IMessageSender<RoundFinalZoneDroneBoxSpawned>? FinalZoneDroneBoxSpawned { get; set; }
    internal IMessageSender<RoundFinalZoneGoldenSupplySpawned>? FinalZoneGoldenSupplySpawned { get; set; }

    internal ValueTask HandleFinalZoneDroneDestroyedAsync(ISession session, RoundFinalZoneDroneDestroyed request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || room.FinalZone?.Supplies is not { } supplies) return;
            RequireSupplyPosition(request.X, request.Y, request.Z);
            var seed = Random.Shared.Next();
            var id = ledger.TryDropDrone(request.ObjectId, supplies.Generate(seed));
            if (id is null) return;
            await PublishFinalZoneAsync(room, publication, FinalZoneDroneBoxSpawned!, new(request, id.Value, seed), operationCt).ConfigureAwait(false);
        }, ct);

    internal ValueTask HandleFinalZoneGoldenSupplyAsync(ISession session, RoundFinalZoneGoldenSupplyRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, publication, operationCt) =>
        {
            var state = room.FinalZone!;
            if (seat.Slot != room.MasterSlot || state.Supplies is not { } supplies
                || request.SpawnIndex <= 0 || request.SpawnIndex > _options.FinalZoneMaximumRows
                || state.GoldenSupplyIndexes.Count >= _options.FinalZoneMaximumGoldenSupplies
                || state.GoldenSupplyIndexes.Contains(request.SpawnIndex)) return;
            RequireSupplyPosition(request.X, request.Y, request.Z);
            var seed = Random.Shared.Next();
            var id = ledger.TryCreateSupply(supplies.Generate(seed));
            if (id is null) return;
            state.GoldenSupplyIndexes.Add(request.SpawnIndex);
            // kr-1256 sub_CDB7E0 invokes OnGoldenSupplySpawned only on receipt, including on the script master.
            await PublishFinalZoneAsync(room, publication, FinalZoneGoldenSupplySpawned!, new(request, id.Value, seed), operationCt).ConfigureAwait(false);
        }, ct);

    private async ValueTask StartFinalZoneDronesAsync(PlayRoom room, FinalZonePublication publication, CancellationToken cancellationToken)
    {
        RoomDeadlineClock? previous;
        lock (room.Gate)
        {
            RequireFinalZonePublication(room, publication, cancellationToken);
            previous = room.FinalZoneDroneClock;
            previous?.Cancel();
        }
        if (previous is not null)
            await previous.Completion.ConfigureAwait(false);
        lock (room.Gate) RequireFinalZonePublication(room, publication, cancellationToken);
        var state = publication.State;
        if (state is not { DroneRules: { Enabled: true } rules, Supplies: not null }) return;
        RoomDeadlineClock clock;
        lock (room.Gate)
        {
            RequireFinalZonePublication(room, publication, cancellationToken);
            clock = new(room.Executor, _lifetime, (Func<Exception, ValueTask>)(async failure =>
            {
                _logger.LogError(failure, "Room {Room}: FinalZone drone scheduling failed.", room.Id);
                await CancelFinalZonePublicationAsync(room, publication).ConfigureAwait(false);
            }));
            room.FinalZoneDroneClock = clock;
            clock.Schedule(Delay(rules.InitialSeconds), AppearAsync);
        }

        async ValueTask AppearAsync(CancellationToken ct)
        {
            int? id;
            FinalZonePublication stepPublication;
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || !OwnsFinalZonePublication(room, publication) || state.Finished)
                {
                    clock.Cancel();
                    return;
                }
                stepPublication = new(state, publication.Ledger, [.. room.Seats]);
                RequireFinalZonePublication(room, stepPublication, ct);
                var ledger = publication.Ledger!;
                id = ledger.DroneCount < _options.FinalZoneMaximumDrones ? ledger.TryCreateDrone() : null;
            }
            if (id is { } objectId)
                await PublishFinalZoneAsync(room, stepPublication, FinalZoneDroneSpawned!, new(objectId, Random.Shared.Next()), ct).ConfigureAwait(false);
            lock (room.Gate)
            {
                RequireFinalZonePublication(room, stepPublication, ct);
                clock.Schedule(Delay(rules.IntervalSeconds), AppearAsync);
            }
        }

        static int Delay(FinalZoneAmountRange range) => checked(Random.Shared.Next(range.Minimum, range.Maximum + 1) * 1000);
    }

    private static void RequireSupplyPosition(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new InvalidDataException("FinalZone supply positions must be finite.");
    }
}
