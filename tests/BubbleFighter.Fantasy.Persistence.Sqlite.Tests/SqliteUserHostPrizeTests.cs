using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteUserHostPrizeTests : UserHostPrizeStoreContractTests
{
    protected override ValueTask<IUserHostPrizeFixture> CreateFixtureAsync() =>
        ValueTask.FromResult<IUserHostPrizeFixture>(new SqliteFixture());

    [Fact]
    public async Task ReopenRetainsEscrowsAndUpgradesSchemaVersion16Saves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bff-sqlite-userhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "save.sqlite");
        var options = new SqliteOptions { DatabasePath = dbPath, MaxPlayers = 8, MaxAccounts = 8, MaxDatabaseBytes = 4_194_304 };

        try
        {
            long host;
            using (var db = new SqliteDatabase(options))
            {
                Assert.Equal(18L, db.RawScalar("PRAGMA user_version"));
                var repos = new SqliteRepositories(db);
                host = (await repos.CreateAsync("alice", "Alice", default)).UserNum;
                await repos.UpsertAsync(new PlayerWallet(host, 25_000, 0), default);
                await repos.ReplaceSlotsAsync(host, InventoryBag.CharacterBag,
                [
                    new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, 4100101, 2, 0)
                    {
                        Lifetime = InventoryLifetime.Permanent,
                    },
                ], default);
                var begin = await repos.BeginEscrowAsync(
                    new UserHostEscrowBeginCommand("kr-812", "reopen-match", 1, host, 10_000, 10, 4100101, 1_000),
                    default);
                Assert.Equal(UserHostEscrowBeginOutcome.Committed, begin.Outcome);
            }

            // Reopen settles the persisted active escrow across process restarts.
            using (var reopened = new SqliteDatabase(options))
            {
                var repos = new SqliteRepositories(reopened);
                var recovered = await repos.RecoverActiveEscrowsAsync("kr-812", "", 2_000, 16, default);
                Assert.Equal(1, recovered.RecoveredCount);
                Assert.Equal(10_000L, recovered.TotalRefundedLucci);

                var flushed = await repos.FlushPendingClaimsAsync("kr-812", host, 16, 3_000, default);
                Assert.Equal(1, flushed.ClaimedCount);
                Assert.Equal(25_000, flushed.WalletAfter?.Lucci);
            }

            // Simulate upgrading a schema-v16 database created before W04 landed.
            using (var conn = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DROP TABLE user_host_claims; DROP TABLE user_host_escrows; PRAGMA user_version=16;";
                cmd.ExecuteNonQuery();
            }

            using (var upgraded = new SqliteDatabase(options))
            {
                Assert.Equal(18L, upgraded.RawScalar("PRAGMA user_version"));
                var repos = new SqliteRepositories(upgraded);
                Assert.Equal(25_000, (await repos.FindByUserNumAsync(host, default))?.Lucci);
                var afterUpgrade = await repos.BeginEscrowAsync(
                    new UserHostEscrowBeginCommand("kr-812", "after-upgrade", 2, host, 5_000, 10, 4100101, 4_000),
                    default);
                Assert.Equal(UserHostEscrowBeginOutcome.Committed, afterUpgrade.Outcome);
                Assert.Equal(20_000, afterUpgrade.HostLucciAfter);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SqliteFixture : IUserHostPrizeFixture
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bff-sqlite-uhfixture-" + Guid.NewGuid().ToString("N"));
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

        public IUserHostPrizeStore Store => _repos;

        public async ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct) =>
            (await _repos.CreateAsync(username, username, ct)).UserNum;

        public ValueTask SetWalletAsync(long userNum, int lucci, int cash, CancellationToken ct) =>
            _repos.UpsertAsync(new PlayerWallet(userNum, lucci, cash), ct);

        public ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct) =>
            _repos.FindByUserNumAsync(userNum, ct);

        public ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            _repos.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag, slots, ct);

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct) =>
            _repos.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);

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
