using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Contracts.Settings;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Net;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using SqlSugar;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// MySQL persistence plugin. Registers <see cref="IDbConnectionFactory"/> and
/// <see cref="IMigrationRunner"/> after probing the server. With <c>auto_migrate</c> it
/// applies pending embedded SQL migrations in order inside per-migration transactions.
/// A missing password, malformed connection string, or connection failure fails the row
/// at load. Repository interfaces
/// over fighters, inventory, money, create-user submits, and clan/shop opens plus the per-player
/// active weapon-preset slot are registered
/// beside <see cref="IDbConnectionFactory"/>; login still consumes
/// <c>IPlayerRepository</c> from the lobby row.
/// </summary>
[Plugin("persistence.mysql", Description = "MySQL persistence via MySqlConnector and SqlSugar.",
    Provides = [
        typeof(IDbConnectionFactory),
        typeof(IMigrationRunner),
        typeof(IClanStore),
        typeof(ILauncherAccountRepository),
        typeof(ILauncherSessionStore),
        typeof(BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore),
        typeof(IShopSessionStore),
        typeof(IFighterRepository),
        typeof(IInventoryRepository),
        typeof(IPlayerWalletRepository),
        typeof(BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository),
        typeof(IShopPurchaseStore),
        typeof(IPresentStore),
        typeof(IPlayerInventoryState), typeof(IPlayerSettingsRepository)])]
public sealed class MySqlPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MySqlOptions>();
        var builder = options.CreateBuilder();
        var factory = new MySqlConnectionFactory(builder.ConnectionString);
        var settings = new MySqlPlayerSettings(factory, options.CustomizedKeys);
        var runner = new MigrationRunner(factory, context.Logger, typeof(MySqlPlugin).Assembly);

        MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, context.Logger);

        try
        {
            await factory.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to open MySQL at {builder.Server}:{builder.Port} database '{builder.Database}' as '{builder.UserID}'.",
                ex);
        }

        context.Effect(context.Services.Register<IDbConnectionFactory>(factory));
        context.Effect(context.Services.Register<IMigrationRunner>(runner));
        context.Effect(context.Services.Register<IPlayerSettingsRepository>(settings));
        context.Effect(context.Services.Register<ILauncherAccountRepository>(new MySqlLauncherAccounts(factory)));
        context.Effect(context.Services.Register<ILauncherSessionStore>(new MySqlLauncherSessions(factory)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Progress.ITutorialStageStore>(new MySqlTutorialStages(factory)));
        context.Effect(context.Services.Register<IShopSessionStore>(new MySqlShopSessionStore(factory)));
        context.Effect(context.Services.Register<IClanStore>(new MySqlClanStore(factory)));
        context.Effect(context.Services.Register<IFighterRepository>(new MySqlFighterRepository(factory)));
        context.Effect(context.Services.Register<IInventoryRepository>(new MySqlInventoryRepository(factory)));
        context.Effect(context.Services.Register<IPlayerWalletRepository>(new MySqlPlayerWalletRepository(factory)));
        context.Effect(context.Services.Register<BubbleFighter.Fantasy.Contracts.Progress.IPlayerProgressRepository>(
            new MySqlPlayerProgressRepository(factory)));
        context.Effect(context.Services.Register<IPlayerInventoryState>(new MySqlPlayerInventoryState(factory)));
        context.Effect(context.Services.Register<IShopPurchaseStore>(new MySqlShopPurchaseStore(factory)));
        context.Effect(context.Services.Register<IPresentStore>(new MySqlPresentStore(factory)));

        if (options.AutoMigrate)
            await runner.ApplyAsync(cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation(
            "MySQL persistence connected to {Server}:{Port}/{Database} as {User}.",
            builder.Server, builder.Port, builder.Database, builder.UserID);
    }
}

/// <summary>Configuration for the <see cref="MySqlPlugin"/> row.</summary>
public sealed class MySqlOptions : IValidatableOptions
{
    /// <summary>Gets or sets the per-account scope, row and name budgets for saved customized keys.</summary>
    [Required]
    public CustomizedKeyLimits CustomizedKeys { get; set; } = new();

    /// <summary>Gets or sets the MySqlConnector connection string.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    /// <summary>Gets or sets whether migrations are applied on activation.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Gets or sets the connection timeout in seconds.</summary>
    [Range(1, 600)]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Builds a connector string with the configured timeout. Callers must run
    /// <see cref="Validate"/> (or bind through the plugin row) first.
    /// </summary>
    /// <returns>A builder whose connection string is ready to open.</returns>
    public MySqlConnectionStringBuilder CreateBuilder()
    {
        var builder = new MySqlConnectionStringBuilder(ConnectionString)
        {
            ConnectionTimeout = (uint)ConnectTimeoutSeconds,
        };
        return builder;
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            yield return new ValidationResult(
                "connection_string is required; BFF_MYSQL_HOST, BFF_MYSQL_PORT, BFF_MYSQL_USER, BFF_MYSQL_PASSWORD, and BFF_MYSQL_DATABASE must resolve.",
                [nameof(ConnectionString)]);
            yield break;
        }

        MySqlConnectionStringBuilder? builder = null;
        string? parseError = null;
        try
        {
            builder = new MySqlConnectionStringBuilder(ConnectionString);
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
        }

