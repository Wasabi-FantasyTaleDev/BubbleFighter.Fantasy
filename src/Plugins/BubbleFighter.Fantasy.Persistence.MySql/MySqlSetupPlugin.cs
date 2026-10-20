using BubbleFighter.Fantasy.Abstractions;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Validates setup connections and reuses the same migrations as the native MySQL persistence row.</summary>
[Plugin("persistence.mysql.setup", ProvidesMany = [typeof(IInstallationDatabase)])]
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
            if (!Enum.TryParse<MySqlSslMode>(settings.MySqlSslMode, false, out var ssl) ||
                ssl is not (MySqlSslMode.None or MySqlSslMode.Required or MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull))
                throw new ArgumentException("mysql_tls_invalid");
            var builder = new MySqlConnectionStringBuilder
            {
                Server = settings.MySqlHost, Port = checked((uint)settings.MySqlPort),
                Database = settings.MySqlDatabase, UserID = settings.MySqlUser, Password = settings.MySqlPassword,
                SslMode = ssl, PersistSecurityInfo = false, ConnectionTimeout = (uint)setup.ConnectTimeoutSeconds,
                DefaultCommandTimeout = (uint)setup.CommandTimeoutSeconds, Pooling = false,
            };
            MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, logger);
            var options = new MySqlOptions { ConnectionString = builder.ConnectionString, ConnectTimeoutSeconds = setup.ConnectTimeoutSeconds };
            if (options.Validate().Any()) throw new ArgumentException("mysql_configuration_invalid");
            var factory = new MySqlConnectionFactory(builder.ConnectionString);
            await factory.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (initialize)
                await new MigrationRunner(factory, logger, typeof(MySqlPlugin).Assembly).ApplyAsync(cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?>
            {
                ["connection_string"] = builder.ConnectionString, ["auto_migrate"] = true, ["connect_timeout_seconds"] = setup.ConnectTimeoutSeconds,
            };
        }
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
