using BubbleFighter.Fantasy.Persistence.MySql;
using Xunit;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class MySqlOptionsTests
{
    [Fact]
    public void ValidateAcceptsTheDefaultDatabaseName()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=127.0.0.1;Port=3306;User ID=bff;Password=secret;Database=bf_game_db",
        };

        Assert.Empty(options.Validate());
        Assert.Equal("bf_game_db", options.CreateBuilder().Database);
    }

    [Fact]
    public void ValidateRejectsMissingPassword()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=127.0.0.1;Port=3306;User ID=bff;Database=bf_game_db",
        };

        var failures = options.Validate().ToList();
        Assert.Contains(failures, f => f.ErrorMessage is not null
            && f.ErrorMessage.Contains("BFF_MYSQL_PASSWORD", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsAnyDatabaseName()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=127.0.0.1;Port=3306;User ID=bff;Password=secret;Database=my_server",
        };

        Assert.Empty(options.Validate());
        Assert.Equal("my_server", options.CreateBuilder().Database);
    }

    [Fact]
    public void ValidateRejectsMissingDatabaseName()
    {
        var options = new MySqlOptions
        {
            ConnectionString = "Server=127.0.0.1;Port=3306;User ID=bff;Password=secret",
        };

        Assert.Contains(options.Validate(), f => f.ErrorMessage is not null
            && f.ErrorMessage.Contains("BFF_MYSQL_DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsEmptyConnectionString()
    {
        var options = new MySqlOptions { ConnectionString = "" };
        var failures = options.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(MySqlOptions.ConnectionString)));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("localhost", false)]
    [InlineData("192.0.2.10", true)]
    [InlineData("db.example.test", true)]
    public void WarnsOnlyForRemoteHostsWithTlsDisabled(string server, bool expected)
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = server, Port = 3306, UserID = "bff", Password = "secret", Database = "bf_game_db",
            SslMode = MySqlConnector.MySqlSslMode.None,
        };
        Assert.Equal(expected, MySqlConnectionFactory.IsUnencryptedRemote(builder));
        var logger = new WarningLogger();
        MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, logger);
        Assert.Equal(expected ? 1 : 0, logger.Messages.Count);
        if (expected)
        {
            Assert.Contains(server, Assert.Single(logger.Messages), StringComparison.Ordinal);
            Assert.Contains("unencrypted", logger.Messages[0], StringComparison.Ordinal);
            Assert.DoesNotContain("secret", logger.Messages[0], StringComparison.Ordinal);
        }
        builder.SslMode = MySqlConnector.MySqlSslMode.Required;
        Assert.False(MySqlConnectionFactory.IsUnencryptedRemote(builder));
        MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, logger);
        Assert.Equal(expected ? 1 : 0, logger.Messages.Count);
    }

    private sealed class WarningLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
