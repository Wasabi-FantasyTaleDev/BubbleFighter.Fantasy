using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A bomb state report from a seated player reaches every member of the bomb round, sender included.</summary>
    [Fact]
    public async Task BombStateIsDeliveredToEveryMember()
    {
        var rig = new Rig();
        try
        {
            await StartBombRoundAsync(rig);
            var bomb = new Sink<RoundBombStateChanged>();

            await rig.Round.HandleBombStateAsync(rig.Host,
                new RoundBombStateReported(RoundBombState.Planting, 11, 3.5f, -1.25f, 42.0f), bomb, default);

            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var change = Assert.Single(bomb.To(member));
                Assert.Equal(RoundBombState.Planting, change.State);
                Assert.Equal(11, change.Operator);
                Assert.Equal(3.5f, change.PositionX);
                Assert.Equal(-1.25f, change.PositionY);
                Assert.Equal(42.0f, change.PositionZ);
                Assert.False(change.TimerAligned);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Forged, duplicate and premature transitions neither change the bomb nor reach any room member.</summary>
    [Fact]
    public async Task BombStateAdmissionPrecedesEveryPublication()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var bomb = new Sink<RoundBombStateChanged>();
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Exploded, 11, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Guest, new(RoundBombState.Planting, 11, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Guest, new(RoundBombState.Planting, 22, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 43, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, float.NaN, 0, 0), bomb, default);
            Assert.Empty(bomb.To(rig.Host));
            Assert.Empty(bomb.To(rig.Guest));
            Assert.Equal(RoundBombState.Reset, room.Bomb.State);

            var planting = new RoundBombStateReported(RoundBombState.Planting, 11, 3.5f, -1.25f, 42);
            await rig.Round.HandleBombStateAsync(rig.Host, planting, bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, planting, bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Guest, planting with { State = RoundBombState.Countdown }, bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, planting with { State = RoundBombState.Countdown, PositionZ = 43 }, bomb, default);
            Assert.Single(bomb.To(rig.Host));
            Assert.Single(bomb.To(rig.Guest));
            Assert.Equal(RoundBombState.Planting, room.Bomb.State);
            Assert.Null(room.Bomb.CountdownStartedAtMilliseconds);

            await rig.Round.HandleBombStateAsync(rig.Host, planting with { State = RoundBombState.Countdown }, bomb, default);
            var armedAt = room.Bomb.CountdownStartedAtMilliseconds;
            await rig.Round.HandleBombStateAsync(rig.Host, planting with { State = RoundBombState.Countdown }, bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Guest, planting with { State = RoundBombState.Defusing, Operator = 22 }, bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Guest, planting with { State = RoundBombState.DefuseInterrupted, Operator = 22 }, bomb, default);
            Assert.Equal(armedAt, room.Bomb.CountdownStartedAtMilliseconds);
            Assert.Equal(RoundBombState.Countdown, room.Bomb.State);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(new[] { RoundBombState.Planting, RoundBombState.Countdown, RoundBombState.Defusing,
                    RoundBombState.DefuseInterrupted }, bomb.To(member).Select(change => change.State));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The shared match reset clears bomb ownership and rejects a previous round's completion.</summary>
    [Fact]
    public async Task BombStateIsResetAtTheNextMatchStart()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var bomb = new Sink<RoundBombStateChanged>();
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), bomb, default);
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                room.MarkPlaying();
            }
            Assert.Equal(RoundBombState.Reset, room.Bomb.State);
            Assert.Equal(-1, room.Bomb.Planter);
            Assert.Null(room.Bomb.CountdownStartedAtMilliseconds);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), bomb, default);
            Assert.Equal(2, bomb.To(rig.Host).Length);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), bomb, default);
            Assert.Equal(3, bomb.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A room of another mode never relays a bomb state report.</summary>
    [Fact]
    public async Task BombStateIsRefusedOutsideBombMode()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var bomb = new Sink<RoundBombStateChanged>();

            await rig.Round.HandleBombStateAsync(rig.Host,
                new RoundBombStateReported(RoundBombState.Planting, 11, 0f, 0f, 0f), bomb, default);

            Assert.Empty(bomb.To(rig.Host));
            Assert.Empty(bomb.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The master's objective winner overrides tied or opposing kill totals and cannot be replaced by a duplicate.</summary>
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(1, 0, 9)]
    [InlineData(2, 9, 0)]
    public async Task BombMissionOverPreservesWinner(int winner, int firstKills, int secondKills)
    {
        var rig = new Rig(configure: options => options.ResultCloseGraceMs = 60000);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            lock (room.Gate)
            {
                room.FindByGame(rig.Host.Id)!.Team = 1;
                room.FindByGame(rig.Guest.Id)!.Team = 2;
                room.Scoring.TeamTotals[0] = firstKills;
                room.Scoring.TeamTotals[1] = secondKills;
            }

            var report = await CompleteBombObjectiveAsync(rig, winner);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report, default);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(3 - winner, 0, 0), default);
            Assert.Equal(winner, room.DecidedWinnerTeam);

            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var ended = Assert.Single(rig.Ended.To(member));
                Assert.Equal(RoundEndReason.WinCondition, ended.Reason);
                Assert.Equal(room.FindByGame(member.Id)!.Team == winner ? RoundOutcome.Won : RoundOutcome.Lost, ended.Outcome);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The native decided winner also controls typed scoreboard rows and the victory reward bonus.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BombWinnerReachesTypedResultsAndRewards(int winner)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "bomb-result", Plugin = "bomb-result", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var boards = new Sink<RoundScoreboard>();
        var ended = new Sink<RoundEnded>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(boards));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(ended));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 60000, configure: options =>
        {
            options.LevelExpThresholds = [0, 1000, 5000];
            options.MaxLevel = 3;
            options.ScoreBasedRewards = true;
            options.RewardCompletionExp = 10;
            options.RewardVictoryExp = 20;
            options.RewardCompletionLucci = 30;
            options.RewardVictoryLucci = 40;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            lock (room.Gate)
            {
                room.FindByGame(rig.Host.Id)!.Team = 1;
                room.FindByGame(rig.Guest.Id)!.Team = 2;
                room.Scoring.TeamTotals[2 - winner] = 9;
            }
            var report = await CompleteBombObjectiveAsync(rig, winner);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report, default);
            for (var i = 0; i < 200 && ended.To(rig.Guest).Length == 0; i++) await Task.Delay(10);
            var rewards = rig.Round.ComputeRewards(room, room.StartGeneration, RoundEndReason.WinCondition);
            var board = Assert.Single(boards.To(rig.Host));
            Assert.Equal(winner, board.WinnerTeam);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var seat = room.FindByGame(member.Id)!;
                var won = seat.Team == winner;
                var outcome = won ? RoundOutcome.Won : RoundOutcome.Lost;
                Assert.Equal(outcome, Assert.Single(ended.To(member)).Outcome);
                Assert.Equal(outcome, board.Rows.Single(row => row.Slot == seat.Slot).Outcome);
                Assert.Equal(won ? 30 : 10, rewards[seat.Slot].ExpEarned);
                Assert.Equal(won ? 70 : 30, rewards[seat.Slot].LucciEarned);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Only the room master may report the native mission result; loading cannot acquire a decided winner.</summary>
    [Fact]
    public async Task BombMissionOverRejectsGuestAndLoadingReports()
    {
        var rig = new Rig();
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var bomb = new Sink<RoundBombStateChanged>();
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), bomb, default);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), bomb, default);
            Assert.Equal(2, bomb.To(rig.Host).Length);
            await rig.Round.HandleBombStateAsync(rig.Guest, new(RoundBombState.Exploded, 11, 0, 0, 0), bomb, default);
            Assert.Equal(2, bomb.To(rig.Host).Length);
            await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Exploded, 11, 0, 0, 0), bomb, default);
            Assert.Equal(RoundBombState.Exploded, bomb.To(rig.Host)[2].State);
            await rig.Round.HandleBombMissionOverAsync(rig.Guest, new(1, 11, -1), default);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Host));
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
            }
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(1, 0, 0), default);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A report naming a team the mode never uses ends nothing.</summary>
    [Fact]
    public async Task BombMissionOverRejectsUnknownTeam()
    {
        var rig = new Rig();
        try
        {
            await StartBombRoundAsync(rig);

            await rig.Round.HandleBombMissionOverAsync(rig.Host, new RoundBombMissionOverReported(3, 0, 0), default);

            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Reports from outside the room, and after the round closed, change nothing.</summary>
    [Fact]
    public async Task BombReportsNeedAPlayingBombRoom()
    {
        var rig = new Rig();
        try
        {
            var roomId = await StartBombRoundAsync(rig);
            var bomb = new Sink<RoundBombStateChanged>();
            var outsider = new RecordingSession(44);
            outsider.SetLobbyPlayer(44, "outsider");

            await rig.Round.HandleBombStateAsync(outsider,
                new RoundBombStateReported(RoundBombState.Planting, 44, 0f, 0f, 0f), bomb, default);
            Assert.Empty(bomb.To(rig.Host));
            await rig.Round.HandleBombMissionOverAsync(outsider, new RoundBombMissionOverReported(1, 0, 0), default);
            Assert.Empty(rig.Ended.To(rig.Host));

            var room = rig.Registry.Find(roomId)!;
            lock (room.Gate)
                room.MarkResult();
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new RoundBombMissionOverReported(2, 0, 0), default);
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Even the master cannot end a live round with an invented objective or another operator's result.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BombMissionOverRejectsUnprovenObjectiveAndForgedReferences(int winner)
    {
        var rig = new Rig(configure: options => options.ResultCloseGraceMs = 60000);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, new(winner, -1, -1), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Host));
            var report = await CompleteBombObjectiveAsync(rig, winner);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report with { WinningTeam = 3 - winner }, default);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report with { Planter = report.Planter + 32 }, default);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report with { Defuser = report.Defuser + 32 }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report, default);
            Assert.Equal(winner, room.DecidedWinnerTeam);
            Assert.Single(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Pre-arm elimination ends a round, but attackers dying after arming do not defeat their bomb.</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task BombEliminationResultFollowsArmedState(bool armed, int eliminatedTeam)
    {
        var rig = new Rig(configure: options => options.ResultCloseGraceMs = 60000);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            if (armed)
            {
                var sink = new Sink<RoundBombStateChanged>();
                await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Planting, 11, 0, 0, 0), sink, default);
                await rig.Round.HandleBombStateAsync(rig.Host, new(RoundBombState.Countdown, 11, 0, 0, 0), sink, default);
            }
            lock (room.Gate) room.Seats.Single(seat => seat.Team == eliminatedTeam).Life.Die();
            var report = new RoundBombMissionOverReported(3 - eliminatedTeam, armed ? 11 : -1, -1);
            await rig.Round.HandleBombMissionOverAsync(rig.Host, report, default);
            if (armed && eliminatedTeam == 1)
            {
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Null(room.DecidedWinnerTeam);
                Assert.Empty(rig.Ended.To(rig.Host));
            }
            else
            {
                Assert.Equal(3 - eliminatedTeam, room.DecidedWinnerTeam);
                Assert.Single(rig.Ended.To(rig.Host));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static async Task<RoundBombMissionOverReported> CompleteBombObjectiveAsync(Rig rig, int winner)
    {
        var room = rig.Registry.TryGet(rig.Host.Id)!;
        var planter = room.Seats.Single(seat => seat.Team == room.Bomb.AttackingTeam);
        var defuser = room.Seats.Single(seat => seat.Team == room.Bomb.DefendingTeam);
        var master = room.Seats.Single(seat => seat.Slot == room.MasterSlot);
        var sink = new Sink<RoundBombStateChanged>();
        await rig.Round.HandleBombStateAsync(planter.Game, new(RoundBombState.Planting, planter.UserNum, 0, 0, 0), sink, default);
        await rig.Round.HandleBombStateAsync(planter.Game, new(RoundBombState.Countdown, planter.UserNum, 0, 0, 0), sink, default);
        if (winner == room.Bomb.AttackingTeam)
        {
            await rig.Round.HandleBombStateAsync(master.Game, new(RoundBombState.Exploded, planter.UserNum, 0, 0, 0), sink, default);
            Assert.Equal(RoundBombState.Exploded, sink.To(rig.Host)[2].State);
            return new(winner, planter.UserNum, -1);
        }
        await rig.Round.HandleBombStateAsync(defuser.Game, new(RoundBombState.Defusing, defuser.UserNum, 0, 0, 0), sink, default);
        await rig.Round.HandleBombStateAsync(defuser.Game, new(RoundBombState.Defused, defuser.UserNum, 0, 0, 0), sink, default);
        Assert.Equal(RoundBombState.Defused, sink.To(rig.Host)[3].State);
        return new(winner, -1, defuser.UserNum);
    }

    /// <summary>Creates a two-player round of the bomb mission (mode 19), plays it into the playing phase, and returns the room id.</summary>
    private static async Task<int> StartBombRoundAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("bomb room", "", 19, "map"), CancellationToken.None);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), CancellationToken.None);
        var room = rig.Registry.Find(roomId)!;
        lock (room.Gate)
        {
            room.FindByGame(rig.Host.Id)!.Team = 1;
            room.FindByGame(rig.Guest.Id)!.Team = 2;
        }
        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), CancellationToken.None);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), CancellationToken.None);
        Assert.Single(rig.Starting.To(rig.Guest));
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), CancellationToken.None);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), CancellationToken.None);
        await rig.WaitStartedAsync(rig.Guest);
        return roomId;
    }
}
