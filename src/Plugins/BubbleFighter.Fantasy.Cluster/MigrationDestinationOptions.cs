using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures the destination's native game endpoints and bounded arrival cleanup deadlines.</summary>
public sealed class MigrationDestinationOptions : IValidatableOptions
{
    /// <summary>Gets or sets the destination identity used by its heartbeat and broker rows.</summary>
    public string NodeId { get; set; } = "";
    /// <summary>Gets or sets the window during which the destination accepts the new game connection.</summary>
    [Range(100, 60000)] public int ArrivalLifetimeMs { get; set; } = 10000;
    /// <summary>Gets or sets the interval between expired-seat cleanup attempts.</summary>
    [Range(100, 10000)] public int CleanupIntervalMs { get; set; } = 1000;
    /// <summary>Gets or sets the deadline for DNS, preparation and each reservation cancellation.</summary>
    [Range(100, 30000)] public int OperationTimeoutMs { get; set; } = 5000;
    /// <summary>Gets or sets one explicitly configured game endpoint per supported client build.</summary>
    public MigrationDestinationEndpoint[] Endpoints { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!RoomDirectoryClientOptions.BackendId(NodeId))
            yield return new("node_id must match the heartbeat node identity.", [nameof(NodeId)]);
        if (Endpoints is null || Endpoints.Length is < 1 or > 64)
        { yield return new("endpoints requires 1..64 game listeners.", [nameof(Endpoints)]); yield break; }
        var builds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in Endpoints)
            if (endpoint is null || !RoomDirectoryClientOptions.Identifier(endpoint.ClientBuild) || !builds.Add(endpoint.ClientBuild)
                || string.IsNullOrWhiteSpace(endpoint.ListenerId) || endpoint.ListenerId.Length > 256
                || endpoint.Host is not { Length: > 0 and <= 253 } || Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns
                || endpoint.Port is < 1 or > 65535)
                yield return new("Each endpoint requires a distinct client_build, a listener_id, a canonical DNS host and port 1..65535.", [nameof(Endpoints)]);
    }
}

/// <summary>Names the canonical host resolved for every accepted migration of one client build.</summary>
public sealed class MigrationDestinationEndpoint
{
    /// <summary>Gets or sets the client build served by this game listener.</summary>
    public string ClientBuild { get; set; } = "";
    /// <summary>Gets or sets the exact destination transport row id, including any profile prefix.</summary>
    public string ListenerId { get; set; } = "";
    /// <summary>Gets or sets the canonical DNS name; numeric addresses are not deployment identities.</summary>
    public string Host { get; set; } = "";
    /// <summary>Gets or sets the externally reachable game listener port.</summary>
    public int Port { get; set; }
}
