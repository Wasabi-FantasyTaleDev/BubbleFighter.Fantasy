using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    private sealed class Records(Func<long, CancellationToken, ValueTask<IReadOnlyList<PlayerBattleRecord>?>> read)
        : IPlayerBattleRecordsRepository
    {
        public bool IsDurable => false;
        public ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct) => read(userNum, ct);
        public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct) => throw new InvalidOperationException("Inspection does not request progress.");
        public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct) => throw new InvalidOperationException("Inspection cannot settle matches.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedBattleHistoryUsesTheTargetsAccountAndRefusesMissingHistory(bool hasHistory)
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        IReadOnlyList<PlayerBattleRecord>? history = hasHistory ? [new(1, Wins: 3)] : null;
        var store = new Records((user, _) =>
        {
            Assert.Equal(20, user);
            return ValueTask.FromResult(history);
        });
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, new Inventory(), replies, asker, new(20), default, store);
        var reply = Assert.Single(replies.Sent).Reply;
        Assert.Equal(hasHistory, reply.Fighter is not null);
        Assert.Equal(hasHistory, reply.Appearance is not null);
        Assert.Same(history, reply.BattleRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASessionReplacedDuringTheBattleReadCannotReceiveOrSupplyAStaleSnapshot(bool replaceRequester)
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<PlayerBattleRecord>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Records(async (_, ct) =>
        {
            readStarted.SetResult();
            return await release.Task.WaitAsync(ct);
        });
        var replies = new Replies();
        var request = FighterInfoHandler.HandleAsync(online, new Inventory(), replies, asker, new(20), default, store).AsTask();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replaced = replaceRequester ? asker : target;
        online.Remove(replaced.Connection.Id);
        online.Attach(new FeatureSession(3), replaceRequester ? new(10, "asker", "New Asker", 5) : new(20, "target", "New Target", 9));
        release.SetResult([new(1, Wins: 3)]);
        await request;
        if (replaceRequester) Assert.Empty(replies.Sent);
        else Assert.Null(Assert.Single(replies.Sent).Reply.Fighter);
    }
}
