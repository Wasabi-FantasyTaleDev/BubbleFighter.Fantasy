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
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
        {
            if (seat.Life.State != SeatLife.Alive || room.FinalZone?.Supplies is not { } supplies) return;
            RequireSupplyPosition(request.X, request.Y, request.Z);
            var seed = Random.Shared.Next();
            var id = ledger.TryDropDrone(request.ObjectId, supplies.Generate(seed));
            if (id is null) return;
            await PublishFinalZoneAsync(room, FinalZoneDroneBoxSpawned!, new(request, id.Value, seed), operationCt).ConfigureAwait(false);
        }, ct);

    internal ValueTask HandleFinalZoneGoldenSupplyAsync(ISession session, RoundFinalZoneGoldenSupplyRequested request,
        CancellationToken ct) => ExecuteFinalZoneInventoryAsync(session, async (room, seat, ledger, operationCt) =>
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
            await PublishFinalZoneAsync(room, FinalZoneGoldenSupplySpawned!, new(request, id.Value, seed), operationCt).ConfigureAwait(false);
        }, ct);

    private async ValueTask StartFinalZoneDronesAsync(PlayRoom room)
    {
        if (room.FinalZoneDroneClock is { } previous)
        {
            previous.Cancel();
            await previous.Completion.ConfigureAwait(false);
        }
        if (room.FinalZone is not { DroneRules: { Enabled: true } rules, Supplies: not null } state) return;
        var clock = new RoomDeadlineClock(room.Executor, _lifetime,
            failure => _logger.LogError(failure, "Room {Room}: FinalZone drone scheduling failed.", room.Id));
        room.FinalZoneDroneClock = clock;
        clock.Schedule(Delay(rules.InitialSeconds), AppearAsync);

        async ValueTask AppearAsync(CancellationToken ct)
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || !ReferenceEquals(room.FinalZone, state) || state.Generation != room.StartGeneration || state.Finished)
                {
                    clock.Cancel();
                    return;
                }
            }
            var ledger = state.Items!;
            if (ledger.DroneCount < _options.FinalZoneMaximumDrones && ledger.TryCreateDrone() is { } id)
                await PublishFinalZoneAsync(room, FinalZoneDroneSpawned!, new(id, Random.Shared.Next()), ct).ConfigureAwait(false);
            clock.Schedule(Delay(rules.IntervalSeconds), AppearAsync);
        }

        static int Delay(FinalZoneAmountRange range) => checked(Random.Shared.Next(range.Minimum, range.Maximum + 1) * 1000);
    }

    private static void RequireSupplyPosition(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new InvalidDataException("FinalZone supply positions must be finite.");
    }
}
