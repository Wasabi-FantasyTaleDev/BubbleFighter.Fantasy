using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>The plaza booths' day record: limits, one paper a day, the wallet in the same write, and reopening.</summary>
public sealed class SqlitePlazaGameRecordTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-plaza-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 };

    /// <summary>Fish stop at the daily limit, a short balance changes nothing, and record and wallet survive reopening.</summary>
    [Fact]
    public async Task SettlementsWriteRecordAndWalletTogetherAndSurviveReopening()
    {
        long user;
        long start;
        using (var database = new SqliteDatabase(Options()))
        {
            var repositories = new SqliteRepositories(database);
            user = (await repositories.CreateAsync("one", "one", default))!.UserNum;
            var seed = await repositories.GetOrCreateAsync(new PlayerWallet(user, 50, 0), default);
            start = seed.Lucci;
            var store = new SqlitePlazaGameRecords(database);
            Assert.Equal(1, (await store.SettleAsync(new(seed, Today) { Credit = 200, FishLimit = 2 }, default)).Record.FishingCount);
            Assert.Equal(2, (await store.SettleAsync(new(seed, Today) { Credit = 200, FishLimit = 2 }, default)).Record.FishingCount);
            Assert.Equal(PlazaGameSettlementStatus.LimitReached,
                (await store.SettleAsync(new(seed, Today) { Credit = 200, FishLimit = 2 }, default)).Status);
            Assert.Equal(PlazaGameSettlementStatus.ShortBalance,
                (await store.SettleAsync(new(seed, Today) { Charge = checked((int)start + 401), FortunePaper = 5 }, default)).Status);
            Assert.Equal(PlazaGameSettlementStatus.Settled,
                (await store.SettleAsync(new(seed, Today) { Charge = 50, FortunePaper = 0xFEDCBA9876543210UL }, default)).Status);
            Assert.Equal(PlazaGameSettlementStatus.PaperTaken,
                (await store.SettleAsync(new(seed, Today) { FortunePaper = 5 }, default)).Status);
        }
        using var reopened = new SqliteDatabase(Options());
        var records = new SqlitePlazaGameRecords(reopened);
        Assert.Equal(new PlazaGameRecord(Today, 2, 0xFEDCBA9876543210UL), await records.ReadAsync(user, Today, default));
        Assert.Equal(start + 350, (await new SqliteRepositories(reopened).FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal(PlazaGameRecord.Empty(Today.AddDays(1)), await records.ReadAsync(user, Today.AddDays(1), default));
        Assert.Equal(PlazaGameRecord.Empty(Today), await records.ReadAsync(user + 1, Today, default));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
