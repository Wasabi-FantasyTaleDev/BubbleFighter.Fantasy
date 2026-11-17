using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks MySQL cash inbox statement generation, locking clauses, and argument guards without opening a DB connection.</summary>
public sealed class CashInboxStorageTests
{
    [Fact]
    public void ActiveAndSlotQueriesFilterByProfileStateAndApplyRowLocks()
    {
        using var conn = new StubConnection();
        var db = SugarSession.Create(conn);

        var (activeSql, _) = MySqlCashInboxStore.ActiveOwnerQuery(db, "kr-812", 42, 9_000).ToSql();
        Assert.Contains("cash_inbox_entries", activeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile", activeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user_num", activeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires_at_ms", activeSql, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<SqlSugar.SqlSugarException>(() =>
            MySqlCashInboxStore.SlotQuery(db, "kr-812", 77).TranLock(SqlSugar.DbLockType.Wait));

        db.Ado.Transaction = new NoopTransaction();
        var (lockedSlotSql, _) = MySqlCashInboxStore.SlotQuery(db, "kr-812", 77)
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("cash_inbox_entries", lockedSlotSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", lockedSlotSql, StringComparison.OrdinalIgnoreCase);

        var (bagSql, _) = MySqlCashInboxStore.CharacterBagQuery(db, 42)
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("inventory_slots", bagSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", bagSql, StringComparison.OrdinalIgnoreCase);

        var claimSql = MySqlCashInboxStore.StateUpdate(db, "kr-812", 77, CashInboxEntryState.PickedUp).ToSqlString();
        Assert.Contains("cash_inbox_entries", claimSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("state", claimSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledAndInvalidRequestsFailBeforeOpeningAConnection()
    {
        var store = new MySqlCashInboxStore(new FailingFactory());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GrantAsync(new("kr-812", 1, 10, 20, 1, 0, "", 1000, 0, 64), new(true)).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.GrantAsync(new("", 1, 10, 20, 1, 0, "", 1000, 0, 64), default).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PickUpAsync(new("kr-812", 1, 5, new PlayerWallet(1, 0, 0), 1000, [], 2000), default).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListActiveAsync("kr-812", 0, 16, 1000, default).AsTask());
    }

    private sealed class FailingFactory : IDbConnectionFactory
    {
        public ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No database connection should be opened by statement or validation tests.");
    }

    private sealed class StubConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "Server=unused";
        public override string Database => "unused";
        public override string DataSource => "unused";
        public override string ServerVersion => "8.0.0";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
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
