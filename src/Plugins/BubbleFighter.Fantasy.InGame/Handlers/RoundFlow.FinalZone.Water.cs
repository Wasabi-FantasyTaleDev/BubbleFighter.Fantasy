using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundFinalZoneWaterTimerChanged>? FinalZoneWaterTimer { get; set; }

    private async ValueTask StartFinalZoneWaterAsync(PlayRoom room)
    {
        var previous = room.FinalZoneWaterClock;
        if (previous is not null)
        {
            previous.Cancel();
            await previous.Completion.ConfigureAwait(false);
        }
        if (room.FinalZone is not { WaterRules: { Enabled: true } rules } state) return;
        var cycle = new FinalZoneWaterCycle(rules);
        var stopAt = Environment.TickCount64 + rules.StopMs;
        room.FinalZoneWaterClock = new(room.Executor, _lifetime,
            failure => _logger.LogError(failure, "Room {Room}: FinalZone water phase failed.", room.Id));
        room.FinalZoneWaterClock.Schedule(cycle.InitialDelayMs, ct => AdvanceAsync(ct));

        async ValueTask AdvanceAsync(CancellationToken ct)
        {
            lock (room.Gate)
            {
                if (room.Phase != RoomPhase.Playing || room.RoundCancelled || room.IsStopping
                    || !ReferenceEquals(room.FinalZone, state) || state.Generation != room.StartGeneration || state.Finished)
                {
                    room.FinalZoneWaterClock?.Cancel();
                    return;
                }
            }
            if (state.WaterPhase != 0)
                await PublishFinalZoneAsync(room, FinalZoneWaterTimer!,
                    new(false, state.WaterPhase, 0), ct).ConfigureAwait(false);
            var next = Environment.TickCount64 < stopAt ? cycle.Next() : null;
            if (next is null)
            {
                room.FinalZoneWaterClock?.Cancel();
                return;
            }
            var duration = (int)Math.Min(next.DurationMs, Math.Max(1, stopAt - Environment.TickCount64));
            next = next with { DurationMs = duration };
            await PublishFinalZoneAsync(room, FinalZoneWaterTimer!, next, ct).ConfigureAwait(false);
            state.WaterPhase = next.Stage;
            room.FinalZoneWaterClock!.Schedule(duration, AdvanceAsync);
        }
    }
}
