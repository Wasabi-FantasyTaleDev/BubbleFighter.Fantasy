using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A team's departure ends the whole match in loading, play, or inter-round results without manufacturing round wins.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task BombWholeTeamDepartureForfeitsTheMatchOnce(int stage, bool masterLeaves)
    {
        var progress = new FakeProgress(new Dictionary<long, PlayerProgress> { [11] = new(11, 1, 0), [22] = new(22, 1, 0) });
        var (context, _, _) = ResultContext(progress);
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var ended = (Sink<RoundEnded>)context.Services.GetAll<IMessageSender<RoundEnded>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: stage == 2 ? 1000 : 0,
            configure: options =>
            {
                options.BombRoundSeries = true;
                options.ResultCloseGraceMs = 1;
                options.LevelExpThresholds = [0, 1000, 5000];
                options.MaxLevel = 3;
            });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var leaving = masterLeaves ? rig.Host : rig.Guest;
            var remaining = masterLeaves ? rig.Guest : rig.Host;
            var winner = masterLeaves ? 2 : 1;
            if (stage == 1)
            {
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.BeginBombSeries();
                }
            }
            else if (stage == 2)
            {
                await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 3 - winner), default);
                for (var attempt = 0; attempt < 200 && boards.To(remaining).Length == 0; attempt++) await Task.Delay(10);
                Assert.Single(boards.To(remaining));
                Assert.Empty(progress.Commands);
            }
            var match = room.MatchId;
            var wins = room.BombSeries!.SnapshotWins();
            await rig.Rooms.HandleLeaveAsync(leaving, new(), default);
            Assert.Equal(winner, room.BombSeries.MatchWinner);
            Assert.True(room.BombSeries.Forfeited);
            if (stage == 1)
            {
                Assert.Equal(RoomPhase.Starting, room.Phase);
                await rig.Round.HandleLoadReportedAsync(remaining, new(), default);
                await WaitBombPhaseAsync(room, RoomPhase.Waiting);
            }
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Equal(wins, room.BombSeries.SnapshotWins());
            Assert.Single(rig.Starting.To(remaining));
            Assert.Single(ended.To(remaining));
            Assert.Equal(winner, boards.To(remaining)[^1].WinnerTeam);
            var command = Assert.Single(progress.Commands);
            Assert.Equal(match, command.MatchId);
            Assert.Equal(masterLeaves ? 22 : 11, command.UserNum);
            Assert.False(room.HasResultSequence);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Every best-of-three sequence reloads only non-final rounds and settles each player exactly once.</summary>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 1, 2)]
    public async Task BombSeriesUsesOneFinalSettlement(int first, int second, int third)
        => await VerifyBombSeriesSettlementAsync(2, third == 0 ? [first, second] : [first, second, third]);

    /// <summary>Native five-round formats retain the same match and settle only after the third win.</summary>
    [Theory]
    [InlineData(1, 1, 1, 0, 0)]
    [InlineData(2, 2, 2, 0, 0)]
    [InlineData(1, 2, 1, 1, 0)]
    [InlineData(1, 2, 1, 2, 1)]
    [InlineData(1, 2, 1, 2, 2)]
    public async Task FiveRoundSeriesUsesOneFinalSettlement(int first, int second, int third, int fourth, int fifth)
        => await VerifyBombSeriesSettlementAsync(3, new[] { first, second, third, fourth, fifth }.Where(winner => winner != 0).ToArray());

    private static async Task VerifyBombSeriesSettlementAsync(int roundsToWin, int[] winners)
    {
        var stored = new Dictionary<long, PlayerProgress> { [11] = new(11, 1, 0), [22] = new(22, 1, 0) };
        var progress = new FakeProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var stages = (Sink<ResultStageAdvanced>)context.Services.GetAll<IMessageSender<ResultStageAdvanced>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, roundsToWin: roundsToWin, configure: options =>
        {
            options.BombRoundSeries = true;
            options.ResultCloseGraceMs = 1;
            options.LevelExpThresholds = [0, 1000, 5000];
            options.MaxLevel = 3;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var match = room.MatchId;
            var generation = room.StartGeneration;
            var scoring = room.Scoring;
            var wins = new int[2];
            for (var index = 0; index < winners.Length; index++)
            {
                Assert.Equal(index, room.BombSeries!.RoundIndex);
                Assert.Equal(index % 2 + 1, room.Bomb.AttackingTeam);
                Assert.Equal(index, rig.Starting.To(rig.Host)[index].Series!.RoundIndex);
                var report = await CompleteBombObjectiveAsync(rig, winners[index]);
                await Task.WhenAll(
                    rig.Round.HandleBombMissionOverAsync(rig.Host, report, default).AsTask(),
                    rig.Round.HandleBombMissionOverAsync(rig.Host, report, default).AsTask(),
                    rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.TimeLimit }, default).AsTask());
                await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
                wins[winners[index] - 1]++;
                Assert.Equal(wins, room.BombSeries.SnapshotWins());
                Assert.Equal(match, room.MatchId);
                Assert.Same(scoring, room.Scoring);
                Assert.Equal(wins, boards.To(rig.Host)[^1].RoundWins);
                if (index == winners.Length - 1)
                {
                    Assert.Equal(RoomPhase.Waiting, room.Phase);
                    Assert.Equal(2, progress.Recorded.Count);
                    Assert.All(progress.Commands, command => Assert.Equal(match, command.MatchId));
                    Assert.Equal(new long[] { 11, 22 }, progress.Commands.Select(command => command.UserNum).Order());
                    Assert.Single(stages.To(rig.Host));
                    Assert.Equal(winners.Length, rig.Starting.To(rig.Host).Length);
                    Assert.Equal(winners[index], boards.To(rig.Host)[^1].WinnerTeam);
                }
                else
                {
                    Assert.Equal(RoomPhase.Starting, room.Phase);
                    Assert.Empty(progress.Recorded);
                    Assert.Empty(progress.Commands);
                    Assert.Empty(stages.To(rig.Host));
                    Assert.DoesNotContain(boards.To(rig.Host), board => board.WithRewards);
                    Assert.Equal(generation + index + 1, room.StartGeneration);
                    await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
                    await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
                    await WaitBombPhaseAsync(room, RoomPhase.Playing);
                }
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A normal timeout awards the defender before arming and cannot defeat a bomb whose own timer is running.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BombSeriesTimeoutRespectsTheArmedBomb(bool armed)
    {
        var (context, _, _) = ResultContext(new FakeProgress([]));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options => options.BombRoundSeries = true);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var sink = new Sink<RoundBombStateChanged>();
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default);
            if (armed)
                await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), sink, default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.TimeLimit }, default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(armed ? RoomPhase.Playing : RoomPhase.Starting, room.Phase);
            Assert.Equal(armed ? new[] { 0, 0 } : [0, 1], room.BombSeries!.SnapshotWins());
            Assert.Equal(armed, room.Bomb.IsArmed);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Canceling a non-final result prevents its retained continuation from reopening the scene.</summary>
    [Fact]
    public async Task CanceledBombResultDoesNotReloadTheScene()
    {
        var (context, _, _) = ResultContext(new FakeProgress([]));
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 60000, configure: options => options.BombRoundSeries = true);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
            for (var attempt = 0; attempt < 200 && boards.To(rig.Host).Length == 0; attempt++) await Task.Delay(10);
            Assert.Single(boards.To(rig.Host));
            lock (room.Gate) room.ResetToWaiting();
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Equal(0, room.BombSeries!.RoundIndex);
            Assert.Single(rig.Starting.To(rig.Host));
            Assert.False(room.HasResultSequence);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static async Task WaitBombPhaseAsync(PlayRoom room, RoomPhase phase)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            lock (room.Gate) if (room.Phase == phase) return;
            await Task.Delay(10);
        }
        Assert.Equal(phase, room.Phase);
    }

    /// <summary>A report queued by the previous scene cannot mutate a replacement scene with the same users and objective state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedBombReportCannotCrossTheRoundGeneration(bool missionOver)
    {
        var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var transition = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.MarkPlaying();
                    if (missionOver)
                    {
                        var planter = room.FindByGame(rig.Host.Id)!;
                        Assert.True(room.Bomb.TryApply(new(RoundBombState.Planting, 11, 0, 0, 0), planter, true, 0));
                        Assert.True(room.Bomb.TryApply(new(RoundBombState.Countdown, 11, 0, 0, 0), planter, true, 0));
                        Assert.True(room.Bomb.TryApply(new(RoundBombState.Exploded, 11, 0, 0, 0), planter, true, 60000));
                    }
                }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var sink = new Sink<RoundBombStateChanged>();
            var report = missionOver
                ? rig.Round.HandleBombMissionOverAsync(rig.Host, new(1, 11, -1), default).AsTask()
                : rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default).AsTask();
            Assert.False(report.IsCompleted);
            release.SetResult();
            await Task.WhenAll(transition, report).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(sink.To(rig.Host));
            Assert.Equal(missionOver ? RoundBombState.Exploded : RoundBombState.Reset, room.Bomb.State);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Room shutdown joins the retained result task and prevents a non-final result from reopening a scene.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BombSeriesShutdownJoinsTheResultTask(bool lastSeatLeaves)
    {
        var progress = new FakeProgress([]);
        var (context, _, _) = ResultContext(progress);
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 60000,
            configure: options => options.BombRoundSeries = true);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
            for (var attempt = 0; attempt < 200 && boards.To(rig.Host).Length == 0; attempt++) await Task.Delay(10);
            Assert.Single(boards.To(rig.Host));
            var result = room.ResultTask;
            Assert.False(result.IsCompleted);
            if (lastSeatLeaves)
            {
                await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
                await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
                Assert.Null(rig.Registry.Find(room.Id));
            }
            await rig.Registry.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.IsCompletedSuccessfully);
            Assert.False(room.HasResultSequence);
            Assert.Single(rig.Starting.To(rig.Host));
            Assert.Empty(progress.Commands);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A replacement master can report the saved planter's explosion, while the former master cannot decide the round.</summary>
    [Fact]
    public async Task BombSeriesUsesTheCurrentMasterWithoutReplacingThePlanter()
    {
        var (context, _, _) = ResultContext(new FakeProgress([]));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options => options.BombRoundSeries = true);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var sink = new Sink<RoundBombStateChanged>();
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), sink, default);
            lock (room.Gate)
            {
                Assert.True(room.TransferMaster(1));
                room.FindByGame(rig.Guest.Id)!.Life.Die();
            }
            await rig.Round.HandleBombStateAsync(rig.Guest, new(RoundBombState.Exploded, 11, 0, 0, 0), sink, default);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(1, 11, -1), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.BombSeries!.RoundWinner);
            await rig.Round.HandleBombMissionOverAsync(rig.Guest, new(1, 11, -1), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(new[] { 1, 0 }, room.BombSeries.SnapshotWins());
            Assert.Equal(1, room.MasterSlot);
            Assert.Equal(SeatLife.Alive, room.FindByGame(rig.Guest.Id)!.Life.State);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A subround reload retains the match receipt and counters but replaces every participant's loading and life state.</summary>
    [Fact]
    public async Task BombSubroundResetPreservesMatchAccountingAndRenewsLives()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            lock (room.Gate)
            {
                room.ResetToWaiting();
                var botAppearance = new RoomMemberView(2, 11, "bot", 1, 2, true, 0, [])
                    { Ai = new(0, 11, 1, 1) };
                Assert.True(room.PutBot(botAppearance));
                room.MarkStarting();
                room.BeginBombSeries();
                room.RoundMapKey = "same-map";
                room.RoundStageKey = "same-stage";
                room.MarkPlaying();
                room.BotAt(2)!.Life.Die();
                room.Scoring.AddKill(0, 1, 1);
                room.Scoring.RememberVictim(0, 1);
                foreach (var seat in room.Seats)
                {
                    seat.Life.Die();
                    seat.SceneReady = seat.RoundReady = seat.RoundInitialized = seat.ObjectAdded = true;
                    seat.EndpointAdvertised = seat.RelayConfirmed = seat.RelayInitialized = true;
                }
                var match = room.MatchId;
                var generation = room.StartGeneration;
                var scoring = room.Scoring;
                var series = room.BombSeries!;
                var keys = room.Seats.Select(seat => seat.Key).ToArray();
                Assert.True(series.TryCompleteRound(0, 2));
                room.DecidedWinnerTeam = 2;
                room.MarkResult();

                Assert.False(room.ContinueBombSeries(generation - 1));
                Assert.Equal(0, series.RoundIndex);
                Assert.True(room.ContinueBombSeries(generation));
                Assert.False(room.ContinueBombSeries(generation));
                Assert.Same(series, room.BombSeries);
                Assert.Same(scoring, room.Scoring);
                Assert.Equal(match, room.MatchId);
                Assert.Equal(generation + 1, room.StartGeneration);
                Assert.Equal(RoomPhase.Starting, room.Phase);
                Assert.Equal("same-map", room.RoundMapKey);
                Assert.Equal("same-stage", room.RoundStageKey);
                Assert.Equal(new[] { 0, 1 }, series.SnapshotWins());
                Assert.Equal(1, room.Scoring.Stats(0).Kills);
                Assert.Equal(1, room.Scoring.Stats(1).Deaths);
                Assert.Equal(new[] { 1, 0 }, room.Scoring.TeamTotals);
                Assert.Null(room.Scoring.TakeVictim(0));
                Assert.Null(room.DecidedWinnerTeam);
                Assert.Equal(2, room.Bomb.AttackingTeam);
                Assert.Equal(RoundBombState.Reset, room.Bomb.State);
                Assert.Same(botAppearance, room.BotAt(2)!.Appearance);
                Assert.Equal(SeatLife.Alive, room.BotAt(2)!.Life.State);
                foreach (var seat in room.Seats)
                {
                    Assert.Equal(SeatLife.Alive, seat.Life.State);
                    Assert.False(seat.SceneReady || seat.RoundReady || seat.RoundInitialized || seat.ObjectAdded
                        || seat.EndpointAdvertised || seat.RelayConfirmed || seat.RelayInitialized);
                    Assert.NotEqual(keys[seat.Slot], seat.Key);
                }
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Continuation cannot consume a win or reload the scene until the retained result sequence releases ownership.</summary>
    [Fact]
    public async Task BombSubroundWaitsForResultCancellationOwnership()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            CancellationTokenSource cancellation;
            int generation;
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                room.BeginBombSeries();
                room.MarkPlaying();
                Assert.True(room.BombSeries!.TryCompleteRound(0, 1));
                room.MarkResult();
                generation = room.StartGeneration;
                cancellation = room.BeginResultSequence(default);
                Assert.Throws<InvalidOperationException>(() => room.ContinueBombSeries(generation));
                Assert.Equal(0, room.BombSeries.RoundIndex);
                Assert.Equal(RoomPhase.Result, room.Phase);
            }
            await room.ReleaseResultSequenceAsync(cancellation);
            lock (room.Gate)
            {
                Assert.True(room.ContinueBombSeries(generation));
                room.MarkPlaying();
                Assert.True(room.BombSeries!.TryCompleteRound(1, 1));
                room.MarkResult();
                Assert.False(room.ContinueBombSeries(room.StartGeneration));
                Assert.Equal(1, room.BombSeries.MatchWinner);
                var completedMatch = room.MatchId;
                room.ResetToWaiting();
                room.MarkStarting();
                Assert.Null(room.BombSeries);
                Assert.NotEqual(completedMatch, room.MatchId);
                Assert.Equal(0, room.Scoring.Stats(0).Kills);
                Assert.Equal(1, room.Bomb.AttackingTeam);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
