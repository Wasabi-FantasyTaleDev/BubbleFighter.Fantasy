using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Commands;

/// <summary>Bounds the permission registry and configures groups and stable account grants.</summary>
public sealed class PermissionOptions
{
    /// <summary>Gets or sets the maximum number of active permission registrations.</summary>
    [Range(2, 16384)] public int MaxNodes { get; set; } = 2048;
    /// <summary>Gets or sets the total storage budget for configured identities, groups and expanded permission rules.</summary>
    [Range(1, 1048576)] public int MaxPolicyEntries { get; set; } = 65536;
    /// <summary>Gets or sets named groups with an acyclic inheritance graph.</summary>
    public PermissionGroupOptions[] Groups { get; set; } = [];
    /// <summary>Gets or sets grants keyed by authentication namespace and stable account identifier.</summary>
    public PermissionSubjectOptions[] Subjects { get; set; } = [];
}

/// <summary>Configures one permission group; explicit denials override all ordinary grants.</summary>
public sealed class PermissionGroupOptions
{
    /// <summary>Gets or sets the unique lowercase group identifier.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the names of groups inherited by this group.</summary>
    public string[] Inherits { get; set; } = [];
    /// <summary>Gets or sets exact nodes or terminal wildcard patterns granted by this group.</summary>
    public string[] Allow { get; set; } = [];
    /// <summary>Gets or sets exact nodes or terminal wildcard patterns denied by this group.</summary>
    public string[] Deny { get; set; } = [];
}

/// <summary>Configures a stable game or administration account independently of display names.</summary>
public sealed class PermissionSubjectOptions
{
    /// <summary>Gets or sets the authentication namespace: game or webui.</summary>
    public string Kind { get; set; } = "game";
    /// <summary>Gets or sets the persistent account key in that namespace.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets game OP status; this setting is invalid for WebUI accounts.</summary>
    public bool Operator { get; set; }
    /// <summary>Gets or sets group memberships for this account only.</summary>
    public string[] Groups { get; set; } = [];
    /// <summary>Gets or sets exact nodes or terminal wildcard patterns granted to this account.</summary>
    public string[] Allow { get; set; } = [];
    /// <summary>Gets or sets exact nodes or terminal wildcard patterns denied to this account.</summary>
    public string[] Deny { get; set; } = [];
}
