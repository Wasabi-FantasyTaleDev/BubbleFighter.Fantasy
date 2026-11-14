using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Preparation observes copied opening equipment and separate completion and collected credits outside the room gate.</summary>
    [Fact]
    public async Task PreparedRewardsUseOpeningEquipmentAndSeparatePickupCoins()
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        uint[] words = [5, 0, 0, 0];
        var seen = new ConcurrentQueue<RoundRewardPreparing>();
        try
        {
            var room = CreateRewardRoom(rig, seat =>
            {
                seat.EquippedItemIds = [301234];
                seat.ItemAttributes = [new(301234, 99, words, 1, "original")];
            });
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                Assert.False(Monitor.IsEntered(room.Gate));
                seen.Enqueue(request);
                var amounts = await next(ct);
                return amounts with { Experience = amounts.Experience * 2, CompletionLucci = amounts.CompletionLucci * 3 };
            }));
            lock (room.Gate)
            {
                words[0] = 999;
                foreach (var seat in room.Seats)
                {
                    seat.EquippedItemIds = [1234];
                    seat.ItemAttributes = [];
                    room.Scoring.AddLucci(seat.Slot, 7, 100);
                }
                room.MarkResult();
            }
            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.TimeLimit, default);
            Assert.Equal(2, seen.Count);
            Assert.All(seen, request =>
            {
                Assert.Equal(("test/game", room.MatchId, 4, RoundEndReason.TimeLimit),
                    (request.Protocol, request.MatchId, request.ModeId, request.Reason));
                Assert.Equal(new RoundRewardAmounts(10, 10, 7), request.BaseReward);
                Assert.Equal(301234, Assert.Single(request.Equipment.WornItemIds));
                Assert.Equal(5u, Assert.Single(request.Equipment.ItemAttributes).Values[0]);
                Assert.Equal("original", Assert.Single(request.Equipment.ItemAttributes).Unknown32);
            });
            Assert.All(rewards.Values, reward => Assert.Equal((20, 37, 20L), (reward.ExpEarned, reward.LucciEarned, reward.Exp)));
            Assert.Equal(2, progress.Credits);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Bomb reloads keep their opening equipment while a new receipt captures the current equipment.</summary>
    [Fact]
    public async Task BombSubroundsKeepEquipmentUntilANewMatchStarts()
    {
        var rig = new Rig();
        try
        {
            var room = CreateRewardRoom(rig, seat => seat.EquippedItemIds = [301234], start: false);
            lock (room.Gate)
            {
                room.MarkStarting();
                room.BeginBombSeries();
                var match = room.MatchId;
                var original = room.Seats[0].RewardEquipment;
                room.MarkPlaying();
                Assert.True(room.BombSeries!.TryCompleteRound(0, 1));
                room.MarkResult();
                foreach (var seat in room.Seats) seat.EquippedItemIds = [301235];
                Assert.True(room.ContinueBombSeries(room.StartGeneration));
                Assert.Equal(match, room.MatchId);
                Assert.Same(original, room.Seats[0].RewardEquipment);
                Assert.Equal(301234, Assert.Single(room.Seats[0].RewardEquipment.WornItemIds));
                room.FreezeRewardEquipment(room.Seats[0]);
                Assert.Same(original, room.Seats[0].RewardEquipment);
                room.ResetToWaiting();
                room.MarkStarting();
                Assert.NotEqual(match, room.MatchId);
                Assert.NotSame(original, room.Seats[0].RewardEquipment);
                Assert.Equal(301235, Assert.Single(room.Seats[0].RewardEquipment.WornItemIds));
                Assert.Null(room.BattleSettlements);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A late-admitted seat freezes its own published attributes once and cannot overwrite an opening seat's view.</summary>
    [Fact]
    public async Task LateAdmissionFreezesEquipmentOnceForTheCurrentMatch()
    {
        var rig = new Rig();
        try
        {
            var room = CreateRewardRoom(rig, seat => seat.EquippedItemIds = [301234], guest: false);
            var hostSnapshot = room.Seats[0].RewardEquipment;
            var (_, guest) = rig.Registry.Join(rig.Guest, room.Id, 22, "guest", equippedItemIds: [301235]);
            guest.ItemAttributes = [new(301235, 98, new uint[] { 0, 0, 0, 0 }, 1, "late")];
            room.FreezeRewardEquipment(guest);
            var original = guest.RewardEquipment;
            guest.EquippedItemIds = [1234];
            guest.ItemAttributes = [];
            room.FreezeRewardEquipment(guest);
            Assert.Equal(room.MatchId, guest.RewardEquipmentMatchId);
            Assert.Equal(301235, Assert.Single(original.WornItemIds));
            Assert.Equal("late", Assert.Single(original.ItemAttributes).Unknown32);
            Assert.Same(original, guest.RewardEquipment);
            Assert.Same(hostSnapshot, room.Seats[0].RewardEquipment);
            room.MarkResult();
            Assert.Throws<InvalidOperationException>(() => room.FreezeRewardEquipment(guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Malformed opening data fails before changing the match identity or any seat's frozen snapshot.</summary>
    [Fact]
    public async Task InvalidOpeningEquipmentDoesNotPartlyStartTheMatch()
    {
        var rig = new Rig();
        try
        {
            var room = CreateRewardRoom(rig, start: false);
            room.Seats[1].ItemAttributes = [new(301234, 99, new uint[] { 0 }, 0, "")];
            Assert.Throws<InvalidDataException>(() => room.MarkStarting());
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Equal("", room.MatchId);
            Assert.Equal(0, room.StartGeneration);
            Assert.All(room.Seats, seat => Assert.Equal("", seat.RewardEquipmentMatchId));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Competing pure preparations use the first adopted complete plan even when the earlier call finishes later.</summary>
    [Fact]
    public async Task ConcurrentPreparationAdoptsOnePlanForEverySettlementAttempt()
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentDictionary<long, int>();
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                var attempt = attempts.AddOrUpdate(request.UserNum, 1, static (_, current) => current + 1);
                var amounts = await next(ct);
                if (request.UserNum == 11 && attempt == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return amounts with { Experience = attempt * 100 };
            }));
            var first = rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            release.SetResult();
            var original = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(second.Values, reward => Assert.Equal(200, reward.ExpEarned));
            Assert.Equal(second.OrderBy(pair => pair.Key), original.OrderBy(pair => pair.Key));
            Assert.Equal(2, progress.Credits);
            Assert.All(room.BattleSettlements!, plan => Assert.Equal(200, plan.Reward.ExpEarned));
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Preparation errors refuse one complete seat credit and retain that failure alongside the successful terminal plans.</summary>
    [Fact]
    public async Task FailedPreparationCannotFallBackToUnmodifiedOrPartialCredit()
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var calls = 0;
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                Interlocked.Increment(ref calls);
                var amounts = await next(ct);
                return request.UserNum == 11 ? amounts with { Experience = -1 } : amounts with { Experience = 20 };
            }));
            var first = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            var retry = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            Assert.Equal(20, Assert.Single(first).Value.ExpEarned);
            Assert.Equal(first, retry);
            Assert.Equal(2, calls);
            Assert.Equal(1, progress.Credits);
            Assert.Equal(new PlayerProgress(11, 1, 0), stored[11]);
            Assert.Single(room.BattleSettlements!, plan => plan.PreparationFailure is not null);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Cancellation, shutdown and a new match invalidate an in-flight preparation before any plan or credit is adopted.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StalePreparationCannotAdoptOrCreditItsOldMatch(int transition)
    {
        var progress = new ReceiptProgress(RewardStored());
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                if (request.UserNum == 11)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return await next(ct);
            }));
            var pending = rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (room.Gate)
            {
                if (transition == 0) room.RoundCancelled = true;
                else if (transition == 1) { room.ResetToWaiting(); room.MarkStarting(); room.MarkResult(); }
                else room.IsStopping = true;
            }
            release.SetResult();
            Assert.Empty(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(room.BattleSettlements);
            Assert.Empty(progress.Matches);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The caller's canceled preparation leaves no cached plan and performs no store operation.</summary>
    [Fact]
    public async Task PreparationCancellationLeavesNoSettlementPlan()
    {
        var progress = new ReceiptProgress(RewardStored());
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                if (request.UserNum == 11)
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                return await next(ct);
            }));
            var pending = rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(room.BattleSettlements);
            Assert.Empty(progress.Matches);
        }
        finally { cancellation.Cancel(); await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A replay displays the stored credit with level-floor normalization, including a receipt that changed no experience.</summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public async Task ReceiptReplayDisplaysCreditedExperienceInsteadOfThePreparedEstimate(bool unchanged, int expected)
    {
        var stored = RewardStored();
        stored[11] = new(11, 2, 0);
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            await progress.SettleRoundAsync(new(room.MatchId, 11, 4,
                before => unchanged ? before : before with { Exp = 103 }), default);
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (_, next, ct) =>
                (await next(ct)) with { Experience = 200 }));
            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            var host = rewards[room.FindByGame(rig.Host.Id)!.Slot];
            Assert.Equal(expected, host.ExpEarned);
            Assert.Equal(4, host.LucciEarned);
            Assert.Equal(unchanged ? 0L : 103L, host.Exp);
            Assert.Equal(2, progress.Credits);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Ordinary terminal plans retain departed accounts and successful displayed rewards without invoking preparation again.</summary>
    [Fact]
    public async Task OrdinaryRewardPlansSurviveDeparturesAndRetries()
    {
        var progress = new ReceiptProgress(RewardStored());
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var calls = 0;
        try
        {
            var room = CreateRewardRoom(rig);
            room.MarkResult();
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                Interlocked.Increment(ref calls);
                Assert.Empty(request.Equipment.ItemAttributes);
                return (await next(ct)) with { Experience = 20 };
            }));
            var first = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            rig.Registry.Detach(rig.Guest.Id);
            room.Seats[0].Level = 3;
            var retry = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);
            Assert.Equal(first.OrderBy(pair => pair.Key), retry.OrderBy(pair => pair.Key));
            Assert.Equal(2, calls);
            Assert.Equal(2, progress.Credits);
            Assert.Equal(2, progress.Matches.Count);
            Assert.Equal(2, room.BattleSettlements!.Count);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The terminal executor operation adopts and commits the full roster before a queued result departure can remove a seat.</summary>
    [Fact]
    public async Task OrdinaryEndFixesItsRewardRosterBeforeAnyQueuedDeparture()
    {
        var progress = new ReceiptProgress(RewardStored());
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context, rewardDelay: 3000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = CreateRewardRoom(rig);
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                if (request.UserNum == 11)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return (await next(ct)) with { Experience = 20 };
            }));
            var ending = rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.TimeLimit }, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var leaving = rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default).AsTask();
            Assert.False(leaving.IsCompleted);
            Assert.Equal(2, room.Seats.Count);
            release.SetResult();
            await ending.WaitAsync(TimeSpan.FromSeconds(5));
            await leaving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, progress.Credits);
            Assert.Equal(2, room.BattleSettlements!.Count);
            Assert.All(room.BattleSettlements!, plan => Assert.Equal(20, plan.SettledReward!.ExpEarned));
            Assert.Null(room.FindByGame(rig.Guest.Id));
            Assert.Equal(20L, (await progress.FindAsync(22, default))!.Exp);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Loading, active play, cancellation, tutorials and non-final bomb rounds never enter ordinary reward preparation.</summary>
    [Fact]
    public async Task NonterminalAndExcludedRoundsCannotPrepareRewards()
    {
        var progress = new ReceiptProgress(RewardStored());
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var calls = 0;
        try
        {
            var room = CreateRewardRoom(rig, start: false);
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (_, next, ct) =>
            {
                Interlocked.Increment(ref calls);
                return await next(ct);
            }));
            room.MarkStarting();
            Assert.Empty(await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default));
            room.BeginBombSeries();
            room.MarkPlaying();
            Assert.Empty(await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default));
            Assert.True(room.BombSeries!.TryCompleteRound(0, 1));
            room.MarkResult();
            Assert.Empty(await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.WinCondition, default));
            room.ResetToWaiting();
            room.MarkStarting();
            room.MarkResult();
            room.RoundCancelled = true;
            Assert.Empty(await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default));
            room.RoundCancelled = false;
            room.Tutorial = new(103, "tutorial", 300001, [300001], 0, 20, 99999000, false);
            Assert.Empty(await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default));
            Assert.Equal(0, calls);
            Assert.Empty(progress.Matches);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A player who leaves during play is absent from the later terminal reward roster.</summary>
    [Fact]
    public async Task PlayingDepartureDoesNotGainATerminalReward()
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        try
        {
            var room = CreateRewardRoom(rig);
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            room.MarkResult();
            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.TimeLimit, default);
            Assert.Single(rewards);
            Assert.Equal(1, progress.Credits);
            Assert.Equal(new PlayerProgress(22, 1, 0), stored[22]);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>A played bomb series settles the remaining winner before a second queued departure during results or reload.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryBombForfeitSettlesBeforeTheFinalDeparture(bool duringReload)
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = CreateRewardRoom(rig, start: false);
            lock (room.Gate)
            {
                room.Seats[0].Team = 1;
                room.Seats[1].Team = 2;
                room.MarkStarting();
                room.BeginBombSeries();
                room.MarkPlaying();
                Assert.True(room.BombSeries!.TryCompleteRound(0, 1));
                room.MarkResult();
                if (duringReload) Assert.True(room.ContinueBombSeries(room.StartGeneration));
            }
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (request, next, ct) =>
            {
                Assert.Equal(11, request.UserNum);
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return (await next(ct)) with { Experience = 20 };
            }));
            var firstLeave = rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finalLeave = rig.Rooms.HandleLeaveAsync(rig.Host, new(), default).AsTask();
            Assert.False(finalLeave.IsCompleted);
            release.SetResult();
            await firstLeave.WaitAsync(TimeSpan.FromSeconds(5));
            await finalLeave.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(room.BombSeries!.Forfeited);
            Assert.Equal(1, progress.Credits);
            Assert.Equal(new PlayerProgress(11, 1, 20), stored[11]);
            Assert.Equal(new PlayerProgress(22, 1, 0), stored[22]);
            var plan = Assert.Single(room.BattleSettlements!);
            Assert.Null(plan.Record);
            Assert.Equal(20, plan.SettledReward!.ExpEarned);
            Assert.Equal(RoomPhase.Closed, room.Phase);
        }
        finally { release.TrySetResult(); await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Initial loading and canceled played series cannot use the forfeit settlement path.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InitialOrCanceledBombDepartureCannotPrepareAReward(int phase)
    {
        var stored = RewardStored();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = RewardRig(context);
        var calls = 0;
        try
        {
            var room = CreateRewardRoom(rig, start: false);
            lock (room.Gate)
            {
                room.Seats[0].Team = 1;
                room.Seats[1].Team = 2;
                room.MarkStarting();
                room.BeginBombSeries();
                if (phase != 0)
                {
                    room.MarkPlaying();
                    Assert.True(room.BombSeries!.TryCompleteRound(0, 1));
                    room.MarkResult();
                    if (phase == 1) Assert.True(room.ContinueBombSeries(room.StartGeneration));
                    room.RoundCancelled = true;
                }
            }
            context.Effect(context.Events.Intercept<RoundRewardPreparing, RoundRewardAmounts>(async (_, next, ct) =>
            {
                Interlocked.Increment(ref calls);
                return await next(ct);
            }));
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Equal(0, calls);
            Assert.Empty(progress.Matches);
            Assert.Null(room.BattleSettlements);
            Assert.Equal(new PlayerProgress(11, 1, 0), stored[11]);
            Assert.Equal(new PlayerProgress(22, 1, 0), stored[22]);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static Dictionary<long, PlayerProgress> RewardStored() => new()
    {
        [11] = new(11, 1, 0), [22] = new(22, 1, 0),
    };

    private static Rig RewardRig(PluginContext context, int rewardDelay = 6000) =>
        new(resultContext: context, resultRewardDelayMs: rewardDelay, configure: options =>
        {
            options.LevelExpThresholds = [0, 100, 1000];
            options.MaxLevel = 3;
            options.RewardBaseExp = 10;
            options.RewardExpPerKill = 0;
            options.RewardWinMultiplier = 1;
            options.RewardLucciPercent = 100;
        });

    private static PlayRoom CreateRewardRoom(Rig rig, Action<RoomSeat>? configure = null, bool start = true, bool guest = true)
    {
        var (room, _) = rig.Registry.CreateForMode(rig.Host, 4, "map", "rewards", 11, "host", listed: true);
        if (guest) rig.Registry.Join(rig.Guest, room.Id, 22, "guest");
        lock (room.Gate)
        {
            foreach (var seat in room.Seats) configure?.Invoke(seat);
            if (start) { room.MarkStarting(); room.MarkPlaying(); }
        }
        return room;
    }
}
