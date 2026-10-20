using BubbleFighter.Fantasy.Abstractions;
using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Installation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Offers real SQLite setup without publishing gameplay repositories before installation completes.</summary>
[Plugin("persistence.sqlite.setup", ProvidesMany = [typeof(IInstallationDatabase)])]
public sealed class SqliteSetupPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.RegisterMany<IInstallationDatabase>(new Database(context.Logger, context.Config.Bind<SqliteSetupOptions>())));
        return ValueTask.CompletedTask;
    }

    private sealed class Database(ILogger logger, SqliteSetupOptions setup) : IInstallationDatabase
    {
        public string Kind => "sqlite";

        public async ValueTask<IReadOnlyDictionary<string, object?>> PrepareAsync(InstallationSettings settings, bool initialize,
            CancellationToken cancellationToken)
        {
            var options = new SqliteOptions { DatabasePath = settings.SqlitePath, BusyTimeoutMs = setup.BusyTimeoutMs };
            if (options.Validate().Any()) throw new ArgumentException("sqlite_path_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            if (initialize)
            {
                using var database = new SqliteDatabase(options, logger);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
                    DefaultTimeout = (int)Math.Ceiling(setup.BusyTimeoutMs / 1000d),
                    Pooling = false,
                }.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check";
                if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("sqlite_integrity_failed");
                // Roll back a schema write: connection success alone does not prove that the save is writable.
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE IF NOT EXISTS __bff_setup_write_probe (value INTEGER)";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                transaction.Rollback();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new Dictionary<string, object?> { ["database_path"] = settings.SqlitePath, ["busy_timeout_ms"] = setup.BusyTimeoutMs };
        }
    }
}

/// <summary>Bounds SQLite lock waits during setup and passes the same policy to the installed provider.</summary>
public sealed class SqliteSetupOptions
{
    /// <summary>Gets or sets the maximum native SQLite lock wait in milliseconds.</summary>
    [Range(1, 60000)]
    public int BusyTimeoutMs { get; set; } = 5000;
}
