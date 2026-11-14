using System.Data;
using MySqlConnector;
using SqlSugar;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks the ordinary key watermark's metadata, account predicates and guards without opening MySQL.</summary>
public sealed class InventorySlotKeyTests
{
    /// <summary>The watermark uses a current account read and an exact signed 64-bit compare-and-update.</summary>
    [Fact]
    public void WatermarkStatementsBindTheOwnerExpectedValueAndFullSignedRange()
    {
        using var db = Client(inTransaction: true);
        var read = MySqlInventorySlotKeys.ReadQuery(db, 7).ToSql();
        Assert.Contains("`last_inventory_slot_key`", read.Key, StringComparison.Ordinal);
        Assert.Contains("`players`", read.Key, StringComparison.Ordinal);
        Assert.EndsWith("for update", read.Key.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(read.Value, parameter => Equals(parameter.Value, 7L));
        var history = MySqlInventorySlotKeys.PurchaseKeyQuery(db, 7).ToSql();
        Assert.Contains("`last_slot_key` DESC", history.Key, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("for update", history.Key.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(history.Value, parameter => Equals(parameter.Value, 7L));
        var active = MySqlInventorySlotKeys.ActiveKeyQuery(db, 7).ToSql();
        Assert.Contains("`slot_key` DESC", active.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`bag`", active.Key, StringComparison.Ordinal);
        Assert.Contains(" OR ", active.Key, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("for update", active.Key.Trim(), StringComparison.OrdinalIgnoreCase);
        var update = MySqlInventorySlotKeys.AdvanceStatement(db, 7, 9001, long.MaxValue).ToSql();
        var predicate = update.Key[update.Key.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];
        Assert.Contains("`user_num`", predicate, StringComparison.Ordinal);
        Assert.Contains("`last_inventory_slot_key`", predicate, StringComparison.Ordinal);
        Assert.Contains(update.Value, parameter => Equals(parameter.Value, long.MaxValue));
        Assert.Contains(update.Value, parameter => Equals(parameter.Value, 9001L));
        Assert.Contains(update.Value, parameter => Equals(parameter.Value, 7L));
        Assert.DoesNotContain("purchase_log", update.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("pet_identity_allocator", update.Key, StringComparison.Ordinal);
    }

    /// <summary>Missing transactions, invalid owners and backwards movement are rejected before a statement runs.</summary>
    [Fact]
    public async Task WatermarkGuardsRefuseInvalidUpdatesAndCancellationBeforeDatabaseAccess()
    {
        using var outside = Client(inTransaction: false);
        Assert.Throws<SqlSugarException>(() => MySqlInventorySlotKeys.ReadQuery(outside, 7));
        Assert.Throws<SqlSugarException>(() => MySqlInventorySlotKeys.PurchaseKeyQuery(outside, 7));
        Assert.Throws<SqlSugarException>(() => MySqlInventorySlotKeys.ActiveKeyQuery(outside, 7));
        Assert.Throws<InvalidOperationException>(() => MySqlInventorySlotKeys.AdvanceStatement(outside, 7, 0, 1));
        using var db = Client(inTransaction: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => MySqlInventorySlotKeys.ReadQuery(db, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MySqlInventorySlotKeys.AdvanceAsync(db, 7, -1, 0, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MySqlInventorySlotKeys.AdvanceAsync(db, 7, 9001, 9000, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MySqlInventorySlotKeys.ReadAsync(db, 7, new(true)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MySqlInventorySlotKeys.AdvanceAsync(db, 7, 9001, 9002, new(true)).AsTask());
        await MySqlInventorySlotKeys.AdvanceAsync(db, 7, 9001, 9001, default);
    }

    /// <summary>The additive migration preserves retired purchase keys and active keys from either bag without fabricating audit rows.</summary>
    [Fact]
    public void MigrationBackfillsBothBagsAndHistoricalPurchaseWatermarks()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), resource =>
            resource.EndsWith(".0042_inventory_slot_key_watermark.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        Assert.Contains("ALTER TABLE players ADD COLUMN last_inventory_slot_key BIGINT NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_purchase_log_user_last_slot_key ON purchase_log (user_num, last_slot_key)", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(last_slot_key)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM purchase_log GROUP BY user_num", sql, StringComparison.Ordinal);
        Assert.Contains("MAX(slot_key)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM inventory_slots WHERE bag IN (0, 1) GROUP BY user_num", sql, StringComparison.Ordinal);
        Assert.Contains("GREATEST(player.last_inventory_slot_key", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(purchased.last_slot_key, 0), COALESCE(owned.last_slot_key, 0)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pet_identity_allocator", sql, StringComparison.Ordinal);
    }

    private static SqlSugarClient Client(bool inTransaction)
    {
        var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        if (inTransaction) db.Ado.Transaction = new NoopTransaction();
        return db;
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
