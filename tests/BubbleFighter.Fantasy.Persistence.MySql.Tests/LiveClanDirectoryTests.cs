using System.Reflection;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>
/// Runs the shared clan directory contract against a real MySQL or MariaDB server named by
/// <c>BFF_MYSQL_VERIFY_CONNECTION</c> (a disposable database; every case starts from empty clan tables). Without the
/// variable the test does nothing, like the other live database tests.
/// </summary>
[Collection("LiveMySqlDatabase")]
public sealed class LiveClanDirectoryTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BFF_MYSQL_VERIFY_CONNECTION");

    /// <summary>Every case of the shared contract, one after another on freshly emptied clan tables.</summary>
    [Fact]
    public async Task TheSharedContractHoldsOnMySql()
    {
        if (ConnectionString is null) return;
        var factory = new MySqlConnectionFactory(ConnectionString);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        var contract = new Live(factory);
        var cases = typeof(ClanDirectoryContractTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
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
        command.CommandText = "DELETE FROM clan_posts; DELETE FROM clan_members; DELETE FROM clans;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Live(MySqlConnectionFactory factory) : ClanDirectoryContractTests
    {
        protected override ValueTask<IClanDirectory> CreateEmptyAsync() => new(new MySqlClanDirectory(factory));
    }
}
