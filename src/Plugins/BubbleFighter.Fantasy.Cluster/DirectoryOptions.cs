using System.ComponentModel.DataAnnotations;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Cluster;

/// <summary>Configures a bounded directory; mounting this plugin is an explicit deployment choice.</summary>
public sealed class DirectoryOptions : IValidatableOptions
{
    /// <summary>Gets or sets the private heartbeat listener; protect remote access with a secure tunnel or TLS termination.</summary>
    public string Listen { get; set; } = "127.0.0.1:8093";
    /// <summary>Gets or sets the maximum time a node stays discoverable without a successful heartbeat.</summary>
    [Range(3, 300)] public int LeaseSeconds { get; set; } = 15;
    /// <summary>Gets or sets the maximum room summaries retained for each configured node.</summary>
    [Range(1, 4096)] public int MaxRoomsPerNode { get; set; } = 512;
    /// <summary>Gets or sets the deployment budget for configured nodes, independently of native selector entries.</summary>
    [Range(1, int.MaxValue)] public int MaxNodes { get; set; } = 128;
    /// <summary>Gets or sets the heartbeat HTTP body limit in bytes.</summary>
    [Range(1024, 4194304)] public int MaxBodyBytes { get; set; } = 262144;
    /// <summary>Gets or sets the maximum combined native settings bytes retained for one published room.</summary>
    [Range(1, 1048576)] public int MaxRoomSettingsBytes { get; set; } = 65536;
    /// <summary>Gets or sets the maximum accepted directory HTTP connections.</summary>
    [Range(1, 256)] public int MaxConnections { get; set; } = 32;
    /// <summary>Gets or sets request-header, keep-alive and body-read deadlines in seconds.</summary>
    [Range(1, 60)] public int HttpTimeoutSeconds { get; set; } = 5;
    /// <summary>Gets or sets graceful directory listener shutdown time in seconds.</summary>
    [Range(1, 30)] public int ShutdownSeconds { get; set; } = 5;
    /// <summary>Gets or sets whether authenticated nodes may issue single-use destination preparation permits; this does not authenticate game sockets.</summary>
    public bool RoomPreparationPermits { get; set; }
    /// <summary>Gets or sets whether authenticated leased nodes can exchange logged-in migration requests.</summary>
    public bool Migrations { get; set; }
    /// <summary>Gets or sets the pending request and cancellation budget per destination node.</summary>
    [Range(1, 4096)] public int MaxPendingMigrations { get; set; } = 256;
    /// <summary>Gets or sets how long the directory waits for a destination offer, in milliseconds.</summary>
    [Range(500, 30000)] public int MigrationRequestTimeoutMs { get; set; } = 5000;
    /// <summary>Gets or sets the maximum pending room preparation permits retained by this directory.</summary>
    [Range(1, 4096)] public int MaxRoomPreparationPermits { get; set; } = 512;
    /// <summary>Gets or sets the monotonic lifetime of a permit before the destination claims it.</summary>
    [Range(100, 60000)] public int RoomPreparationPermitLifetimeMs { get; set; } = 10000;
    /// <summary>
    /// Gets or sets whether leased nodes of one verified account domain share their plaza members per client build.
    /// Nodes without a data scope, or whose heartbeat does not prove the scope's database, never share.
    /// </summary>
    public bool PlazaFederation { get; set; }
    /// <summary>Gets or sets the retained plaza changes; a node further behind receives a full snapshot instead.</summary>
    [Range(64, 65536)] public int MaxPlazaEvents { get; set; } = 4096;
    /// <summary>Gets or sets the plaza members held across every scope and build.</summary>
    [Range(1, 65536)] public int MaxPlazaMembers { get; set; } = 4096;
    /// <summary>Gets or sets the bounded set of known nodes; unknown node heartbeats are refused.</summary>
    public DirectoryNodeOptions[] Nodes { get; set; } = [];
    /// <summary>Gets or sets selector entries, separately from node membership.</summary>
    public SelectorOptions[] Selectors { get; set; } = [];

