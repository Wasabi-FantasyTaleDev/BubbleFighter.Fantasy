using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    [Fact]
    public async Task TutorialScoresPublishOnlyTheirOwnLiveCountersAndResetOnReentry()
    {
        var rig = new Rig();
        var tutorials = new TutorialRoomFlow(rig.Registry);
        var scores = new Sink<TutorialScoreUpdated>();
        try
        {
            await tutorials.OpenAsync(rig.Host, new(15, "sin_mission_shotgun_lv_01", 300005,
                [300005], 0, 0, 90000, true), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            await tutorials.ScoreAsync(rig.Host, new(0, TutorialScoreEvent.Bubble), scores, default);
            Assert.Empty(scores.To(rig.Host));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await tutorials.ScoreAsync(rig.Guest, new(0, TutorialScoreEvent.Bubble), scores, default);
            await tutorials.ScoreAsync(rig.Host, new(1, TutorialScoreEvent.Bubble), scores, default);
            Assert.Empty(scores.To(rig.Host));
            foreach (var goal in new[] { TutorialScoreEvent.Bubble, TutorialScoreEvent.Bubble,
                TutorialScoreEvent.Pang, TutorialScoreEvent.Help })
                await tutorials.ScoreAsync(rig.Host, new(0, goal), scores, default);
            Assert.Equal(new TutorialScoreUpdated(0, "host", 2, 1, 1), scores.To(rig.Host)[^1]);
            Assert.Empty(scores.To(rig.Guest));
            Assert.All(room.Scoring.TeamTotals, total => Assert.Equal(0, total));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
            await tutorials.CloseAsync(rig.Host, new(), default);
            await tutorials.ScoreAsync(rig.Host, new(0, TutorialScoreEvent.Bubble), scores, default);
            Assert.Equal(4, scores.To(rig.Host).Length);
            await tutorials.OpenAsync(rig.Host, new(15, "sin_mission_shotgun_lv_01", 300005,
                [300005], 0, 0, 90000, true), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            for (var i = 0; i < 200 && rig.Started.To(rig.Host).Length < 2; i++) await Task.Delay(10);
            await tutorials.ScoreAsync(rig.Host, new(0, TutorialScoreEvent.Bubble), scores, default);
            Assert.Equal(new TutorialScoreUpdated(0, "host", 1, 0, 0), scores.To(rig.Host)[^1]);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Theory]
    [InlineData(103, true)]
    [InlineData(16, true)]
    [InlineData(15, false)]
    public async Task TutorialScoresCannotCreditOtherScenes(int mode, bool tutorial)
    {
        var rig = new Rig();
        var tutorials = new TutorialRoomFlow(rig.Registry);
        var scores = new Sink<TutorialScoreUpdated>();
        try
        {
            await tutorials.OpenAsync(rig.Host, new(mode, "map", 300005, [300005], 0, 0, 90000, true), default);
            var room = Assert.IsType<PlayRoom>(rig.Registry.TryGet(rig.Host.Id));
            lock (room.Gate)
            {
                room.MarkPlaying();
                if (!tutorial) room.Tutorial = null;
            }
            await tutorials.ScoreAsync(rig.Host, new(0, TutorialScoreEvent.Bubble), scores, default);
            Assert.Empty(scores.To(rig.Host));
            Assert.Equal(0, room.Scoring.Stats(0).Knockdowns);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
