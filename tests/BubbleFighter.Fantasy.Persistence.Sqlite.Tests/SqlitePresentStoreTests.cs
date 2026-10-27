using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Runs the shared gift store contract over the SQLite save and pins the save's gift table and schema version.</summary>
public sealed class SqlitePresentStoreTests : PresentStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-present-" + Guid.NewGuid().ToString("N"));
    private SqliteDatabase? _database;

    private string DatabasePath => Path.Combine(_directory, "save.sqlite");

    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 16, MaxAccounts = 16, MaxDatabaseBytes = 4_194_304 };

    /// <inheritdoc />
    protected override Stores Create()
    {
        _database = new SqliteDatabase(Options());
        var repositories = new SqliteRepositories(_database);
        return new Stores(repositories, repositories, repositories, repositories, repositories);
    }

    [Fact]
    public async Task GiftsAndTheirStateSurviveARestart()
    {
        PresentPlayer sender, receiver;
        long first, second;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            sender = new((await stores.CreateAsync("a-sender", "sender", default)).UserNum, "sender");
            receiver = new((await stores.CreateAsync("a-receiver", "receiver", default)).UserNum, "receiver");
            first = (await stores.SendAsync(Send(sender, receiver, 10), default)).Present!.Id;
            second = (await stores.SendAsync(Send(sender, receiver, 20), default)).Present!.Id;
            Assert.True(await stores.DiscardAsync("kr-812", receiver.UserNum, second, default));
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            Assert.Equal(970, (await stores.FindByUserNumAsync(sender.UserNum, default))!.Lucci);
            var received = await stores.ListReceivedAsync("kr-812", receiver.UserNum, 0, 4, default);
            Assert.Equal(first, Assert.Single(received.Records).Id);
            Assert.Equal(1, await stores.CountPendingAsync("kr-812", receiver.UserNum, default));
            var sent = await stores.ListSentAsync("kr-812", sender.UserNum, 0, 4, default);
            Assert.Equal([PresentState.Discarded, PresentState.Pending], sent.Records.Select(record => record.State));
            // Ids are never reused after a restart.
            var third = (await stores.SendAsync(Send(sender, receiver, 1), default)).Present!.Id;
            Assert.True(third > second);
        }
    }

    [Fact]
    public async Task ASaveFromBeforeTheGiftBoxGainsTheTableAndKeepsItsPlayers()
    {
        using (var database = new SqliteDatabase(Options()))
            Assert.True(await new SqliteRepositories(database).RegisterAsync("player", "hash", default));
        Assert.Equal(7, UserVersion());
        Execute("DROP TABLE messenger_requests; DROP TABLE messenger_categories; DROP TABLE messenger_relations; DROP TABLE messenger_users; DROP TABLE round_settlements; DROP TABLE present_claims; DROP TABLE presents; PRAGMA user_version=3;");

        using var upgraded = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(upgraded);
        Assert.Equal("hash", (await stores.FindAsync("player", default))!.PasswordHash);
        Assert.Equal(7, UserVersion());
        var sender = new PresentPlayer((await stores.CreateAsync("a-sender", "sender", default)).UserNum, "sender");
        var receiver = new PresentPlayer((await stores.CreateAsync("a-receiver", "receiver", default)).UserNum, "receiver");
        Assert.Equal(PresentSendOutcome.Sent, (await stores.SendAsync(Send(sender, receiver, 5), default)).Outcome);
    }

    [Fact]
    public async Task AGiftBoxSaveFromBeforeRewardsGainsTheClaimTableAndKeepsItsGifts()
    {
        PresentPlayer sender, receiver;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            sender = new((await stores.CreateAsync("a-sender", "sender", default)).UserNum, "sender");
            receiver = new((await stores.CreateAsync("a-receiver", "receiver", default)).UserNum, "receiver");
            Assert.Equal(PresentSendOutcome.Sent, (await stores.SendAsync(Send(sender, receiver, 5), default)).Outcome);
        }
        Execute("DROP TABLE messenger_requests; DROP TABLE messenger_categories; DROP TABLE messenger_relations; DROP TABLE messenger_users; DROP TABLE round_settlements; DROP TABLE present_claims; PRAGMA user_version=4;");

        using var upgraded = new SqliteDatabase(Options());
        var upgradedStores = new SqliteRepositories(upgraded);
        Assert.Equal(7, UserVersion());
        Assert.Equal(1, await upgradedStores.CountPendingAsync("kr-812", receiver.UserNum, default));
        var reward = new PresentRewardCommand("kr-812", receiver.UserNum, "level:2", "System", [1000123], "hi", 1);
        Assert.Equal(PresentRewardOutcome.Granted, (await upgradedStores.GrantRewardAsync(reward, default)).Outcome);
        Assert.Equal(PresentRewardOutcome.AlreadyClaimed, (await upgradedStores.GrantRewardAsync(reward, default)).Outcome);
    }

    [Fact]
    public async Task ARewardClaimSurvivesARestartSoTheRewardIsNotGrantedTwice()
    {
        long receiver;
        PresentRewardCommand reward;
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            receiver = (await stores.CreateAsync("a-receiver", "receiver", default)).UserNum;
            reward = new PresentRewardCommand("kr-812", receiver, "level:2", "System", [1000123], "hi", 1);
            Assert.Equal(PresentRewardOutcome.Granted, (await stores.GrantRewardAsync(reward, default)).Outcome);
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            Assert.Equal(PresentRewardOutcome.AlreadyClaimed, (await stores.GrantRewardAsync(reward, default)).Outcome);
            Assert.Equal(1, await stores.CountPendingAsync("kr-812", receiver, default));
        }
    }

    [Fact]
    public async Task AFailedGiftInsertRollsBackEveryGiftAndTheClaimSoRecoveryCanRetry()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var receiver = (await stores.CreateAsync("a-receiver", "receiver", default)).UserNum;
        var reward = new PresentRewardCommand("kr-812", receiver, "level:2", "System", [1000123, 1000124], "hi", 1);
        Execute("CREATE TRIGGER fail_reward BEFORE INSERT ON presents WHEN NEW.stock_key=1000124 BEGIN SELECT RAISE(ABORT, 'injected gift failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => stores.GrantRewardAsync(reward, default).AsTask());
        Assert.Equal(0, await stores.CountPendingAsync("kr-812", receiver, default));
        Execute("DROP TRIGGER fail_reward;");
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.GrantRewardAsync(reward, default)).Outcome);
        Assert.Equal(2, await stores.CountPendingAsync("kr-812", receiver, default));
    }

    [Fact]
    public void ASaveOfANewerSchemaThanThisBuildKnowsIsRefused()
    {
        using (var database = new SqliteDatabase(Options()))
            Assert.Equal(7, UserVersion());
        Execute("PRAGMA user_version=8;");
        Assert.Throws<InvalidDataException>(() => new SqliteDatabase(Options()));
    }

    [Fact]
    public void TheGiftTableRefusesAStateOrHiddenFlagOutsideItsDomain()
    {
        using (var database = new SqliteDatabase(Options()))
            Assert.Equal(7, UserVersion());
        const string row = "INSERT INTO presents(profile,sender_user_num,sender_nickname,receiver_user_num,receiver_nickname,stock_key,message,sent_at";
        Assert.Throws<SqliteException>(() => Execute(row + ",state) VALUES('x',1,'a',2,'b',3,'',0,3);"));
        Assert.Throws<SqliteException>(() => Execute(row + ",sender_hidden) VALUES('x',1,'a',2,'b',3,'',0,2);"));
        Assert.Throws<SqliteException>(() => Execute(row + ",receiver_hidden) VALUES('x',1,'a',2,'b',3,'',0,2);"));
    }

    private static Contracts.Presents.PresentSendCommand Send(PresentPlayer from, PresentPlayer to, int price) =>
        new("kr-812", from, to, 1000123, 0, 207026, "hi", "lucci", price, new(from.UserNum, 1000, 0), 1_000, 100);

    private int UserVersion()
    {
        using var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _database?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        base.Dispose();
    }
}
