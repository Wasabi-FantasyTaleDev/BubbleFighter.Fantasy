using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteCashInboxTests : CashInboxStoreContractTests
{
    protected override ValueTask<ICashInboxFixture> CreateFixtureAsync() =>
        ValueTask.FromResult<ICashInboxFixture>(new SqliteFixture());

    [Fact]
    public async Task ReopenRetainsInboxEntriesAndUpgradesSchemaVersion15Saves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bff-sqlite-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "save.sqlite");
        var options = new SqliteOptions { DatabasePath = dbPath, MaxPlayers = 8, MaxAccounts = 8, MaxDatabaseBytes = 4_194_304 };

        try
        {
            long userNum;
            long slotId;
            using (var db = new SqliteDatabase(options))
            {
                Assert.Equal(18L, db.RawScalar("PRAGMA user_version"));
                var repos = new SqliteRepositories(db);
                userNum = (await repos.CreateAsync("alice", "Alice", default)).UserNum;
                var grant = await repos.GrantAsync(
                    new CashInboxGrantCommand("kr-812", userNum, 1001, 50001, 1, 0, "gift", 1000, 0, 64),
                    default);
                slotId = grant.Entry!.CashSlotId;
            }

            using (var reopened = new SqliteDatabase(options))
            {
                var repos = new SqliteRepositories(reopened);
                var active = await repos.ListActiveAsync("kr-812", userNum, 16, 1500, default);
                Assert.Single(active);
                Assert.Equal(slotId, active[0].CashSlotId);
                Assert.Equal("gift", active[0].Note);
            }

            // Simulate upgrading a schema-v15 database created before W07 landed.
            using (var conn = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DROP TABLE cash_inbox_claims; DROP TABLE cash_inbox_entries; PRAGMA user_version=15;";
                cmd.ExecuteNonQuery();
            }

            using (var upgraded = new SqliteDatabase(options))
            {
                Assert.Equal(18L, upgraded.RawScalar("PRAGMA user_version"));
                var repos = new SqliteRepositories(upgraded);
                var postUpgrade = await repos.GrantAsync(
                    new CashInboxGrantCommand("kr-812", userNum, 1002, 50002, 1, 0, "after-upgrade", 2000, 0, 64),
                    default);
                Assert.Equal(CashInboxGrantOutcome.Granted, postUpgrade.Outcome);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SqliteFixture : ICashInboxFixture
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bff-sqlite-cashinbox-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabase _database;
        private readonly SqliteRepositories _repos;

        public SqliteFixture()
        {
            Directory.CreateDirectory(_dir);
            _database = new SqliteDatabase(new SqliteOptions
            {
                DatabasePath = Path.Combine(_dir, "save.sqlite"),
                MaxPlayers = 16,
                MaxAccounts = 16,
                MaxDatabaseBytes = 4_194_304,
            });
            _repos = new SqliteRepositories(_database);
        }

        public ICashInboxStore Store => _repos;

        public async ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct) =>
            (await _repos.CreateAsync(username, username, ct)).UserNum;

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct) =>
            _repos.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);

        public ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            _repos.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag, slots, ct);

        public ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct) =>
            _repos.FindByUserNumAsync(userNum, ct);

        public ValueTask DisposeAsync()
        {
            _database.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
