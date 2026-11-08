using System.Reflection;
using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>
/// Runs the shared messenger store contract against a real MySQL server named by <c>BFF_MYSQL_VERIFY_CONNECTION</c>
/// (a disposable database; every case starts from empty messenger tables). Without the variable the test does nothing,
/// like the other live database tests.
/// </summary>
[Collection("LiveMySqlDatabase")]
public sealed class LiveMessengerStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BFF_MYSQL_VERIFY_CONNECTION");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentResolutionsConsumeOneRequestOnly(bool accept)
    {
        if (ConnectionString is null) return;
        var factory = await PrepareAsync();
        var first = new MySqlMessengerStore(factory);
        var second = new MySqlMessengerStore(factory);
        var owner = await UserAsync(first, "owner");
        var friend = await UserAsync(first, "friend");
        var limits = new MessengerLimits(1, 4, 4);
        var created = await first.CreateRequestAsync(friend.Id, owner.Id, "", DateTime.UtcNow, limits, default);
        var handle = Assert.IsType<MessengerStoredRequest>(created.Request).Handle;
        var results = await ContendAsync(owner.Id,
            token => first.ResolveRequestAsync(handle, owner.Id, accept, limits, token).AsTask(),
            token => second.ResolveRequestAsync(handle, owner.Id, accept, limits, token).AsTask());
        Assert.Single(results, outcome => outcome.Result == MessengerResult.Ok);
        Assert.Single(results, outcome => outcome.Result == MessengerResult.NotFound);
        Assert.Empty(await first.ListIncomingRequestsAsync(owner.Id, default));
        Assert.Equal(accept ? 1 : 0, (await first.LoadRelationsAsync(owner.Id, default)).Relations.Count);
        Assert.Equal(accept ? 1 : 0, (await second.LoadRelationsAsync(friend.Id, default)).Relations.Count);
        await ClearAsync();
    }

    [Fact]
    public async Task ConcurrentAcceptsCannotExceedTheRecipientsFriendLimit()
    {
        if (ConnectionString is null) return;
        var factory = await PrepareAsync();
        var first = new MySqlMessengerStore(factory);
        var second = new MySqlMessengerStore(factory);
        var owner = await UserAsync(first, "owner");
        var one = await UserAsync(first, "one");
        var two = await UserAsync(first, "two");
        var limits = new MessengerLimits(1, 4, 4);
        var requestOne = await first.CreateRequestAsync(one.Id, owner.Id, "", DateTime.UtcNow, limits, default);
        var requestTwo = await second.CreateRequestAsync(two.Id, owner.Id, "", DateTime.UtcNow, limits, default);
        var handleOne = Assert.IsType<MessengerStoredRequest>(requestOne.Request).Handle;
        var handleTwo = Assert.IsType<MessengerStoredRequest>(requestTwo.Request).Handle;
        var results = await ContendAsync(owner.Id,
            token => first.ResolveRequestAsync(handleOne, owner.Id, true, limits, token).AsTask(),
            token => second.ResolveRequestAsync(handleTwo, owner.Id, true, limits, token).AsTask());
        Assert.Single(results, outcome => outcome.Result == MessengerResult.Ok);
        Assert.Single(results, outcome => outcome.Result == MessengerResult.LimitReached);
        Assert.Single((await first.LoadRelationsAsync(owner.Id, default)).Relations);
        Assert.Single(await second.ListIncomingRequestsAsync(owner.Id, default));
        await ClearAsync();
    }

    private static async Task<MySqlConnectionFactory> PrepareAsync()
    {
        var factory = new MySqlConnectionFactory(ConnectionString!);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        await ClearAsync();
        return factory;
    }

    private static ValueTask<MessengerUser> UserAsync(IMessengerStore store, string name) =>
        store.UpsertUserAsync(new("cluster", name, name, 1), default);

    private static async Task<MessengerRequestOutcome[]> ContendAsync(MessengerUserId owner,
        Func<CancellationToken, Task<MessengerRequestOutcome>> first, Func<CancellationToken, Task<MessengerRequestOutcome>> second)
    {
        await using var blocker = new MySqlConnection(ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new MySqlCommand("SELECT id FROM messenger_users WHERE id = @id FOR UPDATE", blocker, transaction))
        {
            hold.Parameters.AddWithValue("@id", owner.Value);
            Assert.Equal(owner.Value, Convert.ToInt64(await hold.ExecuteScalarAsync()));
        }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = new[] { first(lifetime.Token), second(lifetime.Token) };
        var contended = false;
        Exception? observationFailure = null;
        try
        {
            await using var observer = new MySqlConnection(ConnectionString);
            await observer.OpenAsync();
            // InnoDB can queue the second waiter behind the first, so follow the full blocked chain.
            await using var waiters = new MySqlCommand("""
                WITH RECURSIVE wait_chain (trx_id) AS (
                    SELECT trx_id FROM information_schema.INNODB_TRX WHERE trx_mysql_thread_id = @thread
                    UNION
                    SELECT w.requesting_trx_id FROM information_schema.INNODB_LOCK_WAITS w
                    JOIN wait_chain b ON w.blocking_trx_id = b.trx_id
                )
                SELECT COUNT(*) - 1 FROM wait_chain
                """, observer);
            waiters.Parameters.AddWithValue("@thread", blocker.ServerThread);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Convert.ToInt32(await waiters.ExecuteScalarAsync(deadline.Token)) < 2)
            {
                foreach (var task in pending)
                    Assert.False(task.IsCompleted, "Both store instances must wait for the account row lock.");
                await Task.Delay(10, deadline.Token);
            }
            contended = true;
        }
        catch (Exception error)
        {
            observationFailure = error;
            throw;
        }
        finally
        {
            await transaction.CommitAsync();
            // Drain both stores even if observing the contention barrier fails.
            if (!contended) await lifetime.CancelAsync();
            try { await Task.WhenAll(pending); }
            catch (Exception drainFailure) when (observationFailure is not null)
            {
                throw new AggregateException("The contention observer and pending operations failed.", observationFailure, drainFailure);
            }
        }
        return pending.Select(task => task.GetAwaiter().GetResult()).ToArray();
    }

    [Fact]
    public async Task TheSharedContractHoldsOnMySql()
    {
        if (ConnectionString is null) return;
        var factory = new MySqlConnectionFactory(ConnectionString);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        var contract = new Live(factory);
        var cases = typeof(MessengerStoreContractTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null).ToArray();
        Assert.NotEmpty(cases);
        foreach (var method in cases)
        {
            await ClearAsync();
            try { await (Task)method.Invoke(contract, null)!; }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"{method.Name} failed on MySQL: {e.InnerException?.Message ?? e.Message}"); }
        }
        await ClearAsync();
    }

    private static async Task ClearAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messenger_requests; DELETE FROM messenger_categories; DELETE FROM messenger_relations; DELETE FROM messenger_users;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Live(MySqlConnectionFactory factory) : MessengerStoreContractTests
    {
        protected override IMessengerStore CreateStore() => new MySqlMessengerStore(factory);
    }
}
