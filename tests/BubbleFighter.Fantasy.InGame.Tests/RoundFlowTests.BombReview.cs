using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A reused numeric connection identity cannot operate the bomb or decide its result.</summary>
    [Fact]
    public async Task BombReportsRequireTheOwningConnection()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var sink = new Sink<RoundBombStateChanged>();
            var impostor = new RecordingSession(rig.Host.Id);
            impostor.SetLobbyPlayer(11, "impostor");
            await rig.Round.HandleBombStateAsync(impostor, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default);
            Assert.Equal(RoundBombState.Reset, room.Bomb.State);
            Assert.Empty(sink.To(rig.Guest));
            var result = await CompleteBombObjectiveAsync(rig, 1);
            await rig.Round.HandleBombMissionOverAsync(impostor, result, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Cancellation rejects both fresh and already queued native operation and result reports.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelledBombReportsCannotOperateOrSettle(bool queued, bool mission)
    {
        var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var sink = new Sink<RoundBombStateChanged>();
            var result = mission ? await CompleteBombObjectiveAsync(rig, 1) : new RoundBombMissionOverReported(1, -1, -1);
            if (!queued) lock (room.Gate) room.RoundCancelled = true;
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate) room.RoundCancelled = true;
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var report = mission
                ? rig.Round.HandleBombMissionOverAsync(rig.Host, result, default).AsTask()
                : rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default).AsTask();
            release.SetResult();
            await Task.WhenAll(blocker, report);
            Assert.Empty(sink.To(rig.Guest));
            Assert.Empty(rig.Ended.To(rig.Guest));
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Equal(mission ? RoundBombState.Exploded : RoundBombState.Reset, room.Bomb.State);
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Every automatic subround entry has its own loading deadline and cannot hang after a missing load report.</summary>
    [Fact]
    public async Task BombSubroundArmsANewLoadTimeout()
    {
        var (context, _, _) = ResultContext(new FakeProgress([]));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options =>
        {
            options.BombRoundSeries = true;
            options.LoadTimeoutMs = 1500;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var firstWatch = room.LoadTimeoutTask;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Starting, room.Phase);
            var secondWatch = room.LoadTimeoutTask;
            Assert.NotSame(firstWatch, secondWatch);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await secondWatch.WaitAsync(TimeSpan.FromSeconds(5));
            await room.RoundCancelTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RoomPhase.Closed, room.Phase);
            Assert.Single(rig.Left.To(rig.Host));
            Assert.Single(rig.Left.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }
}
