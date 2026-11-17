using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks MySQL user-host escrow/claim statement SQL, locking clauses, and pre-open validation.</summary>
public sealed class UserHostPrizeStorageTests
{
    [Fact]
    public void EscrowTicketWalletAndClaimQueriesApplyProfileFilterAndRowLocks()
    {
        using var conn = new StubConnection();
        var db = SugarSession.Create(conn);

        Assert.Throws<SqlSugar.SqlSugarException>(() =>
            MySqlUserHostPrizeStore.EscrowQuery(db, "kr-812", "match-1").TranLock(SqlSugar.DbLockType.Wait));

        db.Ado.Transaction = new NoopTransaction();
        var (escrowSql, _) = MySqlUserHostPrizeStore.EscrowQuery(db, "kr-812", "match-1")
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("user_host_escrows", escrowSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", escrowSql, StringComparison.OrdinalIgnoreCase);

        var (activeSql, _) = MySqlUserHostPrizeStore.ActiveEscrowsQuery(db, "kr-812", "node-a")
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("user_host_escrows", activeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("node_id", activeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", activeSql, StringComparison.OrdinalIgnoreCase);

        var (walletSql, _) = MySqlUserHostPrizeStore.WalletQuery(db, 42)
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("player_wallets", walletSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", walletSql, StringComparison.OrdinalIgnoreCase);

        var (ticketSql, _) = MySqlUserHostPrizeStore.TicketQuery(db, 42, 4100101)
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("inventory_slots", ticketSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", ticketSql, StringComparison.OrdinalIgnoreCase);

        var (pendingSql, _) = MySqlUserHostPrizeStore.PendingClaimsQuery(db, "kr-812", 42)
            .TranLock(SqlSugar.DbLockType.Wait).ToSql();
        Assert.Contains("user_host_claims", pendingSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE", pendingSql, StringComparison.OrdinalIgnoreCase);

        var finalizeSql = MySqlUserHostPrizeStore.EscrowFinalizeUpdate(
            db, "kr-812", "match-1", UserHostEscrowState.Settled, 5_000).ToSqlString();
        Assert.Contains("user_host_escrows", finalizeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("state", finalizeSql, StringComparison.OrdinalIgnoreCase);

        var claimUpdateSql = MySqlUserHostPrizeStore.ClaimsMarkClaimedUpdate(db, [10L, 11L], 6_000).ToSqlString();
        Assert.Contains("user_host_claims", claimUpdateSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claimed_at_ms", claimUpdateSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledAndInvalidRequestsFailBeforeOpeningAConnection()
    {
        var store = new MySqlUserHostPrizeStore(new FailingFactory());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.BeginEscrowAsync(new("kr-812", "match-1", 1, 1, 1000, 10, 4100101, 1000), new(true)).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.BeginEscrowAsync(new("", "match-1", 1, 1, 1000, 10, 4100101, 1000), default).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.BeginEscrowAsync(new("kr-812", "match-1", 1, 1, 1000, 101, 4100101, 1000), default).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SettleEscrowAsync(new("kr-812", "match-1", false, [new(0, 100)], [], 1000), default).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.RecoverActiveEscrowsAsync("kr-812", "", 1000, 0, default).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.FlushPendingClaimsAsync("kr-812", 0, 16, 1000, default).AsTask());
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
