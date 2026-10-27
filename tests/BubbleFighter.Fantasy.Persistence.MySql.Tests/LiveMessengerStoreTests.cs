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
public sealed class LiveMessengerStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BFF_MYSQL_VERIFY_CONNECTION");

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
