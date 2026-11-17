using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The selected map overrides the configured count and remains frozen after play starts.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task RacingUsesEveryNativeStageAndFreezesItsCount(int nativeRounds)
    {
        var rig = new Rig(configure: options =>
        {
            options.ExtremeRacingRounds = 3;
            options.ExtremeRacingMapRounds = true;
            options.ExtremeRacingRoundMs = 60_000;
            options.ExtremeRacingFinishCountdownMs = 60_000;
            options.ExtremeRacingIntermissionMs = ExtremeRacingPause;
        });
        var racing = new ExtremeRacingSinks();
        rig.Round.ExtremeRacing = racing.Senders;
        var store = new MapRoundStore($"[SavePoint]\nStageCount={nativeRounds}\n");
        rig.Round.ConfigureExtremeRacingMapRounds(store);
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            await WaitForAsync(() => racing.Timers.To(rig.Guest).Length == 1);
            store.Text = "[SavePoint]\nStageCount=1\n";
            var readsAtStart = store.Reads;
            for (var stage = 1; stage <= nativeRounds; stage++)
            {
                var expectedStage = stage;
                await WaitForAsync(() => room.ExtremeRacing.Round == expectedStage && room.ExtremeRacing.IsActive);
                await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(stage), default);
                await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(stage), default);
                if (stage < nativeRounds)
                {
                    Assert.Empty(rig.Ended.To(rig.Host));
                    Assert.Equal(ExtremeRacingPhase.Intermission, room.ExtremeRacing.Phase);
                }
            }
            await WaitForAsync(() => rig.Ended.To(rig.Host).Length == 1);
            Assert.Equal(nativeRounds, racing.Records.To(rig.Host).Length);
            Assert.Equal(nativeRounds, racing.Timers.To(rig.Host).Count(timer =>
                timer.Timer == RoundExtremeRacingTimer.RaceRunning && timer.Active));
            Assert.Equal(readsAtStart, store.Reads);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A map's timeout advances through its own stages and restarts the full race clock each time.</summary>
    [Fact]
    public async Task RacingMapTimeoutAdvancesAndRestartsTheClock()
    {
        var rig = new Rig(configure: options =>
        {
            options.ExtremeRacingRounds = 3;
            options.ExtremeRacingMapRounds = true;
            options.ExtremeRacingRoundMs = 300;
            options.ExtremeRacingFinishCountdownMs = 60_000;
            options.ExtremeRacingIntermissionMs = ExtremeRacingPause;
        });
        var racing = new ExtremeRacingSinks();
        rig.Round.ExtremeRacing = racing.Senders;
        rig.Round.ConfigureExtremeRacingMapRounds(new MapRoundStore("[SavePoint]\nStageCount=2\n"));
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
        try
        {
            await StartExtremeRacingRoundAsync(rig);
            await WaitForAsync(() => racing.Rounds.To(rig.Host).Length == 1);
            Assert.Empty(rig.Ended.To(rig.Host));
            await WaitForAsync(() => rig.Ended.To(rig.Host).Length == 1);
            Assert.Equal(new[] { 2 }, racing.Rounds.To(rig.Host).Select(round => round.Round));
            Assert.Equal(new[] { 300, 300 }, racing.Timers.To(rig.Host)
                .Where(timer => timer.Timer == RoundExtremeRacingTimer.RaceRunning && timer.Active)
                .Select(timer => timer.DurationMs));
            Assert.Equal(2, racing.Records.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