        if (parseError is not null || builder is null)
        {
            yield return new ValidationResult($"connection_string: {parseError}", [nameof(ConnectionString)]);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(builder.Server))
            yield return new ValidationResult("connection_string is missing Server (BFF_MYSQL_HOST).", [nameof(ConnectionString)]);
        if (builder.Port == 0)
            yield return new ValidationResult("connection_string is missing Port (BFF_MYSQL_PORT).", [nameof(ConnectionString)]);
        if (string.IsNullOrWhiteSpace(builder.UserID))
            yield return new ValidationResult("connection_string is missing User ID (BFF_MYSQL_USER).", [nameof(ConnectionString)]);
        if (string.IsNullOrEmpty(builder.Password))
            yield return new ValidationResult(
                "connection_string is missing Password (BFF_MYSQL_PASSWORD is required and must not be empty).",
                [nameof(ConnectionString)]);
        if (string.IsNullOrWhiteSpace(builder.Database))
            yield return new ValidationResult("connection_string is missing Database (BFF_MYSQL_DATABASE).", [nameof(ConnectionString)]);
    }
}

/// <summary><see cref="IDbConnectionFactory"/> backed by MySqlConnector.</summary>
public sealed class MySqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>Creates the factory.</summary>
    /// <param name="connectionString">The validated connection string.</param>
    public MySqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    /// <summary>Returns whether a connection string disables TLS for a non-loopback host.</summary>
    /// <param name="builder">The parsed connection settings.</param>
    /// <returns><see langword="true"/> when a remote connection has no TLS.</returns>
    internal static bool IsUnencryptedRemote(MySqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.SslMode != MySqlSslMode.None)
            return false;
        var host = builder.Server.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(host, out var address))
            return !IPAddress.IsLoopback(address);
        return true;
    }

    /// <summary>Logs the transport risk without exposing the connection string or its credentials.</summary>
    internal static void WarnIfUnencryptedRemote(MySqlConnectionStringBuilder builder, ILogger logger)
    {
        if (IsUnencryptedRemote(builder))
            logger.LogWarning("MySQL host {Server} uses SslMode=None; credentials and game data travel unencrypted.", builder.Server);
    }

    /// <inheritdoc />
    public async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a connection and runs <c>SELECT 1</c> so a down server fails activation.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask ProbeAsync(CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(this, ct).ConfigureAwait(false);
        // Raw because the probe touches no table, so there is no entity to query.
        await session.Db.Ado.GetScalarAsync("SELECT 1", new List<SugarParameter>(), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Applies embedded <c>Migrations/*.sql</c> resources in filename order. Each migration is
/// named <c>NNNN_description.sql</c>; the four-digit prefix is the schema version recorded
/// in <c>schema_migrations</c>. Every migration runs inside its own transaction. A
/// misnamed or empty script fails the run instead of being skipped.
/// </summary>
public sealed class MigrationRunner : IMigrationRunner
{
    // Raw DDL: bootstraps the bookkeeping table before any entity can be mapped to it.
    private const string EnsureTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INT NOT NULL,
            name VARCHAR(255) NOT NULL,
            applied_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (version)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _logger;
    private readonly Assembly _assembly;

    /// <summary>Creates the runner.</summary>
    /// <param name="factory">The connection factory.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="assembly">The assembly holding the embedded migration resources.</param>
    public MigrationRunner(IDbConnectionFactory factory, ILogger logger, Assembly assembly)
    {
        _factory = factory;
        _logger = logger;
        _assembly = assembly;
    }

    /// <summary>Lists pending and applied migrations; exposed for tests and diagnostics.</summary>
    /// <returns>Migration descriptors in apply order.</returns>
    public IReadOnlyList<(int Version, string Name)> DiscoverMigrations()
    {
        var result = new List<(int, string)>();
        var versions = new HashSet<int>();
        foreach (var resource in _assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var file = resource[..^4].Split('.')[^1];
            var underscore = file.IndexOf('_');
            if (underscore != 4 || !int.TryParse(file.AsSpan(0, 4), out var version))
                throw new InvalidOperationException($"Embedded SQL resource '{resource}' is not named NNNN_description.sql.");
            if (!versions.Add(version))
                throw new InvalidOperationException($"Duplicate embedded migration version {version}.");
            result.Add((version, resource));
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No embedded SQL migrations were found.");
        return result;
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(CancellationToken ct)
    {
        var migrations = DiscoverMigrations();
        await using var session = await SugarSession.OpenAsync(_factory, ct).ConfigureAwait(false);
        var db = session.Db;
        // Migration scripts are DDL and stay raw SQL; only the bookkeeping rows go through the entity map.
        await db.Ado.ExecuteCommandAsync(EnsureTableSql, new List<SugarParameter>(), ct).ConfigureAwait(false);
        var applied = (await db.Queryable<SchemaMigrationRow>().Select(m => m.Version).ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

        foreach (var (version, resource) in migrations)
        {
            if (applied.Contains(version))
                continue;
            var script = await ReadResourceAsync(resource, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(script))
                throw new InvalidOperationException($"Embedded migration '{resource}' is empty.");
            var name = resource[..^4].Split('.')[^1];
            await using var transaction = await session.BeginAsync().ConfigureAwait(false);
            await db.Ado.ExecuteCommandAsync(script, new List<SugarParameter>(), ct).ConfigureAwait(false);
            await db.Insertable(new SchemaMigrationRow { Version = version, Name = name }).ExecuteCommandAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            _logger.LogInformation("Applied migration {Version} ({Name}).", version, name);
        }
    }

    private async ValueTask<string> ReadResourceAsync(string resource, CancellationToken ct)
    {
        await using var stream = _assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
