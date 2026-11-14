using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoundFlow
{
    internal IMessageSender<RoundFinalZoneWaterTimerChanged>? FinalZoneWaterTimer { get; set; }

    private async ValueTask StartFinalZoneWaterAsync(PlayRoom room, FinalZonePublication publication, CancellationToken cancellationToken)
    {
        RoomDeadlineClock? previous;
        lock (room.Gate)
        {
            RequireFinalZonePublication(room, publication, cancellationToken);
            previous = room.FinalZoneWaterClock;
            previous?.Cancel();
        }
        if (previous is not null)
            await previous.Completion.ConfigureAwait(false);
        lock (room.Gate) RequireFinalZonePublication(room, publication, cancellationToken);
        var state = publication.State;
        if (state.WaterRules is not { Enabled: true } rules) return;
        var cycle = new FinalZoneWaterCycle(rules);
        var stopAt = Environment.TickCount64 + rules.StopMs;
        RoomDeadlineClock clock;
        lock (room.Gate)
        {
            RequireFinalZonePublication(room, publication, cancellationToken);
            clock = new(room.Executor, _lifetime, (Func<Exception, ValueTask>)(async failure =>
            {
                _logger.LogError(failure, "Room {Room}: FinalZone water phase failed.", room.Id);
                await CancelFinalZonePublicationAsync(room, publication).ConfigureAwait(false);
            }));
            room.FinalZoneWaterClock = clock;
            clock.Schedule(cycle.InitialDelayMs, AdvanceAsync);
        }

        async ValueTask AdvanceAsync(CancellationToken ct)
        {
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
            }
            if (state.WaterPhase != 0)
                await PublishFinalZoneAsync(room, stepPublication, FinalZoneWaterTimer!,
                    new(false, state.WaterPhase, 0), ct).ConfigureAwait(false);
            var next = Environment.TickCount64 < stopAt ? cycle.Next() : null;
            if (next is null)
            {
                clock.Cancel();
                return;
            }
            var duration = (int)Math.Min(next.DurationMs, Math.Max(1, stopAt - Environment.TickCount64));
            next = next with { DurationMs = duration };
            await PublishFinalZoneAsync(room, stepPublication, FinalZoneWaterTimer!, next, ct).ConfigureAwait(false);
            lock (room.Gate)
            {
                RequireFinalZonePublication(room, stepPublication, ct);
                state.WaterPhase = next.Stage;
                clock.Schedule(duration, AdvanceAsync);
            }
        }
    }
}
