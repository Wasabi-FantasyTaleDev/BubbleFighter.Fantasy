namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Defines a population's physical members and durable peak separately from server selectors.</summary>
public sealed class PresenceGroupOptions
{
    /// <summary>Gets or sets the stable group identity requested by Launcher API rows.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets unique physical node identities; automatic and manual selector aliases are not members.</summary>
    public string[] Nodes { get; set; } = [];
    /// <summary>Gets or sets the authoritative database identifier all reports must prove before being counted.</summary>
    public string PersistentDomainId { get; set; } = "";
    /// <summary>Gets or sets the durable checkpoint file; its membership fingerprint prevents reusing another group's peak.</summary>
    public string PeakStatePath { get; set; } = "";
}
