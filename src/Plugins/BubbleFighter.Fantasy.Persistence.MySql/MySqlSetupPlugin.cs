using BubbleFighter.Fantasy.Abstractions;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Validates setup connections and reuses the same migrations as the native MySQL persistence row.</summary>
[Plugin("persistence.mysql.setup", Category = "storage", Names = ["en=MySQL setup", "zh-Hans=MySQL 设置", "zh-Hant=MySQL 設定", "ko=MySQL 설정"], ProvidesMany = [typeof(IInstallationDatabase)])]
public sealed class MySqlSetupPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.RegisterMany<IInstallationDatabase>(new Database(context.Logger, context.Config.Bind<MySqlSetupOptions>())));
        return ValueTask.CompletedTask;
    }

    private sealed class Database(ILogger logger, MySqlSetupOptions setup) : IInstallationDatabase
    {
        public string Kind => "mysql";

        public async ValueTask<IReadOnlyDictionary<string, object?>> PrepareAsync(InstallationSettings settings, bool initialize,
            CancellationToken cancellationToken)
        {
            var builder = Connection(settings);
            MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, logger);
            var options = new MySqlOptions { ConnectionString = builder.ConnectionString, ConnectTimeoutSeconds = setup.ConnectTimeoutSeconds };
            if (options.Validate().Any()) throw new ArgumentException("mysql_configuration_invalid");
            // A fresh database server has no game database yet: the probe proves the account can sign in, and the apply
            // creates the database, so an operator never has to run CREATE DATABASE by hand.
            if (!await SchemaExistsAsync(builder, cancellationToken).ConfigureAwait(false))
            {
                if (!initialize) return Result(builder);
                await ServerCommandAsync(builder, $"CREATE DATABASE IF NOT EXISTS {Quote(builder.Database)} " +
                    "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", cancellationToken).ConfigureAwait(false);
            }
            var factory = new MySqlConnectionFactory(builder.ConnectionString);
            await factory.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (initialize)
                await new MigrationRunner(factory, logger, typeof(MySqlPlugin).Assembly).ApplyAsync(cancellationToken).ConfigureAwait(false);
            return Result(builder);
        }

        public async ValueTask<InstallationDatabaseContents?> InspectAsync(InstallationSettings settings, CancellationToken cancellationToken)
        {
            var builder = Connection(settings);
            if (!await SchemaExistsAsync(builder, cancellationToken).ConfigureAwait(false)) return new(false, 0, null);
            await using var connection = await OpenServerAsync(builder, cancellationToken).ConfigureAwait(false);
            var tables = await TablesAsync(connection, builder.Database, cancellationToken).ConfigureAwait(false);
            long? accounts = null;
            if (tables.Contains("accounts", StringComparer.OrdinalIgnoreCase))
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM {Quote(builder.Database)}.accounts";
                accounts = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            return new(true, tables.Count, accounts);
        }

        public async ValueTask ClearAsync(InstallationSettings settings, CancellationToken cancellationToken)
        {
            var builder = Connection(settings);
            if (!await SchemaExistsAsync(builder, cancellationToken).ConfigureAwait(false)) return;
            await using var connection = await OpenServerAsync(builder, cancellationToken).ConfigureAwait(false);
            var tables = await TablesAsync(connection, builder.Database, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Setup is replacing the existing data of database {Database}: dropping {Count} tables.", builder.Database, tables.Count);
            // Dropping the tables instead of the database keeps its grants and needs no DROP DATABASE privilege.
            await using var command = connection.CreateCommand();
            command.CommandText = "SET FOREIGN_KEY_CHECKS = 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var table in tables)
            {
                command.CommandText = $"DROP TABLE IF EXISTS {Quote(builder.Database)}.{Quote(table)}";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            command.CommandText = "SET FOREIGN_KEY_CHECKS = 1";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private MySqlConnectionStringBuilder Connection(InstallationSettings settings)
        {
            if (!Enum.TryParse<MySqlSslMode>(settings.MySqlSslMode, false, out var ssl) ||
                ssl is not (MySqlSslMode.None or MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull))
                throw new ArgumentException("mysql_tls_invalid");
            return new MySqlConnectionStringBuilder
            {
                Server = settings.MySqlHost, Port = checked((uint)settings.MySqlPort),
                Database = settings.MySqlDatabase, UserID = settings.MySqlUser, Password = settings.MySqlPassword,
                SslMode = ssl, PersistSecurityInfo = false, ConnectionTimeout = (uint)setup.ConnectTimeoutSeconds,
                DefaultCommandTimeout = (uint)setup.CommandTimeoutSeconds, Pooling = false,
            };
        }

        private Dictionary<string, object?> Result(MySqlConnectionStringBuilder builder) => new()
        {
            ["connection_string"] = builder.ConnectionString, ["auto_migrate"] = true, ["connect_timeout_seconds"] = setup.ConnectTimeoutSeconds,
        };

        // Connects without selecting the database, so a missing database is an answer instead of an error.
        private static async Task<MySqlConnection> OpenServerAsync(MySqlConnectionStringBuilder builder, CancellationToken cancellationToken)
        {
            var server = new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" };
            var connection = new MySqlConnection(server.ConnectionString);
            try { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return connection; }
            catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
        }

        private static async Task<bool> SchemaExistsAsync(MySqlConnectionStringBuilder builder, CancellationToken cancellationToken)
        {
            await using var connection = await OpenServerAsync(builder, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
            command.Parameters.AddWithValue("@name", builder.Database);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        }

        private static async Task ServerCommandAsync(MySqlConnectionStringBuilder builder, string sql, CancellationToken cancellationToken)
        {
            await using var connection = await OpenServerAsync(builder, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<List<string>> TablesAsync(MySqlConnection connection, string database, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @name";
            command.Parameters.AddWithValue("@name", database);
            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
            return tables;
        }

        private static string Quote(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";
    }
}

/// <summary>Bounds database connections and commands performed by the setup provider.</summary>
public sealed class MySqlSetupOptions
{
    /// <summary>Gets or sets the maximum time to establish one database connection.</summary>
    [Range(1, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets the maximum time for one setup probe or migration command.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}
