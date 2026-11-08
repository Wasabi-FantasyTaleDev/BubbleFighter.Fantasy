using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private sealed class CancelTriggerDuringSettlement(MemoryPlayerProgressRepository store,
        CancellationTokenSource trigger) : IPlayerBattleRecordsRepository
    {
        public bool IsDurable => false;
        public ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct) =>
            store.ListBattleRecordsAsync(userNum, ct);
        public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct) => store.FindAsync(userNum, ct);
        public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
        {
            trigger.Cancel();
            return store.SettleRoundAsync(command, ct);
        }
    }

    private sealed class LostBattleAcknowledgement(MemoryPlayerProgressRepository store) : IPlayerBattleRecordsRepository
    {
        private readonly HashSet<long> _lost = [];
        public bool IsDurable => false;
        public ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct) =>
            store.ListBattleRecordsAsync(userNum, ct);
        public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct) => store.FindAsync(userNum, ct);
        public async ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
        {
            var result = await store.SettleRoundAsync(command, ct);
            lock (_lost)
                if (_lost.Add(command.UserNum)) throw new TimeoutException("Committed response was lost.");
            return result;
        }
    }

    private static async Task<MemoryPlayerProgressRepository> BattleStore()
    {
        var table = new MemoryPlayerTable(8);
        var wallets = new MemoryPlayerWalletRepository(table);
        foreach (var user in new long[] { 11, 22, 33 })
            await wallets.UpsertAsync(new PlayerWallet(user, 0, 0), default);
        return new(table);
    }

    private static void BattleOptions(InGameOptions options)
    {
        options.RecordBattleStatistics = true;
        options.LevelExpThresholds = [0, 1000, 5000];
        options.MaxLevel = 3;
        options.RewardBaseExp = 150;
        options.RewardWinMultiplier = 1;
    }

    [Fact]
    public async Task BattleRecordingRequiresAnExplicitCapableStore()
    {
        var (context, _, _) = ResultContext(new FakeProgress([]));
        try { Assert.Throws<ConfigException>(() => new Rig(resultContext: context, configure: BattleOptions)); }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task TerminalRecordsSurviveImmediateResultDepartureAndRepeatedEndRequests()
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 3000, configure: BattleOptions);
        try
        {
            var room = await StartedRoundAsync(rig);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            host.Team = 1;
            guest.Team = 2;
            await rig.Round.HandleKillCreditReportedAsync(rig.Host,
                new(host.Slot, guest.Slot, 1) { RequiresReporterOwnership = true }, default);
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                new(host.Slot) { Victim = guest.Slot, HasVictimReference = true, RequiresReporterOwnership = true }, default);
            var source = room.Scoring.Stats(host.Slot);
            Assert.Equal(1, source.Kills);
            Assert.Equal(1, source.Knockdowns);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            var won = Assert.Single((await store.ListBattleRecordsAsync(11, default))!);
            Assert.Equal((1L, 1L, 1L, (long)source.Score), (won.Wins, won.Kills, won.Knockdowns, won.Score));
            Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(22, default))!).Losses);
            var exp = (await store.FindAsync(11, default))!.Exp;
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(won, Assert.Single((await store.ListBattleRecordsAsync(11, default))!));
            Assert.Equal(exp, (await store.FindAsync(11, default))!.Exp);
            Assert.Equal(0, Assert.Single((await store.ListBattleRecordsAsync(22, default))!).Escapes);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveDepartureRecordsOneEscapeBeforeMasterTransfer(bool masterLeaves)
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, configure: BattleOptions);
        try
        {
            var room = await StartedRoundAsync(rig);
            var leaving = masterLeaves ? rig.Host : rig.Guest;
            var remaining = masterLeaves ? rig.Guest : rig.Host;
            await rig.Rooms.HandleLeaveAsync(leaving, new(), default);
            var record = Assert.Single((await store.ListBattleRecordsAsync(leaving.Id, default))!);
            Assert.Equal((1L, 1L, 0L, 0L), (record.Matches, record.Escapes, record.Wins, record.Losses));
            Assert.Equal(0, (await store.FindAsync(leaving.Id, default))!.Exp);
            await rig.Round.HandleEndRequestedAsync(remaining, new(), default);
            Assert.Equal(record, Assert.Single((await store.ListBattleRecordsAsync(leaving.Id, default))!));
            Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(remaining.Id, default))!).Draws);
            Assert.Equal(RoomPhase.Result, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task LoadingDepartureDoesNotFabricateAPlayedMatch()
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, configure: BattleOptions);
        try
        {
            await rig.StartAsync();
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Null(await store.ListBattleRecordsAsync(22, default));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task ResultSnapshotRetainsDepartedAccountsForSettlementRetry()
    {
        var store = await BattleStore();
        var (context, raised, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, configure: options =>
        {
            BattleOptions(options);
            options.LevelExpThresholds = [0, 100, 5000];
        });
        try
        {
            var room = await StartedRoundAsync(rig);
            room.MarkResult();
            var first = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            rig.Registry.Detach(rig.Guest.Id);
            var retry = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            Assert.Equal(first.Count, retry.Count);
            Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(22, default))!).Matches);
            Assert.Equal(2, raised.Count);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task LostAcknowledgementRetriesKeepOneBattleAndOneRewardPerAccount()
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(new LostBattleAcknowledgement(store));
        var rig = new Rig(resultContext: context, configure: BattleOptions);
        try
        {
            var room = await StartedRoundAsync(rig);
            room.MarkResult();
            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            Assert.Equal(2, rewards.Count);
            foreach (var user in new long[] { 11, 22 })
            {
                Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(user, default))!).Matches);
                Assert.Equal(150, (await store.FindAsync(user, default))!.Exp);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task CancelledTriggerDoesNotAbandonADecidedTerminalRoster()
    {
        var store = await BattleStore();
        using var trigger = new CancellationTokenSource();
        var (context, _, _) = ResultContext(new CancelTriggerDuringSettlement(store, trigger));
        var rig = new Rig(resultContext: context, configure: BattleOptions);
        try
        {
            var room = await StartedRoundAsync(rig);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), trigger.Token);
            Assert.True(trigger.IsCancellationRequested);
            foreach (var user in new long[] { 11, 22 })
                Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(user, default))!).Matches);
            Assert.True(room.HasResultSequence);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task BombSeriesRecordsOnlyTheFinalMatchWithCumulativeCounters()
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options =>
        {
            BattleOptions(options);
            options.BombRoundSeries = true;
            options.ResultCloseGraceMs = 1;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var match = room.MatchId;
            for (var round = 0; round < 2; round++)
            {
                await rig.Round.HandleKillCreditReportedAsync(rig.Host,
                    new(room.FindByGame(rig.Host.Id)!.Slot, room.FindByGame(rig.Guest.Id)!.Slot, 1)
                        { RequiresReporterOwnership = true }, default);
                await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
                await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(match, room.MatchId);
                if (round == 0)
                {
                    Assert.Null(await store.ListBattleRecordsAsync(11, default));
                    Assert.Null(await store.ListBattleRecordsAsync(22, default));
                    await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
                    await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
                    await WaitBombPhaseAsync(room, RoomPhase.Playing);
                }
            }
            var record = Assert.Single((await store.ListBattleRecordsAsync(11, default))!);
            Assert.Equal((1L, 1L, 2L), (record.Matches, record.Wins, record.Knockdowns));
            Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(22, default))!).Losses);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Fact]
    public async Task LeavingDuringBombReloadRetainsTheAlreadyPlayedSeries()
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options =>
        {
            BattleOptions(options);
            options.BombRoundSeries = true;
            options.ResultCloseGraceMs = 1;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(1, room.BombSeries!.RoundIndex);
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            var departed = Assert.Single((await store.ListBattleRecordsAsync(22, default))!);
            Assert.Equal((1L, 1L, 0L), (departed.Matches, departed.Escapes, departed.Losses));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BombForfeitRecordsTheWinnerBeforeAnImmediateFinalDeparture(bool duringReload)
    {
        var store = await BattleStore();
        var (context, _, _) = ResultContext(store);
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: duringReload ? 0 : 3000, configure: options =>
        {
            BattleOptions(options);
            options.BombRoundSeries = true;
            options.ResultCloseGraceMs = 1;
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleBombMissionOverAsync(rig.Host, await CompleteBombObjectiveAsync(rig, 1), default);
            if (duringReload)
            {
                await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(RoomPhase.Starting, room.Phase);
            }
            else
            {
                for (var attempt = 0; attempt < 200 && boards.To(rig.Host).Length == 0; attempt++) await Task.Delay(10);
                Assert.Single(boards.To(rig.Host));
                Assert.False(room.ResultTask.IsCompleted);
            }
            Assert.Null(await store.ListBattleRecordsAsync(11, default));
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.True(room.BombSeries!.Forfeited);
            var winner = Assert.Single((await store.ListBattleRecordsAsync(11, default))!);
            Assert.Equal((1L, 1L, 0L), (winner.Matches, winner.Wins, winner.Escapes));
            Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(22, default))!).Escapes);
            var exp = (await store.FindAsync(11, default))!.Exp;
            await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(winner, Assert.Single((await store.ListBattleRecordsAsync(11, default))!));
            Assert.Equal(exp, (await store.FindAsync(11, default))!.Exp);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }
}
