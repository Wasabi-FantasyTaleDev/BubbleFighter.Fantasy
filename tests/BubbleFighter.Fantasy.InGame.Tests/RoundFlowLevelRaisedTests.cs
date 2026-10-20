using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

// The round announces a stored level rise on the event bus and knows nothing of what a subscriber grants for it.
public sealed partial class RoundFlowTests
{
    private sealed class FakeProgress(Dictionary<long, PlayerProgress> stored) : IPlayerProgressRepository
    {
        public List<PlayerProgress> Recorded { get; } = [];

        public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(stored.GetValueOrDefault(userNum));

        public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
        {
            var before = stored[command.UserNum];
            var progress = command.AdvanceChecked(before);
            lock (Recorded)
                Recorded.Add(progress);
            stored[progress.UserNum] = progress;
            return ValueTask.FromResult(new RoundSettlementResult(RoundSettlementOutcome.Settled, before, progress,
                command.LucciEarned, command.LucciEarned));
        }
    }

    private static (PluginContext Context, ConcurrentQueue<PlayerLevelRaised> Raised, Action<Func<PlayerLevelRaised, ValueTask>> Subscribe)
        ResultContext(IPlayerProgressRepository progress)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "level-raised-test", Plugin = "level-raised-test", Source = "test" },
            services, events, NullLogger.Instance);
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        context.Effect(services.Register(progress));
        var raised = new ConcurrentQueue<PlayerLevelRaised>();
        events.OnAsync<PlayerLevelRaised>((e, _) =>
        {
            raised.Enqueue(e);
            return ValueTask.CompletedTask;
        });
        return (context, raised, handler => events.OnAsync<PlayerLevelRaised>((e, _) => handler(e)));
    }

    // The rewards are granted when the round is in its result phase; every seated player starts at level 1 with no experience.
    private static void ToResult(PlayRoom room, Dictionary<long, PlayerProgress> stored)
    {
        lock (room.Gate)
        {
            room.MarkResult();
            foreach (var seat in room.Seats.Where(seat => seat.UserNum > 0))
                stored[seat.UserNum] = new PlayerProgress(seat.UserNum, 1, 0);
        }
    }

    [Fact]
    public async Task ARoundThatCrossesSeveralLevelsPublishesOneRiseWithTheStoredOldAndNewLevel()
    {
        var stored = new Dictionary<long, PlayerProgress>();
        var progress = new FakeProgress(stored);
        var (context, raised, _) = ResultContext(progress);
        // Thresholds 0 / 100 / 500 / 1000: 600 experience takes a level-1 player to level 3.
        var rig = new Rig(resultContext: context, configure: options =>
        {
            options.LevelExpThresholds = [0, 100, 500, 1000];
            options.MaxLevel = 4;
            options.RewardBaseExp = 600;
            options.RewardWinMultiplier = 1;
        });
        try
        {
            var room = await StartedRoundAsync(rig);
            ToResult(room, stored);

            await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);

            Assert.NotEmpty(progress.Recorded);
            Assert.Equal(progress.Recorded.Count, raised.Count);
            Assert.All(raised, rise => Assert.Equal(("test/game", 1, 3), (rise.Protocol, rise.OldLevel, rise.NewLevel)));
            Assert.Equal(raised.Select(rise => rise.UserNum).Order(), progress.Recorded.Select(p => p.UserNum).Order());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task ARoundThatStaysInTheSameLevelPublishesNothing()
    {
        var stored = new Dictionary<long, PlayerProgress>();
        var progress = new FakeProgress(stored);
        var (context, raised, _) = ResultContext(progress);
        var rig = new Rig(resultContext: context, configure: options =>
        {
            options.LevelExpThresholds = [0, 100, 500, 1000];
            options.MaxLevel = 4;
            options.RewardBaseExp = 10;
            options.RewardWinMultiplier = 1;
        });
        try
        {
            var room = await StartedRoundAsync(rig);
            ToResult(room, stored);

            await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);

            Assert.NotEmpty(progress.Recorded);
            Assert.Empty(raised);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task AFailingSubscriberDoesNotLoseTheRecordedRewardsOfTheRound()
    {
        var stored = new Dictionary<long, PlayerProgress>();
        var progress = new FakeProgress(stored);
        var (context, _, subscribe) = ResultContext(progress);
        subscribe(_ => throw new InvalidOperationException("grant failed"));
        var rig = new Rig(resultContext: context, configure: options =>
        {
            options.LevelExpThresholds = [0, 100, 500, 1000];
            options.MaxLevel = 4;
            options.RewardBaseExp = 150;
            options.RewardWinMultiplier = 1;
        });
        try
        {
            var room = await StartedRoundAsync(rig);
            ToResult(room, stored);

            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);

            Assert.NotEmpty(rewards);
            Assert.All(rewards.Values, reward => Assert.Equal(2, reward.Level));
            Assert.Equal(progress.Recorded.Count, rewards.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
    // A store that keeps receipts like the real ones and can lose the acknowledgement of a commit or refuse a player.
    private sealed class ReceiptProgress(Dictionary<long, PlayerProgress> stored) : IPlayerProgressRepository
    {
        private readonly Dictionary<(string, long), RoundSettlementResult> _receipts = [];

        public int Credits { get; private set; }
        public HashSet<long> LoseAcknowledgementOnce { get; } = [];
        public HashSet<long> AlwaysFail { get; } = [];
        public List<string> Matches { get; } = [];

        public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct)
        {
            lock (_receipts)
                return ValueTask.FromResult(stored.GetValueOrDefault(userNum));
        }

        public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
        {
            lock (_receipts)
            {
                Matches.Add(command.MatchId);
                if (AlwaysFail.Contains(command.UserNum))
                    throw new InvalidOperationException("store unavailable");
                if (_receipts.TryGetValue((command.MatchId, command.UserNum), out var first))
                    return ValueTask.FromResult(first with { Outcome = RoundSettlementOutcome.AlreadySettled });
                var before = stored[command.UserNum];
                var after = command.AdvanceChecked(before);
                stored[command.UserNum] = after;
                Credits++;
                var result = new RoundSettlementResult(RoundSettlementOutcome.Settled, before, after, command.LucciEarned, command.LucciEarned);
                _receipts[(command.MatchId, command.UserNum)] = result;
                if (LoseAcknowledgementOnce.Remove(command.UserNum))
                    throw new TimeoutException("the commit's acknowledgement was lost");
                return ValueTask.FromResult(result);
            }
        }
    }

    private static Rig SettlementRig(PluginContext context) => new(resultContext: context, configure: options =>
    {
        options.LevelExpThresholds = [0, 100, 500, 1000];
        options.MaxLevel = 4;
        options.RewardBaseExp = 150;
        options.RewardWinMultiplier = 1;
    });

    [Fact]
    public async Task ARetryAfterALostAcknowledgementCreditsOnceAndStillShowsTheReward()
    {
        var stored = new Dictionary<long, PlayerProgress>();
        var progress = new ReceiptProgress(stored);
        var (context, raised, _) = ResultContext(progress);
        var rig = SettlementRig(context);
        try
        {
            var room = await StartedRoundAsync(rig);
            ToResult(room, stored);
            var users = stored.Keys.ToArray();
            progress.LoseAcknowledgementOnce.UnionWith(users);

            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);

            Assert.Equal(users.Length, rewards.Count);
            Assert.Equal(users.Length, progress.Credits);
            Assert.All(rewards.Values, reward => Assert.Equal((2, 150L), (reward.Level, reward.Exp)));
            Assert.All(users, user => Assert.Equal(new PlayerProgress(user, 2, 150), stored[user]));
            Assert.Equal(users.Length, raised.Count);
            Assert.Single(progress.Matches.Distinct());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task ASeatWhoseStoreFailsLeavesTheOtherSeatsRewarded()
    {
        var stored = new Dictionary<long, PlayerProgress>();
        var progress = new ReceiptProgress(stored);
        var (context, _, _) = ResultContext(progress);
        var rig = SettlementRig(context);
        try
        {
            var room = await StartedRoundAsync(rig);
            ToResult(room, stored);
            var users = stored.Keys.Order().ToArray();
            Assert.True(users.Length >= 2);
            progress.AlwaysFail.Add(users[0]);

            var rewards = await rig.Round.GrantRewardsAsync(room, room.StartGeneration, RoundEndReason.StageReported, default);

            Assert.Equal(users.Length - 1, rewards.Count);
            Assert.Equal(new PlayerProgress(users[0], 1, 0), stored[users[0]]);
            Assert.All(users[1..], user => Assert.Equal(new PlayerProgress(user, 2, 150), stored[user]));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
