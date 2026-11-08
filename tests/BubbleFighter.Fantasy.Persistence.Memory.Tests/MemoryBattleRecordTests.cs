using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryBattleRecordTests
{
    private static RoundSettlementCommand Command(string match, int mode = 1,
        BattleRecordOutcome outcome = BattleRecordOutcome.Won) =>
        new(match, 7, 10, before => before with { Exp = before.Exp + 20 })
        {
            BattleRecord = new(mode, outcome, 3, 2, 1, 2, 9),
        };

    private static async Task<MemoryPlayerProgressRepository> Store(int capacity = 32, int modes = 4)
    {
        var table = new MemoryPlayerTable(4);
        await new MemoryPlayerWalletRepository(table).UpsertAsync(new PlayerWallet(7, 0, 0), default);
        return new(table, capacity, modes);
    }

    [Fact]
    public async Task UnknownHistoryIsAbsentAndFreshCountersUseRealOutcomesPerMode()
    {
        var store = await Store();
        Assert.Null(await store.ListBattleRecordsAsync(7, default));
        Assert.Null(await store.ListBattleRecordsAsync(8, default));
        Assert.False(store.IsDurable);
        foreach (var outcome in Enum.GetValues<BattleRecordOutcome>())
            await store.SettleRoundAsync(Command(outcome.ToString(), outcome: outcome), default);
        await store.SettleRoundAsync(Command("item", 3), default);
        var records = (await store.ListBattleRecordsAsync(7, default))!;
        Assert.Equal(2, records.Count);
        Assert.Equal(new PlayerBattleRecord(1, 1, 1, 1, 1, 12, 8, 4, 8, 36), records[0]);
        Assert.Equal(4, records[0].Matches);
        Assert.Equal(1, records[1].Matches);
    }

    [Fact]
    public async Task ConcurrentRetriesAndOldReceiptReplaysDoNotCountTwice()
    {
        var store = await Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(async () => await store.SettleRoundAsync(Command("original"), default))));
        Assert.Single(results, result => result.Outcome == RoundSettlementOutcome.Settled);
        for (var i = 0; i < 16; i++) await store.SettleRoundAsync(Command($"later-{i}"), default);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled,
            (await store.SettleRoundAsync(Command("original") with { BattleRecord = null }, default)).Outcome);
        var record = Assert.Single((await store.ListBattleRecordsAsync(7, default))!);
        Assert.Equal(17, record.Matches);
        Assert.Equal(340, (await store.FindAsync(7, default))!.Exp);
    }

    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(32, 1, 3)]
    public async Task FullBudgetsRejectBeforeRewardsOrCountersChange(int receipts, int modes, int secondMode)
    {
        var store = await Store(receipts, modes);
        await store.SettleRoundAsync(Command("first"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SettleRoundAsync(Command("second", secondMode), default));
        Assert.Equal(20, (await store.FindAsync(7, default))!.Exp);
        Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(7, default))!).Matches);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled,
            (await store.SettleRoundAsync(Command("first"), default)).Outcome);
    }

    [Fact]
    public async Task InvalidCountersAndProgressLeaveNoReceiptOrPartialHistory()
    {
        var store = await Store();
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SettleRoundAsync(
            Command("retry") with { BattleRecord = new(1, BattleRecordOutcome.Won, -1, 0, 0, 0, 0) }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SettleRoundAsync(
            Command("retry") with { Advance = before => before with { UserNum = 8 } }, default));
        Assert.Null(await store.ListBattleRecordsAsync(7, default));
        Assert.Equal(0, (await store.FindAsync(7, default))!.Exp);
        Assert.Equal(RoundSettlementOutcome.Settled, (await store.SettleRoundAsync(Command("retry"), default)).Outcome);
    }

    [Fact]
    public void AccumulationRejectsOverflowAndCrossModeMixing()
    {
        var record = new PlayerBattleRecord(1, Kills: long.MaxValue);
        Assert.Throws<OverflowException>(() => record.Add(Command("match").BattleRecord!));
        Assert.Throws<ArgumentException>(() => record.Add(Command("match", 3).BattleRecord!));
        Assert.Equal(long.MaxValue, record.Kills);
    }
}
