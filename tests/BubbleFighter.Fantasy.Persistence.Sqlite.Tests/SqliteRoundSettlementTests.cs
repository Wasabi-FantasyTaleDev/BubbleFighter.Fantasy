using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Pins the once-only round settlement of the SQLite save, its restart survival and the version 6 upgrade.</summary>
public sealed class SqliteRoundSettlementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-settle-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "save.sqlite");

    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 16, MaxAccounts = 16, MaxDatabaseBytes = 4_194_304 };

    private static RoundSettlementCommand Add(string match, long user, long exp, int lucci) =>
        new(match, user, lucci, stored => stored with { Level = stored.Level + 1, Exp = stored.Exp + exp });

    [Fact]
    public async Task ASettlementCreditsOnceAndItsReceiptSurvivesARestart()
    {
        long user;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            user = (await stores.CreateAsync("account", "player", default)).UserNum;
            await stores.UpsertAsync(new PlayerWallet(user, 100, 0), default);
            var settled = await stores.SettleRoundAsync(Add("m1", user, 40, 10), default);
            Assert.Equal((RoundSettlementOutcome.Settled, 110), (settled.Outcome, settled.LucciBalance));
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            var repeated = await stores.SettleRoundAsync(Add("m1", user, 999, 999), default);
            Assert.Equal(RoundSettlementOutcome.AlreadySettled, repeated.Outcome);
            Assert.Equal((repeated.Before.Exp + 40, 10, 110), (repeated.After.Exp, repeated.LucciEarned, repeated.LucciBalance));
            Assert.Equal(repeated.After, await stores.FindAsync(user, default));
            Assert.Equal(110, (await stores.FindByUserNumAsync(user, default))!.Lucci);
        }
    }

    [Fact]
    public async Task AFailedProgressRuleLeavesNoReceiptAndNoCredit()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateAsync("account", "player", default)).UserNum;
        await stores.UpsertAsync(new PlayerWallet(user, 100, 0), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stores.SettleRoundAsync(new("m1", user, 10, stored => stored with { Level = 0 }), default).AsTask());
        Assert.Equal(100, (await stores.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(RoundSettlementOutcome.Settled, (await stores.SettleRoundAsync(Add("m1", user, 1, 1), default)).Outcome);
    }

    [Fact]
    public async Task AVersionFiveSaveGainsTheReceiptTable()
    {
        long user;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            user = (await stores.CreateAsync("account", "player", default)).UserNum;
            await stores.UpsertAsync(new PlayerWallet(user, 0, 0), default);
        }
        Execute("DROP TABLE messenger_requests; DROP TABLE messenger_categories; DROP TABLE messenger_relations; DROP TABLE messenger_users; DROP TABLE round_settlements; DROP TABLE IF EXISTS pet_identity_allocator; PRAGMA user_version=5;");
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            Assert.Equal(RoundSettlementOutcome.Settled, (await stores.SettleRoundAsync(Add("m1", user, 1, 1), default)).Outcome);
        }
        using var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(13L, command.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Wallet failure rolls back medals and their receipt; a committed retry remains idempotent after reopen.</summary>
    [Fact]
    public async Task RoundMedalsShareRollbackAndDurableSettlementReceipt()
    {
        long user;
        RoundSettlementCommand command;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            user = (await stores.CreateAsync("medals", "player", default)).UserNum;
            await stores.UpsertAsync(new PlayerWallet(user, int.MaxValue, 0), default);
            command = Add("medal-round", user, 40, 1) with
                { TitleMedals = new(user, "kr-1256", "round", "round:medal-round", [new(30001, 3)]) };
            await Assert.ThrowsAsync<OverflowException>(() => stores.SettleRoundAsync(command, default).AsTask());
            Assert.Empty(await stores.ReadAsync(user, "kr-1256", default));
            await stores.UpsertAsync(new PlayerWallet(user, 0, 0), default);
            Assert.Equal(RoundSettlementOutcome.Settled, (await stores.SettleRoundAsync(command, default)).Outcome);
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await stores.SettleRoundAsync(command, default)).Outcome);
            Assert.Equal(new[] { new TitleMedalCount(30001, 3) }, await stores.ReadAsync(user, "kr-1256", default));
            Assert.Equal(1, (await stores.FindByUserNumAsync(user, default))!.Lucci);
            var different = command with
                { TitleMedals = new(user, "kr-1256", "round", "round:medal-round", [new(30001, 4)]) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => stores.SettleRoundAsync(different, default).AsTask());
            Assert.Equal(3, Assert.Single(await stores.ReadAsync(user, "kr-1256", default)).Count);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
