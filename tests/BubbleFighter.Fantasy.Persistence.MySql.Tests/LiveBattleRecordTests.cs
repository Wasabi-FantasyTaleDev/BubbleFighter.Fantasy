using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    private static RoundSettlementCommand Battle(long user, string match, int mode = 1, BattleRecordOutcome outcome = BattleRecordOutcome.Won) =>
        new(match, user, outcome == BattleRecordOutcome.Escaped ? 0 : 10, before => before with { Exp = before.Exp + 2 })
        {
            BattleRecord = new(mode, outcome, 1, 2, 3, 4, 5),
        };

    [Fact]
    public async Task BattleRecordsCommitOnceAcrossConcurrentConnectionsAndRepositoryReopen()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        await new MySqlPlayerWalletRepository(factory).UpsertAsync(new(user, 100, 0), default);
        var repository = new MySqlPlayerProgressRepository(factory);
        Assert.True(repository.IsDurable);
        Assert.Null(await repository.ListBattleRecordsAsync(user, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new MySqlPlayerProgressRepository(factory).SettleRoundAsync(Battle(user, "concurrent"), default).AsTask()));
        Assert.Single(results, result => result.Outcome == RoundSettlementOutcome.Settled);
        foreach (var outcome in new[] { BattleRecordOutcome.Draw, BattleRecordOutcome.Lost, BattleRecordOutcome.Escaped })
            await repository.SettleRoundAsync(Battle(user, outcome.ToString(), outcome: outcome), default);
        await repository.SettleRoundAsync(Battle(user, "mode3", 3), default);
        var reopened = new MySqlPlayerProgressRepository(factory);
        var expected = new[] { new PlayerBattleRecord(1, 1, 1, 1, 1, 4, 8, 12, 16, 20),
            new PlayerBattleRecord(3, 1, 0, 0, 0, 1, 2, 3, 4, 5) };
        Assert.Equal(expected, await reopened.ListBattleRecordsAsync(user, default));
        var retry = Battle(user, "concurrent", 99) with { Advance = _ => throw new InvalidOperationException("Must not repeat.") };
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await reopened.SettleRoundAsync(retry, default)).Outcome);
        Assert.Equal(expected, await reopened.ListBattleRecordsAsync(user, default));
        Assert.Equal(140, (await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(10, (await reopened.FindAsync(user, default))!.Exp);
    }

    [Fact]
    public async Task BattleRecordAndProgressRollBackWhenTheWalletWriteFails()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var repository = new MySqlPlayerProgressRepository(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SettleRoundAsync(Battle(user, "retry"), default).AsTask());
        Assert.Null(await repository.ListBattleRecordsAsync(user, default));
        Assert.Equal(0, (await repository.FindAsync(user, default))!.Exp);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM round_settlements WHERE user_num=@u", ("@u", user)));
        await new MySqlPlayerWalletRepository(factory).UpsertAsync(new(user, 100, 0), default);
        Assert.Equal(RoundSettlementOutcome.Settled, (await repository.SettleRoundAsync(Battle(user, "retry"), default)).Outcome);
        Assert.Equal(1, Assert.Single((await repository.ListBattleRecordsAsync(user, default))!).Matches);
    }

    [Fact]
    public async Task BattleModeCapacityAndOverflowPreserveExistingRewardsAndReceipts()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        await new MySqlPlayerWalletRepository(factory).UpsertAsync(new(user, 100, 0), default);
        var repository = new MySqlPlayerProgressRepository(factory, maxBattleModesPerPlayer: 1);
        await repository.SettleRoundAsync(Battle(user, "one"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SettleRoundAsync(Battle(user, "two", 3), default).AsTask());
        Assert.Equal(110, (await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default))!.Lucci);
        await repository.SettleRoundAsync(Battle(user, "two"), default);
        Assert.Equal(2, Assert.Single((await repository.ListBattleRecordsAsync(user, default))!).Matches);
        await ScalarAsync<int>("UPDATE player_battle_records SET wins=9223372036854775807 WHERE user_num=@u AND mode_id=1", ("@u", user));
        await Assert.ThrowsAsync<OverflowException>(() => repository.SettleRoundAsync(Battle(user, "three"), default).AsTask());
        Assert.Equal(120, (await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(4, (await repository.FindAsync(user, default))!.Exp);
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM round_settlements WHERE user_num=@u", ("@u", user)));
    }

    [Fact]
    public async Task BattleRecordsDoNotBackfillAnOlderRewardReceipt()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        await new MySqlPlayerWalletRepository(factory).UpsertAsync(new(user, 100, 0), default);
        var repository = new MySqlPlayerProgressRepository(factory);
        await repository.SettleRoundAsync(new("old", user, 5, before => before with { Exp = 50 }), default);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await repository.SettleRoundAsync(Battle(user, "old"), default)).Outcome);
        Assert.Null(await repository.ListBattleRecordsAsync(user, default));
        await repository.SettleRoundAsync(Battle(user, "new"), default);
        Assert.Equal(1, Assert.Single((await repository.ListBattleRecordsAsync(user, default))!).Matches);
    }
}
