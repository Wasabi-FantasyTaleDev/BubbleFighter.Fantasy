using System.Data;
using System.Reflection;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.MySql;
using MySqlConnector;
using SqlSugar;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

// Renders the statements the repositories run and checks entity maps against the migrations. SqlSugar binds
// to a real MySqlConnection, so nothing here opens a connection: SQL is rendered offline.
public sealed class SugarStatementTests
{
    private static SqlSugarClient Client(bool inTransaction = false)
    {
        var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        if (inTransaction)
            db.Ado.Transaction = new NoopTransaction();
        return db;
    }

    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void PlayerLockIsAnUpdateLockingReadAndRefusesToRunOutsideATransaction()
    {
        var sql = Normalize(MySqlPlayerLock.LockQuery(Client(inTransaction: true), 7).ToSql().Key);
        Assert.StartsWith("SELECT `user_num` FROM `players`", sql, StringComparison.Ordinal);
        Assert.EndsWith("for update", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SqlSugarException>(() => MySqlPlayerLock.LockQuery(Client(), 7));
    }

    [Fact]
    public void WalletUpsertIsOneAtomicStatementWithoutTheIdentityProbe()
    {
        var row = new WalletRow { UserNum = 7, Lucci = 5, Cash = 6, UpdatedAt = DateTime.UtcNow };
        var (sql, parameters) = SugarSql.BuildUpsert(Client(), row, [],
            [nameof(WalletRow.Lucci), nameof(WalletRow.Cash)], [(nameof(WalletRow.UpdatedAt), "UTC_TIMESTAMP()")]);
        sql = Normalize(sql);
        Assert.StartsWith("INSERT INTO `player_wallets` (`user_num`,`lucci`,`cash`,`updated_at`) VALUES (@user_num,@lucci,@cash,@updated_at)", sql, StringComparison.Ordinal);
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `lucci` = VALUES(`lucci`), `cash` = VALUES(`cash`), `updated_at` = UTC_TIMESTAMP()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LAST_INSERT_ID", sql, StringComparison.Ordinal);
        Assert.Equal(7L, parameters.Single(p => p.ParameterName == "@user_num").Value);
    }

    [Fact]
    public void PurchaseSeedInsertLeavesUpdatedAtToTheColumnDefaultAndChangesNothingOnConflict()
    {
        var (sql, _) = SugarSql.BuildUpsert(Client(), new WalletRow { UserNum = 7, Lucci = 100, Cash = 0 },
            [nameof(WalletRow.UpdatedAt)], [nameof(WalletRow.UserNum)], []);
        sql = Normalize(sql);
        Assert.Contains("(`user_num`,`lucci`,`cash`) VALUES", sql, StringComparison.Ordinal);
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `user_num` = VALUES(`user_num`)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenUpsertsKeepTheDefaultTimestampOnInsertAndRefreshItOnUpdate()
    {
        var (shop, shopParameters) = SugarSql.BuildUpsert(Client(),
            new ShopSessionRow { UserNum = 7, Token = long.MinValue, ShopType = int.MinValue, Kind = 1 },
            [nameof(ShopSessionRow.OpenedAt)],
            [nameof(ShopSessionRow.Token), nameof(ShopSessionRow.ShopType), nameof(ShopSessionRow.Kind)],
            [(nameof(ShopSessionRow.OpenedAt), "CURRENT_TIMESTAMP")]);
        shop = Normalize(shop);
        Assert.StartsWith("INSERT INTO `shop_sessions` (`user_num`,`token`,`shop_type`,`kind`) VALUES", shop, StringComparison.Ordinal);
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `token` = VALUES(`token`), `shop_type` = VALUES(`shop_type`), `kind` = VALUES(`kind`), `opened_at` = CURRENT_TIMESTAMP", shop, StringComparison.Ordinal);
        Assert.Equal(long.MinValue, shopParameters.Single(p => p.ParameterName == "@token").Value);
        Assert.Equal(int.MinValue, shopParameters.Single(p => p.ParameterName == "@shop_type").Value);

        var (clan, _) = SugarSql.BuildUpsert(Client(), new ClanOpenRow { UserNum = 7, Kind = 2 },
            [nameof(ClanOpenRow.OpenedAt)], [nameof(ClanOpenRow.Kind)], [(nameof(ClanOpenRow.OpenedAt), "CURRENT_TIMESTAMP")]);
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `kind` = VALUES(`kind`), `opened_at` = CURRENT_TIMESTAMP", Normalize(clan), StringComparison.Ordinal);
    }

    [Fact]
    public void FighterUpsertUpdatesEveryMutableColumn()
    {
        var row = new FighterRow { UserNum = 7, Slot = 1, CharacterType = 3, Level = 2, EquippedItemId = 300003, Nickname = "Dao", UpdatedAt = DateTime.UtcNow };
        var (sql, _) = SugarSql.BuildUpsert(Client(), row, [],
            [nameof(FighterRow.CharacterType), nameof(FighterRow.Level), nameof(FighterRow.EquippedItemId), nameof(FighterRow.Nickname)],
            [(nameof(FighterRow.UpdatedAt), "UTC_TIMESTAMP()")]);
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `character_type` = VALUES(`character_type`), `level` = VALUES(`level`), "
            + "`equipped_item_id` = VALUES(`equipped_item_id`), `nickname` = VALUES(`nickname`), `updated_at` = UTC_TIMESTAMP()",
            Normalize(sql), StringComparison.Ordinal);
    }

    [Fact]
    public void WalletCreditIsAnInDatabaseIncrementNotAReadModifyWrite()
    {
        var db = Client();
        var earned = 40;
        var sql = Normalize(db.Updateable<WalletRow>()
            .SetColumns(w => w.Lucci == w.Lucci + earned)
            .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
            .Where(w => w.UserNum == 7L).ToSql().Key);
        Assert.Contains("`lucci` =( `lucci` + @Lucci0 )", sql.Replace("(  ", "( "), StringComparison.Ordinal);
        Assert.Contains("`updated_at` =UTC_TIMESTAMP(", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void WearUpdateTouchesOnlyTheWornBitAndNeverTheLifetime(bool worn, int setValue)
    {
        var statement = MySqlInventoryRepository.WornUpdate(Client(), 1, 100001, worn).ToSql();
        var sql = Normalize(statement.Key);
        // The stored flags are masked with ~1 (bound as -2) and or-ed with the set value, so only the worn bit moves.
        Assert.Contains("`flags` =( ( `flags` & @", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, -2));
        Assert.Contains(statement.Value, p => Equals(p.Value, setValue));
        Assert.DoesNotContain("lifetime_kind", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("expires_at_unix_ms", sql, StringComparison.Ordinal);
        Assert.Contains("`updated_at` =UTC_TIMESTAMP(", sql, StringComparison.Ordinal);
        Assert.Contains("`item_id` =", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LifeUpdateLowersOnlyLifeRowsOfThePlayerInBothBagsAndNeverBelowZero()
    {
        var statement = MySqlInventoryRepository.LifeUpdate(Client(), 7, 3).ToSql();
        var sql = Normalize(statement.Key);
        // Same rule as GREATEST(quantity - 3, 0), written as a conditional with the day count bound as a parameter.
        Assert.Contains("CASE WHEN", sql, StringComparison.Ordinal);
        Assert.Contains("`quantity` -", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GREATEST", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" - 3", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, 3));
        Assert.Contains("`updated_at` =UTC_TIMESTAMP(", sql, StringComparison.Ordinal);
        Assert.Contains("`user_num` =", sql, StringComparison.Ordinal);
        Assert.Contains("`lifetime_kind` =", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("expires_at_unix_ms", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, (int)InventoryLifetimeKind.Life));
        Assert.Throws<ArgumentOutOfRangeException>(() => MySqlInventoryRepository.LifeUpdate(Client(), 7, 0));
    }

    [Fact]
    public void EndedRowsAreTheDeadlinesPassedAndTheLifeRowsAtZeroInOnePredicateForTheReadAndTheDelete()
    {
        var ended = MySqlInventoryRepository.EndedRows(7, 1_000_000);
        var sql = Normalize(Client().Queryable<InventorySlotRow>().Where(ended).ToSql().Key);
        Assert.Contains("`expires_at_unix_ms` <=", sql, StringComparison.Ordinal);
        Assert.Contains("`quantity` <=", sql, StringComparison.Ordinal);
        Assert.Contains(" OR ", sql, StringComparison.OrdinalIgnoreCase);
        var delete = Normalize(Client().Deleteable<InventorySlotRow>().Where(ended).ToSql().Key);
        Assert.StartsWith("DELETE FROM `inventory_slots`", delete, StringComparison.Ordinal);
        Assert.Contains("`quantity` <=", delete, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetedExpiryDeleteBindsOwnerIdentityAndDeadlineAndKeepsTheEndedPredicate()
    {
        using var client = Client();
        var statement = MySqlInventoryRepository.RemoveExpiredItemStatement(client, 7, 202001, 1_000_000).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("DELETE FROM `inventory_slots`", sql, StringComparison.Ordinal);
        Assert.Contains("`user_num` =", sql, StringComparison.Ordinal);
        Assert.Contains("`item_id` =", sql, StringComparison.Ordinal);
        Assert.Contains("`bag`", sql, StringComparison.Ordinal);
        Assert.Contains("`expires_at_unix_ms` <=", sql, StringComparison.Ordinal);
        Assert.Contains("`quantity` <=", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, 7L));
        Assert.Contains(statement.Value, p => Equals(p.Value, 202001));
        Assert.Contains(statement.Value, p => Equals(p.Value, 1_000_000L));
        Assert.DoesNotContain("202001", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLifeMigrationAddsTheLoginDayColumnAndWidensTheLifetimeCheck()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0027_inventory_life.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var script = reader.ReadToEnd();
        Assert.Contains("ADD COLUMN last_login_day DATE NULL", script, StringComparison.Ordinal);
        Assert.Contains("lifetime_kind IN (0, 1, 3) AND expires_at_unix_ms IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT ck_inventory_lifetime", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EquipmentUpdateChangesAllSlotsInOneStatementBoundToParameters()
    {
        var (sql, parameters) = MySqlInventoryRepository.BuildWornUpdate(1, [(0, 3, (byte)1), (1, 0, (byte)0)]);
        Assert.Single(Regex(sql, "UPDATE"));
        Assert.Contains("(bag, ordinal) IN ((@bag0, @ordinal0), (@bag1, @ordinal1))", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN bag = @bag1 AND ordinal = @ordinal1 THEN @flags1", sql, StringComparison.Ordinal);
        Assert.Equal(7, parameters.Count);
        Assert.Equal(3, parameters.Single(p => p.ParameterName == "@ordinal0").Value);
    }

    [Fact]
    public void PurchaseUpdatesExistingRowByKeyAndNeverRewritesFlagsOrOrdinal()
    {
        var expiry = InventoryLifetime.Until(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var slot = new InventorySlotRecord(1, InventoryBag.CharacterBag, 10, 100001, 10, 0x81) { Lifetime = expiry };
        var statement = MySqlShopPurchaseStore.SlotUpdate(Client(), slot).ToSql();
        var sql = Normalize(statement.Key);
        Assert.Contains("`quantity` =", sql, StringComparison.Ordinal);
        Assert.Contains("`lifetime_kind` =", sql, StringComparison.Ordinal);
        Assert.Contains("`expires_at_unix_ms` =", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`flags`", sql.Split("WHERE")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("`ordinal`", sql.Split("WHERE")[0], StringComparison.Ordinal);
        Assert.Contains("`slot_key` =", sql.Split("WHERE")[1], StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, expiry.ExpiresAtUnixMilliseconds));
        // A permanent lifetime carries no deadline, which must render as a literal NULL rather than fail.
        var permanent = MySqlShopPurchaseStore.SlotUpdate(Client(), slot with { Lifetime = InventoryLifetime.Permanent }).ToSql();
        Assert.Contains("`expires_at_unix_ms` = NULL", Normalize(permanent.Key), StringComparison.Ordinal);
    }

    [Fact]
    public void ChargeWritesAbsoluteBalancesForTheLockedWalletRow()
    {
        var statement = MySqlShopPurchaseStore.ChargeUpdate(Client(), new PlayerWallet(7, 90, 3)).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("UPDATE `player_wallets` SET", sql, StringComparison.Ordinal);
        Assert.Contains("`lucci` =", sql, StringComparison.Ordinal);
        Assert.Contains("`cash` =", sql, StringComparison.Ordinal);
        Assert.Contains("`updated_at` =UTC_TIMESTAMP(", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE ( `user_num` = @UserNum", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, 90));
        Assert.Contains(statement.Value, p => Equals(p.Value, 3));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(2, 1791158400000L)]
    public void StoredLifetimeSurvivesRowRecordRoundTrips(int kind, long? deadline)
    {
        var row = new InventorySlotRow { UserNum = 1, Bag = 0, Ordinal = 4, SlotKey = 1000, ItemId = 100001, Quantity = 1, Flags = 0, LifetimeKind = kind, ExpiresAtUnixMilliseconds = deadline };
        var record = MySqlInventoryRepository.ToRecord(row);
        Assert.Equal(new InventoryLifetime((InventoryLifetimeKind)kind, deadline), record.Lifetime);
        var back = MySqlInventoryRepository.ToRow(record, 4);
        Assert.Equal(kind, back.LifetimeKind);
        Assert.Equal(deadline, back.ExpiresAtUnixMilliseconds);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(0, 1L)]
    [InlineData(9, null)]
    public void CorruptStoredLifetimeFailsLoudBeforeAnyMutation(int kind, long? deadline)
    {
        var row = new InventorySlotRow { UserNum = 1, SlotKey = 1000, ItemId = 100001, Quantity = 1, LifetimeKind = kind, ExpiresAtUnixMilliseconds = deadline };
        Assert.ThrowsAny<ArgumentException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    [Fact]
    public void ClanAndShopRowsRejectUndefinedKinds()
    {
        Assert.Equal(new ClanOpenRecord(7, ClanOpenKind.Attendance), MySqlClanStore.ToRecord(7, 2));
        Assert.Throws<InvalidOperationException>(() => MySqlClanStore.ToRecord(7, 99));
        var row = new ShopSessionRow { UserNum = 7, Token = long.MaxValue, ShopType = int.MinValue, Kind = 1 };
        Assert.Equal(new ShopOpenRecord(7, long.MaxValue, int.MinValue, ShopKind.CashShop), MySqlShopSessionStore.ToRecord(7, row));
        row.Kind = 2;
        Assert.Throws<InvalidOperationException>(() => MySqlShopSessionStore.ToRecord(7, row));
    }

    [Fact]
    public void ClanSnapshotsFailInsteadOfTruncating()
    {
        Assert.Equal(new long[] { 2, 5 }, MySqlClanStore.BoundedSnapshot([2, 5], 2));
        Assert.Throws<InvalidOperationException>(() => MySqlClanStore.BoundedSnapshot([2, 5, 8], 2));
    }

    [Fact]
    public void EveryMappedTableAndColumnExistsInTheMigrations()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var script = string.Concat(assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            }));
        var db = Client();
        var entities = assembly.GetTypes().Where(t => t.GetCustomAttribute<SugarTable>() is not null).ToArray();
        Assert.Equal(19, entities.Length);
        foreach (var entity in entities)
        {
            var info = db.EntityMaintenance.GetEntityInfo(entity);
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {info.DbTableName} ", script, StringComparison.Ordinal);
            var table = script[script.IndexOf($"CREATE TABLE IF NOT EXISTS {info.DbTableName} ", StringComparison.Ordinal)..];
            table = table[..table.IndexOf(") ENGINE", StringComparison.Ordinal)];
            foreach (var column in info.Columns.Where(c => !c.IsIgnore))
            {
                var created = table.Contains($"    {column.DbColumnName} ", StringComparison.Ordinal);
                var added = script.Contains($"ADD COLUMN {column.DbColumnName} ", StringComparison.Ordinal)
                    || script.Contains($"ADD COLUMN IF NOT EXISTS {column.DbColumnName} ", StringComparison.Ordinal)
                    || script.Contains($"CHANGE COLUMN unknown1 {column.DbColumnName} ", StringComparison.Ordinal)
                    || script.Contains($"CHANGE COLUMN unknown2 {column.DbColumnName} ", StringComparison.Ordinal);
                Assert.True(created || added, $"{info.DbTableName}.{column.DbColumnName} is not defined by any migration.");
            }
        }
    }

    private static IEnumerable<System.Text.RegularExpressions.Match> Regex(string input, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(input, pattern);

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-1)]
    public void TutorialBestRecordKeepsTheLargerCountAndTheSmallerPositiveTime(int seconds)
    {
        var statement = MySqlTutorialStages.BestRecordUpdate(Client(), 7, new TutorialStageClear(3, 5, seconds)).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("UPDATE `tutorial_stage_clears` SET `best_count` =( CASE WHEN ( `best_count` > @BestCount0 )", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE (( `user_num` =", sql, StringComparison.Ordinal);
        Assert.Contains("`best_seconds` =( CASE WHEN ( `best_seconds` <= @", sql, StringComparison.Ordinal);
        if (seconds > 0)
            Assert.Contains("`best_seconds` < @", sql, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("`best_seconds` < @", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, 5));
        Assert.Contains(statement.Value, p => Equals(p.Value, seconds));
    }

    [Fact]
    public void LauncherRotationIsOneCompareAndSetOnTheCurrentHashAndALiveSession()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var statement = MySqlLauncherSessions.RotateUpdate(Client(), "sid", "current", "next", now.AddDays(1), now).ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("UPDATE `launcher_sessions` SET `token_hash` = ", sql, StringComparison.Ordinal);
        Assert.Contains("`rotated_at` =", sql, StringComparison.Ordinal);
        Assert.Contains("`session_id` =", sql, StringComparison.Ordinal);
        Assert.Contains("`token_hash` =", sql.Substring(sql.IndexOf(" WHERE", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("`revoked_at` IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("`expires_at` >", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, "next"));
        Assert.Contains(statement.Value, p => Equals(p.Value, "current"));
    }

    [Fact]
    public void LauncherAccountLookupIsOneUnionOfAJoinedAccountAndAnUnaccountedPlayer()
    {
        var statement = MySqlLauncherAccounts.AccountLookup(Client(), "u").Take(1).ToSql();
        var sql = Normalize(statement.Key);
        Assert.Contains("UNION ALL", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.EndsWith("LIMIT 0,1", sql, StringComparison.Ordinal);
        Assert.Equal(2, statement.Value.Count(p => Equals(p.Value, "u")));
    }

    [Fact]
    public void InitialPasswordUpdateOnlyWritesAnEmptyHashAndBindsTheNewOne()
    {
        var statement = MySqlLauncherAccounts.InitialPasswordUpdate(Client(), "u", "hash").ToSql();
        var sql = Normalize(statement.Key);
        Assert.StartsWith("UPDATE `accounts` SET `password_hash` = @", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(`password_hash`) =", sql, StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, "hash"));
    }

    [Fact]
    public void ExpiredRefreshDeleteIncludesTheExpiryBoundary()
    {
        var now = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var statement = MySqlLauncherSessions.ExpiredDelete(Client(), now).ToSql();
        Assert.StartsWith("DELETE FROM `launcher_sessions`", Normalize(statement.Key));
        Assert.Contains("`expires_at` <=", statement.Key);
        Assert.Contains(statement.Value, parameter => Equals(parameter.Value, now));
    }

    [Fact]
    public void SessionLimitRetainsNewestLiveAccountRowsAndRevokesOnlyExcessRows()
    {
        var now = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var query = MySqlLauncherSessions.RetainedQuery(Client(), "alice", now, 7).ToSql();
        var sql = Normalize(query.Key);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("`created_at` DESC", sql);
        Assert.Contains("`session_id` DESC", sql);
        Assert.EndsWith("LIMIT 0,7", sql);
        Assert.Contains(query.Value, parameter => Equals(parameter.Value, "alice"));
        var revoke = MySqlLauncherSessions.RevokeExcessUpdate(Client(), "alice", ["keep"], now).ToSql();
        var update = Normalize(revoke.Key);
        Assert.Contains("`revoked_at` IS NULL", update);
        Assert.Contains("NOT", update);
        Assert.Contains(revoke.Value, parameter => Equals(parameter.Value, "alice"));
        var none = MySqlLauncherSessions.RevokeExcessUpdate(Client(), "alice", [], now).ToSql();
        Assert.DoesNotContain("IN ()", none.Key);
    }

    private sealed class NoopTransaction : IDbTransaction
    {
        public IDbConnection? Connection => null;
        public IsolationLevel IsolationLevel => IsolationLevel.RepeatableRead;
        public void Commit() { }
        public void Rollback() { }
        public void Dispose() { }
    }
}
