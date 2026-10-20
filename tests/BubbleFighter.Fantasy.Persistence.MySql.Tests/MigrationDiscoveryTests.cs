using BubbleFighter.Fantasy.Persistence.MySql;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void DiscoverMigrationsIncludesPlayableSchema()
    {
        var runner = new MigrationRunner(new UnusedFactory(), NullLogger.Instance, typeof(MySqlPlugin).Assembly);
        var versions = runner.DiscoverMigrations().Select(m => m.Version).ToArray();

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 11, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 }, versions);
    }

    [Fact]
    public void EmbeddedSqlCreatesDocumentedTables()
    {
        var sql = ReadAllEmbeddedSql();

        foreach (var table in new[]
        {
            "schema_migrations",
            "accounts",
            "players",
            "player_onboarding",
            "fighters",
            "inventory_slots",
            "equipped_slots",
            "character_item_ids",
            "cash_inventory_slots",
            "player_money",
            "player_wallets",
            "create_user_submits",
            "shop_sessions",
            "clans",
            "clan_members",
            "clan_opens",
            "presents",
            "present_claims",
        })
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS shop_catalog", sql, StringComparison.Ordinal);
    }

    private static string ReadAllEmbeddedSql()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var parts = new List<string>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing resource '{name}'.");
            using var reader = new StreamReader(stream);
            parts.Add(reader.ReadToEnd());
        }

        return string.Join('\n', parts);
    }

    private sealed class UnusedFactory : BubbleFighter.Fantasy.Abstractions.Persistence.IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Discovery does not open a connection.");
    }
}
