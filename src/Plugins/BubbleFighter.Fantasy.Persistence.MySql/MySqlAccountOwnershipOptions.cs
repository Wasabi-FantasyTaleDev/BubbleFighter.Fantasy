using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using MySqlConnector;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Configures a separate bounded account-occupancy row against the authoritative database.</summary>
public sealed class MySqlAccountOwnershipOptions : IValidatableOptions
{
    /// <summary>Gets or sets the authority database connection string, resolved from deployment secrets.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    /// <summary>Gets or sets the expected database marker as 32 hexadecimal digits.</summary>
    public string PersistentDomainId { get; set; } = "";

    /// <summary>Gets or sets this node's stable directory identity; empty disables account handoff publication.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Gets or sets the maximum simultaneous ownership connections, including pending acquisition.</summary>
    [Range(1, 100000)]
    public int MaxOwners { get; set; } = 512;

    /// <summary>Gets or sets the deadline for establishing an authority connection.</summary>
    [Range(1, 60)]
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Gets or sets the command timeout for acquiring and checking an account hold.</summary>
    [Range(1, 60)]
    public int CommandTimeoutSeconds { get; set; } = 3;

    /// <summary>Creates connector settings; ownership connections never enter a reusable connection pool.</summary>
    public MySqlConnectionStringBuilder CreateBuilder() => new(ConnectionString)
    { Pooling = false, ConnectionTimeout = (uint)ConnectTimeoutSeconds, DefaultCommandTimeout = (uint)CommandTimeoutSeconds };

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        var persistence = new MySqlOptions { ConnectionString = ConnectionString, ConnectTimeoutSeconds = ConnectTimeoutSeconds };
        foreach (var error in persistence.Validate()) yield return error;
        if (NodeId is null || NodeId.Length > 64 || NodeId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            yield return new("node_id must be empty or a lowercase directory node identifier of at most 64 characters.", [nameof(NodeId)]);
        if (!Guid.TryParseExact(PersistentDomainId, "N", out var id) || id == Guid.Empty)
            yield return new("persistent_domain_id must identify the authoritative database using 32 hexadecimal digits.", [nameof(PersistentDomainId)]);
    }
}
