using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>
/// The persistence plugin ships the ORM without its bundled drivers. A statement must still reach the MySQL driver the
/// plugin references: executing one creates the driver's command and opens its connection, so a missing or
/// unloadable driver fails here as a load error instead of as the driver's own connection error.
/// </summary>
public sealed class DriverResolutionTests
{
    /// <summary>A query against a closed port fails inside MySqlConnector, which proves the provider reached it.</summary>
    [Fact]
    public void StatementsRunThroughTheReferencedMySqlDriver()
    {
        using var connection = new MySqlConnection("Server=127.0.0.1;Port=1;User ID=nobody;Password=none;Connection Timeout=2");
        using var db = SugarSession.Create(connection);
        var error = Assert.ThrowsAny<Exception>(() => { db.Ado.GetInt("SELECT 1"); });
        // The ORM wraps the driver's failure without keeping it as the inner exception; the text is the driver's own.
        // Whether the refused port or the two-second timeout reports first depends on the machine's load.
        Assert.True(error is MySqlException || error.InnerException is MySqlException
            || error.Message.Contains("Connect Timeout expired", StringComparison.Ordinal)
            || error.Message.Contains("Unable to connect to any of the specified MySQL hosts", StringComparison.Ordinal),
            $"Expected the driver's own connection error, got {error.GetType().FullName}: {error.Message}");
    }
}
