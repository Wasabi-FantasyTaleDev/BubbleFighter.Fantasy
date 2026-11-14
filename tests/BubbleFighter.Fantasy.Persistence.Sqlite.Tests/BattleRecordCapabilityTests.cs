using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class BattleRecordCapabilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bff-battle-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "save.sqlite");
    private SqliteOptions Options(int modes = 128) => new() { DatabasePath = DatabasePath, MaxBattleModesPerPlayer = modes };
    private static RoundSettlementCommand Record(long user, string match, int mode = 1, BattleRecordOutcome outcome = BattleRecordOutcome.Won) =>
        new(match, user, outcome == BattleRecordOutcome.Escaped ? 0 : 10, before => before with { Exp = before.Exp + 2 })
        {
            BattleRecord = new(mode, outcome, 1, 2, 3, 4, 5),
        };

    [Fact]
    public async Task AllOutcomesCountersAndReceiptsSurviveReopenWithoutInventingHistory()
    {
        long user;
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            Assert.True(store.IsDurable);
            user = (await store.CreateAsync("player", "player", default)).UserNum;
            await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            Assert.Null(await store.ListBattleRecordsAsync(user, default));
            Assert.Null(await store.ListBattleRecordsAsync(user + 100, default));
            foreach (var outcome in Enum.GetValues<BattleRecordOutcome>())
                await store.SettleRoundAsync(Record(user, outcome.ToString(), outcome: outcome), default);
            await store.SettleRoundAsync(Record(user, "mode3", 3), default);
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            var records = await store.ListBattleRecordsAsync(user, default);
            Assert.Equal(new[] { new PlayerBattleRecord(1, 1, 1, 1, 1, 4, 8, 12, 16, 20),
                new PlayerBattleRecord(3, 1, 0, 0, 0, 1, 2, 3, 4, 5) }, records);
            var retry = Record(user, "Won", 99) with { Advance = _ => throw new InvalidOperationException("Must not repeat.") };
            Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await store.SettleRoundAsync(retry, default)).Outcome);
            Assert.Equal(records, await store.ListBattleRecordsAsync(user, default));
            Assert.Equal(140, (await store.FindByUserNumAsync(user, default))!.Lucci);
            Assert.Equal(10, (await store.FindAsync(user, default))!.Exp);
        }
    }

    [Fact]
    public async Task FailedReceiptRollsBackCountersProgressAndWalletAndAllowsRetry()
    {
        using var database = new SqliteDatabase(Options());
        var store = new SqliteRepositories(database);
        var user = (await store.CreateAsync("player", "player", default)).UserNum;
        await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
        Execute("CREATE TRIGGER reject_receipt BEFORE INSERT ON round_settlements BEGIN SELECT RAISE(ABORT, 'test receipt failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => store.SettleRoundAsync(Record(user, "retry"), default).AsTask());
        Assert.Null(await store.ListBattleRecordsAsync(user, default));
        Assert.Equal(100, (await store.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(0, (await store.FindAsync(user, default))!.Exp);
        Execute("DROP TRIGGER reject_receipt;");
        Assert.Equal(RoundSettlementOutcome.Settled, (await store.SettleRoundAsync(Record(user, "retry"), default)).Outcome);
        Assert.Equal(1, Assert.Single((await store.ListBattleRecordsAsync(user, default))!).Wins);
    }

    [Fact]
    public async Task ModeCapacityRejectsWholeSettlementButAllowsAnExistingModeAndItsRetry()
    {
        using var database = new SqliteDatabase(Options(1));
        var store = new SqliteRepositories(database);
        var user = (await store.CreateAsync("player", "player", default)).UserNum;
        await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
        await store.SettleRoundAsync(Record(user, "one"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleRoundAsync(Record(user, "two", 3), default).AsTask());
        Assert.Equal(110, (await store.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(2, (await store.FindAsync(user, default))!.Exp);
        await store.SettleRoundAsync(Record(user, "two"), default);
        Assert.Equal(2, Assert.Single((await store.ListBattleRecordsAsync(user, default))!).Matches);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await store.SettleRoundAsync(Record(user, "one", 3), default)).Outcome);
    }

    [Fact]
    public async Task WalletOverflowAndCancellationLeaveBattleAndReceiptUnchanged()
    {
        using var database = new SqliteDatabase(Options());
        var store = new SqliteRepositories(database);
        var user = (await store.CreateAsync("player", "player", default)).UserNum;
        await store.UpsertAsync(new PlayerWallet(user, int.MaxValue, 0), default);
        await Assert.ThrowsAsync<OverflowException>(() => store.SettleRoundAsync(Record(user, "one"), default).AsTask());
        Assert.Null(await store.ListBattleRecordsAsync(user, default));
        await store.UpsertAsync(new PlayerWallet(user, 0, 0), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SettleRoundAsync(Record(user, "one"), new(true)).AsTask());
        Assert.Null(await store.ListBattleRecordsAsync(user, default));
        Assert.Equal(RoundSettlementOutcome.Settled, (await store.SettleRoundAsync(Record(user, "one"), default)).Outcome);
    }

    [Fact]
    public async Task VersionEightSaveRetainsUnrecordedProgressAndGainsDurableBattleRecords()
    {
        long user;
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            user = (await store.CreateAsync("player", "player", default)).UserNum;
            await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            await store.SettleRoundAsync(new("old", user, 5, before => before with { Exp = 50 }), default);
        }
        Execute("UPDATE players SET state=json_remove(state, '$.BattleRecords'); DROP TABLE IF EXISTS pet_identity_allocator; PRAGMA user_version=8;");
        using var upgraded = new SqliteDatabase(Options());
        var repository = new SqliteRepositories(upgraded);
        Assert.Null(await repository.ListBattleRecordsAsync(user, default));
        Assert.Equal(50, (await repository.FindAsync(user, default))!.Exp);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await repository.SettleRoundAsync(Record(user, "old"), default)).Outcome);
        Assert.Null(await repository.ListBattleRecordsAsync(user, default));
        await repository.SettleRoundAsync(Record(user, "new"), default);
        Assert.Equal(1, Assert.Single((await repository.ListBattleRecordsAsync(user, default))!).Matches);
    }

    [Fact]
    public async Task OverflowedAggregateLeavesNoPartialRewardOrReceipt()
    {
        long user;
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            user = (await store.CreateAsync("player", "player", default)).UserNum;
            await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            await store.SettleRoundAsync(Record(user, "one"), default);
        }
        Execute("UPDATE players SET state=json_set(state, '$.BattleRecords[0].Wins', 9223372036854775807);");
        using var reopened = new SqliteDatabase(Options());
        var repository = new SqliteRepositories(reopened);
        await Assert.ThrowsAsync<OverflowException>(() => repository.SettleRoundAsync(Record(user, "two"), default).AsTask());
        Assert.Equal(110, (await repository.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(2, (await repository.FindAsync(user, default))!.Exp);
        Assert.Equal(long.MaxValue, Assert.Single((await repository.ListBattleRecordsAsync(user, default))!).Wins);
    }

    [Theory]
    [InlineData("json_set(state, '$.BattleRecords', NULL)")]
    [InlineData("json_set(state, '$.BattleRecords[0].Wins', -1)")]
    [InlineData("json_insert(state, '$.BattleRecords[#]', json_extract(state, '$.BattleRecords[0]'))")]
    public async Task InvalidStoredBattleHistoryFailsReopen(string corruptState)
    {
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            var user = (await store.CreateAsync("player", "player", default)).UserNum;
            await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            await store.SettleRoundAsync(Record(user, "one"), default);
        }
        Execute("UPDATE players SET state=" + corruptState + ";");
        Assert.Throws<InvalidDataException>(() => new SqliteDatabase(Options()));
    }

    [Fact]
    public async Task MissingStoredCounterDoesNotBecomeAZeroStatistic()
    {
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteRepositories(database);
            var user = (await store.CreateAsync("player", "player", default)).UserNum;
            await store.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            await store.SettleRoundAsync(Record(user, "one"), default);
        }
        Execute("UPDATE players SET state=json_remove(state, '$.BattleRecords[0].Wins');");
        Assert.Throws<System.Text.Json.JsonException>(() => new SqliteDatabase(Options()));
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
