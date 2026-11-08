using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks the native four-phase order, shortening floor and finite map-wide stop deadline.</summary>
public sealed class FinalZoneWaterCycleTests
{
    /// <summary>The alarm shortens between complete waves while escape, recession and rise retain their own intervals.</summary>
    [Fact]
    public void WaterCycleShortensOnlyAlarmAndStopsAtMapDeadline()
    {
        var cycle = new FinalZoneWaterCycle(new(true, 10, 100, 30, 50, 20, 30, 40, 640));
        Assert.Equal(10, cycle.InitialDelayMs);
        var phases = new List<(int Stage, int Duration)>();
        for (var next = cycle.Next(); next is not null; next = cycle.Next())
            phases.Add((next.Stage, next.DurationMs));
        Assert.Equal(new[] { 100, 70, 50, 50 }, phases.Where(phase => phase.Stage == 1).Select(phase => phase.Duration));
        Assert.Equal(new[] { 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4, 1, 2, 3, 4 }, phases.Select(phase => phase.Stage));
        Assert.Equal(630, phases.Sum(phase => phase.Duration));
        Assert.Equal(40, phases[^1].Duration);
        Assert.Null(cycle.Next());
    }

    /// <summary>A disabled map emits no water phase; a stop during an interval clamps its final duration.</summary>
    [Fact]
    public void DisabledWaterAndPartialFinalPhaseDoNotRunBeyondMapLimit()
    {
        Assert.Null(new FinalZoneWaterCycle(new(false, 0, 0, 0, 0, 0, 0, 0, 0)).Next());
        var cycle = new FinalZoneWaterCycle(new(true, 10, 100, 0, 0, 20, 30, 40, 125));
        Assert.Equal(100, cycle.Next()!.DurationMs);
        Assert.Equal(15, cycle.Next()!.DurationMs);
        Assert.Null(cycle.Next());
    }
}
