using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks title transaction isolation and ownership fencing without opening a database or executing SQL.</summary>
public sealed class TitleTransactionTests
{
    /// <summary>Each title entry point selects statement snapshots that do not gap-lock another account's first ledger.</summary>
    [Theory]
    [InlineData("read")]
    [InlineData("grant")]
    [InlineData("claim")]
    public async Task TitleOperationsSelectReadCommitted(string operation)
    {
        var connection = new RecordingConnection { RefuseBegin = true };
        var store = new MySqlInventoryRepository(new RecordingFactory(connection));
        var error = await Record.ExceptionAsync(() => InvokeAsync(store, operation));
        Assert.NotNull(error);
        Assert.Equal(IsolationLevel.ReadCommitted, connection.RequestedIsolation);
        Assert.Null(connection.Transaction);
        Assert.Equal(0, connection.CommandsCreated);
    }

    /// <summary>Both title mutations pass their exact transaction to the authority fence and roll it back when authority is refused.</summary>
    [Theory]
    [InlineData("grant")]
    [InlineData("claim")]
    public async Task TitleMutationsKeepTheAuthorityFence(string operation)
    {
        var connection = new RecordingConnection();
        var fence = new RecordingFence { Refuse = true };
        var store = new MySqlInventoryRepository(new RecordingFactory(connection), fence);
        await Assert.ThrowsAsync<ProbeException>(() => InvokeAsync(store, operation));
        Assert.Equal(7, fence.UserNum);
        Assert.Same(connection, fence.Connection);
        Assert.Same(connection.Transaction, fence.Transaction);
        Assert.Equal(IsolationLevel.ReadCommitted, fence.Transaction!.IsolationLevel);
        Assert.Equal(1, connection.Transaction!.Rollbacks);
        Assert.Equal(0, connection.Transaction.Commits);
        Assert.Equal(0, connection.CommandsCreated);
    }

    /// <summary>The existing write overload retains REPEATABLE READ and an uncommitted operation still rolls back.</summary>
    [Fact]
    public async Task ExistingWriteOverloadRetainsItsIsolationAndRollback()
    {
        var connection = new RecordingConnection();
        var fence = new RecordingFence();
        await using var session = await SugarSession.OpenAsync(new RecordingFactory(connection), default);
        await using (await session.BeginWriteAsync(7, fence, default)) { }
        Assert.Equal(IsolationLevel.RepeatableRead, connection.RequestedIsolation);
        Assert.Same(connection, fence.Connection);
        Assert.Same(connection.Transaction, fence.Transaction);
        Assert.Equal(1, connection.Transaction!.Rollbacks);
        Assert.Equal(0, connection.Transaction.Commits);
        Assert.Equal(0, connection.CommandsCreated);
    }

    /// <summary>An explicit isolation level preserves the shared transaction's single-commit and no-later-rollback behavior.</summary>
    [Fact]
    public async Task ExplicitWriteIsolationRetainsCommitOwnership()
    {
        var connection = new RecordingConnection();
        var fence = new RecordingFence();
        await using var session = await SugarSession.OpenAsync(new RecordingFactory(connection), default);
        await using (var transaction = await session.BeginWriteAsync(7, fence, IsolationLevel.ReadCommitted, default))
            await transaction.CommitAsync();
        Assert.Equal(IsolationLevel.ReadCommitted, connection.RequestedIsolation);
        Assert.Same(connection.Transaction, fence.Transaction);
        Assert.Equal(1, connection.Transaction!.Commits);
        Assert.Equal(0, connection.Transaction.Rollbacks);
        Assert.Equal(0, connection.CommandsCreated);
    }

    private static async Task InvokeAsync(MySqlInventoryRepository store, string operation)
    {
        switch (operation)
        {
            case "read":
                await store.ReadAsync(7, "kr-1256", default);
                break;
            case "grant":
                await store.GrantAsync(new(7, "kr-1256", "test", "first", [new(30007, 20)]), default);
                break;
            case "claim":
                await store.TryClaimAsync(new(7, new("kr-1256", [400001]), 400001, 1200001, [new(30007, 20)],
                    new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, DateTimeOffset.UnixEpoch), default);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class RecordingFactory(RecordingConnection connection) : IDbConnectionFactory
    {
        public ValueTask<DbConnection> OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<DbConnection>(connection);
        }
    }

    private sealed class RecordingFence : IAccountMutationFence
    {
        public bool Refuse { get; init; }
        public long UserNum { get; private set; }
        public DbConnection? Connection { get; private set; }
        public DbTransaction? Transaction { get; private set; }

        public ValueTask VerifyAsync(DbConnection connection, DbTransaction transaction, long userNum, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection = connection;
            Transaction = transaction;
            UserNum = userNum;
            if (Refuse) throw new ProbeException();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;
        public bool RefuseBegin { get; init; }
        public IsolationLevel? RequestedIsolation { get; private set; }
        public RecordingTransaction? Transaction { get; private set; }
        public int CommandsCreated { get; private set; }
        [AllowNull]
        public override string ConnectionString { get; set; } = "Server=unused;Database=transaction_probe";
        public override string Database => "transaction_probe";
        public override string DataSource => "unused";
        public override string ServerVersion => "8.0.0";
        public override ConnectionState State => _state;
        public override void Open() => _state = ConnectionState.Open;
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            RequestedIsolation = isolationLevel;
            if (RefuseBegin) throw new ProbeException();
            return Transaction = new(this, isolationLevel);
        }

        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BeginDbTransaction(isolationLevel));
        }

        protected override DbCommand CreateDbCommand()
        {
            CommandsCreated++;
            throw new InvalidOperationException("The transaction probe must stop before executing SQL.");
        }
    }

    private sealed class RecordingTransaction(DbConnection connection, IsolationLevel isolation) : DbTransaction
    {
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }
        public override IsolationLevel IsolationLevel => isolation;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Commits++;
        public override void Rollback() => Rollbacks++;
    }

    private sealed class ProbeException : Exception { }
}