    /// <summary>Gets or sets independent population groups; an empty list preserves standalone reporting.</summary>
    public PresenceGroupOptions[] PresenceGroups { get; set; } = [];
    /// <summary>Gets or sets the maximum private account observations accepted per node report.</summary>
    [Range(1, 65536)] public int MaxPresencePlayersPerNode { get; set; } = 4096;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (!IPEndPoint.TryParse(Listen, out _)) yield return new("listen must be an IP endpoint.", [nameof(Listen)]);
        if (Nodes is null || Nodes.Length < 1 || Nodes.Length > MaxNodes || Nodes.Any(node => node is null))
        { yield return new("nodes must contain non-null definitions within max_nodes.", [nameof(Nodes)]); yield break; }
        if (PresenceGroups is null || PresenceGroups.Length > MaxNodes || PresenceGroups.Any(group => group is null))
            yield return new("presence_groups must be a bounded list of non-null groups.", [nameof(PresenceGroups)]);
        else
        {
            if (PresenceGroups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != PresenceGroups.Length
                || PresenceGroups.Select(group => CheckpointPath(group.PeakStatePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != PresenceGroups.Length)
                yield return new("Presence groups require unique identities and checkpoint paths.", [nameof(PresenceGroups)]);
            foreach (var group in PresenceGroups)
                if (!Identifier(group.Id) || group.Nodes is null || group.Nodes.Length is < 1 || group.Nodes.Length > MaxNodes
                    || group.Nodes.Distinct(StringComparer.Ordinal).Count() != group.Nodes.Length
                    || group.Nodes.Any(id => !Nodes.Any(node => node.Id == id))
                    || !Guid.TryParseExact(group.PersistentDomainId, "N", out var domain) || domain == Guid.Empty
                    || CheckpointPath(group.PeakStatePath) is null
                    || group.Nodes is not null && Nodes.Where(node => group.Nodes.Contains(node.Id, StringComparer.Ordinal))
                        .Select(node => node.DataScope).Distinct(StringComparer.Ordinal).Count() != 1
                    || group.Nodes is not null && Nodes.Any(node => group.Nodes.Contains(node.Id, StringComparer.Ordinal) && node.DataScope.Length == 0))
                    yield return new("Presence groups need known distinct nodes in one data scope, a persistent domain and a checkpoint path.", [nameof(PresenceGroups)]);
        }
        if (Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != Nodes.Length)
            yield return new("Node identities must be unique.", [nameof(Nodes)]);
        if (Nodes.Select(node => node.HeartbeatKey).Distinct(StringComparer.Ordinal).Count() != Nodes.Length)
            yield return new("Each node requires its own heartbeat key.", [nameof(Nodes)]);
        foreach (var node in Nodes)
        {
            if (node.Authentication is { } auth && (!Identifier(auth.AuthorityId) || auth.Mode is not ("master-issued" or "own-authority")
                || auth.PolicyMode != "authority" || !Uri.TryCreate(auth.ApiUrl, UriKind.Absolute, out var api)
                || api.UserInfo.Length > 0 || api.Query.Length > 0 || api.Fragment.Length > 0 || api.AbsolutePath != "/"
                || api.Scheme is not ("http" or "https")))
                yield return new("Authentication metadata requires an explicit authority, HTTP origin, supported mode and authority policy.", [nameof(Nodes)]);
            if (!BackendId(node.Id) || !Text(node.Name, 128) || !Scope(node.DataScope) || !Scope(node.JoinScope)
                || node.RoomDirectories is null || node.RoomDirectories.Length > 32
                || node.RoomDirectories.Any(group => !Identifier(group)) || node.RoomDirectories.Distinct(StringComparer.Ordinal).Count() != node.RoomDirectories.Length
                || node.HeartbeatKey is null || node.HeartbeatKey.Length is < 32 or > 256 || node.HeartbeatKey.Any(c => c is < '!' or > '~'))
                yield return new("Nodes require bounded identities, names, scopes and a private heartbeat key of 32..256 characters.", [nameof(Nodes)]);
            if (node.Endpoints is null || node.Endpoints.Length is < 1 or > 64 || node.Endpoints.Any(endpoint => endpoint is null))
            { yield return new("Each node requires 1..64 build endpoints.", [nameof(Nodes)]); continue; }
            if (node.Endpoints.Select(endpoint => endpoint.Build).Distinct(StringComparer.Ordinal).Count() != node.Endpoints.Length
                || node.Endpoints.Any(endpoint => !Identifier(endpoint.Build) || endpoint.Host is null || endpoint.Host.Length > 253
                    || Uri.CheckHostName(endpoint.Host) == UriHostNameType.Unknown || endpoint.Port is < 1 or > 65535 || endpoint.Capacity < 1))
                yield return new("Build endpoints require unique builds, valid hosts, ports and positive capacities.", [nameof(Nodes)]);
            if (node.NativeMigrationBuilds is null || node.NativeMigrationBuilds.Length > 64
                || node.NativeMigrationBuilds.Distinct(StringComparer.Ordinal).Count() != node.NativeMigrationBuilds.Length
                || node.NativeMigrationBuilds.Any(build => !Identifier(build) || !node.Endpoints.Any(endpoint => endpoint.Build == build)))
                yield return new("native_migration_builds must contain distinct configured build endpoints.", [nameof(Nodes)]);
        }
        if (Nodes.Where(node => node.Authentication is not null).GroupBy(node => node.Authentication!.AuthorityId, StringComparer.Ordinal)
            .Any(group => group.Select(node => node.Authentication!.ApiUrl).Distinct(StringComparer.Ordinal).Count() != 1))
            yield return new("An authority identity must name exactly one API origin.", [nameof(Nodes)]);
        if (Selectors is null || Selectors.Length < 1 || Selectors.Any(selector => selector is null))
        { yield return new("selectors must contain at least one non-null definition.", [nameof(Selectors)]); yield break; }
        if (Selectors.Select(selector => selector.Id).Distinct().Count() != Selectors.Length)
            yield return new("Selector identities must be unique.", [nameof(Selectors)]);
        if (Selectors.Select(selector => selector.Name).Distinct(StringComparer.Ordinal).Count() != Selectors.Length)
            yield return new("Selector display names must be unique.", [nameof(Selectors)]);
        foreach (var selector in Selectors)
        {
            if (selector.Id <= 0 || !Text(selector.Name, 128) || selector.ShortName is not null && !Text(selector.ShortName, 128)
                || selector.Nodes is null || selector.Nodes.Length > MaxNodes
                || selector.Automatic && selector.Nodes.Length == 0
                || selector.Nodes.Distinct(StringComparer.Ordinal).Count() != selector.Nodes.Length
                || selector.Nodes.Any(id => !Nodes.Any(node => node.Id == id)) || !selector.Automatic && selector.Nodes.Length > 1)
            { yield return new("Selectors require bounded names and distinct known nodes; automatic selectors need candidates, while manual selectors have zero or one node.", [nameof(Selectors)]); continue; }
            var members = Nodes.Where(node => selector.Nodes.Contains(node.Id, StringComparer.Ordinal)).ToArray();
            if (selector.Automatic && (members.Any(node => node.DataScope is not { Length: > 0 })
                || members.Select(node => node.DataScope).Distinct(StringComparer.Ordinal).Count() != 1))
                yield return new("Automatic placement must stay within one explicitly declared persistent data domain.", [nameof(Selectors)]);
            if (selector.PersistentDomainId is null
                || selector.Automatic && (!Guid.TryParseExact(selector.PersistentDomainId, "N", out var domain) || domain == Guid.Empty)
                || !selector.Automatic && selector.PersistentDomainId.Length != 0)
                yield return new("Automatic selectors require the authoritative persistent_domain_id; manual selectors leave it empty.", [nameof(Selectors)]);
            if (selector.Automatic && members.Select(node => node.Authentication is { } auth
                    ? (auth.AuthorityId, auth.ApiUrl, auth.Mode, auth.PolicyMode)
                    : ((string, string, string, string)?)null).Distinct().Count() != 1)
                yield return new("Automatic placement must use one exact authentication authority and policy source.", [nameof(Selectors)]);
        }
        var automaticDomains = Selectors.Where(selector => selector.Automatic && selector.Nodes is not null)
            .SelectMany(selector => Nodes.Where(node => selector.Nodes.Contains(node.Id, StringComparer.Ordinal))
                .Select(node => (node.DataScope, selector.PersistentDomainId)));
        if (automaticDomains.GroupBy(pair => pair.DataScope, StringComparer.Ordinal)
            .Any(group => group.Select(pair => pair.PersistentDomainId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
            yield return new("Automatic selectors for one data scope must pin the same persistent_domain_id.", [nameof(Selectors)]);
    }

    private static string? CheckpointPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool Scope(string? value) => value is not null && (value.Length == 0 || Identifier(value));
    private static bool BackendId(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    private static bool Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return false;
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }
}

/// <summary>A physical node's endpoint, authentication and explicitly independent sharing scopes.</summary>
public sealed class DirectoryNodeOptions
{
    /// <summary>Gets or sets the explicit ticket issuer and policy source; null forbids inferring authentication from directory scopes.</summary>
    public NodeAuthenticationOptions? Authentication { get; set; }
    /// <summary>Gets or sets the stable node identifier.</summary>
    public string Id { get; set; } = "";
    /// <summary>Gets or sets the administrative display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the declared persistent account domain; empty means independent data.</summary>
    public string DataScope { get; set; } = "";
    /// <summary>Gets or sets explicit cross-node join trust; empty means discovery only.</summary>
    public string JoinScope { get; set; } = "";
    /// <summary>Gets or sets room-directory memberships; these never grant account sharing.</summary>
    public string[] RoomDirectories { get; set; } = [];
    /// <summary>Gets or sets builds with explicitly deployed native migration source and destination support; empty keeps remote rooms read-only.</summary>
    public string[] NativeMigrationBuilds { get; set; } = [];
    /// <summary>Gets or sets this node's private authentication key, supplied through deployment secrets.</summary>
    public string HeartbeatKey { get; set; } = "";
    /// <summary>Gets or sets per-build advertised game endpoints.</summary>
    public NodeEndpointOptions[] Endpoints { get; set; } = [];
}

/// <summary>A configured build listener whose hostname stays unresolved until the connecting client needs it.</summary>
public sealed class NodeEndpointOptions
{
    /// <summary>Gets or sets the client profile identity.</summary>
    public string Build { get; set; } = "";
    /// <summary>Gets or sets the advertised hostname or address.</summary>
    public string Host { get; set; } = "";
    /// <summary>Gets or sets the game listener TCP port.</summary>
    public int Port { get; set; }
    /// <summary>Gets or sets the maximum admitted player population.</summary>
    public int Capacity { get; set; } = 512;
}

/// <summary>Configures an explicit authentication source without inferring trust from data or room scopes.</summary>
public sealed class NodeAuthenticationOptions
{
    /// <summary>Gets or sets the stable identity of the ticket issuer.</summary>
    public string AuthorityId { get; set; } = "";
    /// <summary>Gets or sets the authentication and catalog origin.</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>Gets or sets master-issued or own-authority ticket handling.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Gets or sets authority policy validation without inheritance or merging.</summary>
    public string PolicyMode { get; set; } = "authority";
}

/// <summary>One stable native selector label with manual or automatic backend candidates.</summary>
public sealed class SelectorOptions
{
    /// <summary>Gets or sets the native selector identity; automatic and manual entries use distinct identities.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the entry display name, independent of backend names.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets an optional concise label; null leaves clients using the full display name.</summary>
    public string? ShortName { get; set; }
    /// <summary>Gets or sets whether healthy non-draining candidates form an automatic placement pool.</summary>
    public bool Automatic { get; set; }
    /// <summary>Gets or sets the authoritative database's 32-hex domain identifier required for automatic candidates.</summary>
    public string PersistentDomainId { get; set; } = "";
    /// <summary>Gets or sets allowed node identities; an empty manual entry remains visible without a configured target.</summary>
    public string[] Nodes { get; set; } = [];
}
