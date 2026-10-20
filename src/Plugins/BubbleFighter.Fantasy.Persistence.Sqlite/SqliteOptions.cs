using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Settings;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Defines the explicit local database location and limits for the SQLite provider.</summary>
public sealed class SqliteOptions : IValidatableOptions
{
    /// <summary>Gets or sets the per-account scope, row and name budgets for saved customized keys.</summary>
    [Required]
    public CustomizedKeyLimits CustomizedKeys { get; set; } = new();

    /// <summary>Gets or sets the absolute save file path, independent of the release and log directories.</summary>
    [Required]
    public string DatabasePath { get; set; } = "";

    /// <summary>Gets or sets the SQLite main-file page quota; a full database rejects and rolls back writes.</summary>
    [Range(1048576L, 1099511627776L)]
    public long MaxDatabaseBytes { get; set; } = 268435456;

    /// <summary>Gets or sets the maximum number of durable player identities.</summary>
    [Range(1, 1000000)]
    public int MaxPlayers { get; set; } = 4096;

    /// <summary>Gets or sets the maximum number of registered launcher accounts.</summary>
    [Range(1, 1000000)]
    public int MaxAccounts { get; set; } = 4096;

    /// <summary>Gets or sets the distinct actor capacity for process-local movement samples.</summary>
    [Range(1, 1000000)]
    public int MaxMovementActors { get; set; } = 16384;

    /// <summary>Gets or sets the maximum native SQLite lock wait in milliseconds.</summary>
    [Range(1, 60000)]
    public int BusyTimeoutMs { get; set; } = 5000;

    /// <summary>Gets or sets how many recent purchase audit entries each player retains.</summary>
    [Range(1, 1024)]
    public int MaxPurchaseHistoryPerPlayer { get; set; } = 256;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!Path.IsPathFullyQualified(DatabasePath) || Path.GetFileName(DatabasePath).Length == 0
            || DatabasePath.IndexOf('\0') >= 0)
            yield return new("database_path must be an absolute file path.", [nameof(DatabasePath)]);
    }
}
