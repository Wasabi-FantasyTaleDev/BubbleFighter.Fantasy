using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Reads configured selector targets and current node leases without granting account or room access.</summary>
public interface IServerDirectory
{
    /// <summary>Returns selector entries for one client build, retaining configured entries even when offline.</summary>
    IReadOnlyList<ServerSelectorEntry> ListServers(string clientBuild);

    /// <summary>Returns fresh room summaries visible to the requesting node under explicit directory memberships.</summary>
    IReadOnlyList<FederatedRoom> ListRooms(string requestingNodeId, string clientBuild);

    /// <summary>
    /// Gets the named groups the servers are offered in (for example Main, Test Server, Oversea Server), first the default;
    /// empty when the deployment does not group its servers.
    /// </summary>
    IReadOnlyList<ServerSection> Sections => [];
}

/// <summary>A named group of servers the Launcher offers as one choice.</summary>
/// <param name="Id">The stable section identity that selectors refer to.</param>
/// <param name="Name">The section's display name.</param>
/// <param name="ApiUrl">
/// The section's own Launcher API origin when it signs players in separately; null when the section shares the sign-in
/// of the API that publishes the directory, so one login reaches its servers through the in-game server list.
/// </param>
public sealed record ServerSection(string Id, string Name,
    [property: JsonPropertyName("api_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApiUrl);

/// <summary>An operator-announced maintenance period of a node, in UTC.</summary>
/// <param name="Start">When the maintenance began or begins; null when it is open-ended at the start.</param>
/// <param name="End">When the maintenance is expected to end; null when unknown.</param>
/// <param name="Description">The operator's explanation shown to players; empty when none was given.</param>
public sealed record ServerMaintenanceWindow(DateTimeOffset? Start, DateTimeOffset? End, string Description);

/// <summary>Reads configured selector membership separately from current placement health, so an admitted node may recover during slow game startup.</summary>
public interface IServerSelectionDirectory
{
    /// <summary>Gets the client builds with configured endpoints, for validating issuer selection bindings before publishing the service.</summary>
    IReadOnlyList<string> ClientBuilds { get; }

    /// <summary>Returns all configured build endpoints and their actual health observations, including offline or never-observed members.</summary>
    IReadOnlyList<ServerSelectorEntry> ListSelectionServers(string clientBuild);
}

/// <summary>Reads the stable identity persisted by the configured authoritative account database.</summary>
public interface IPersistentDataDomainIdentity
{
    /// <summary>Returns the database's persisted domain identifier, shared by nodes using the same database.</summary>
    /// <param name="ct">Cancels the database read.</param>
    /// <returns>A stable opaque identifier stored in the persistent database.</returns>
    ValueTask<string> ReadAsync(CancellationToken ct);
}

/// <summary>A distinct selector identity and name, with up to one manual target or eligible automatic targets.</summary>
/// <param name="Id">The stable selector identity, independent of its backend nodes.</param>
/// <param name="Name">The selector's display name.</param>
/// <param name="Automatic">Whether the client selects among the returned candidates.</param>
/// <param name="Targets">Configured targets and their current admission state.</param>
public sealed record ServerSelectorEntry(int Id, string Name, bool Automatic, IReadOnlyList<ServerNodeTarget> Targets)
{
    /// <summary>Gets an optional concise display label; absent labels retain the full configured name.</summary>
    [JsonPropertyName("short_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShortName { get; init; }

    /// <summary>Gets the identity of the section this selector is offered in; null when the directory has no sections.</summary>
    [JsonPropertyName("section")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Section { get; init; }
}

/// <summary>A build-specific endpoint with independent data, room and routing scopes.</summary>
/// <param name="NodeId">The stable physical node identity.</param>
/// <param name="Host">The configured DNS name or address; directory reads never freeze DNS resolution.</param>
/// <param name="Port">The game listener port for this build.</param>
/// <param name="DataScope">The declared persistent account domain; an empty value grants no sharing.</param>
/// <param name="JoinScope">The explicit routing trust domain; an empty value grants no cross-node joins.</param>
/// <param name="Online">Whether a current lease reports the build ready.</param>
/// <param name="Draining">Whether new placements are refused while existing work drains.</param>
/// <param name="Players">Authenticated game connections currently on the build.</param>
/// <param name="Capacity">The configured admission limit for this build.</param>
public sealed record ServerNodeTarget(string NodeId, string Host, int Port, string DataScope, string JoinScope,
    bool Online, bool Draining, int Players, int Capacity)
{
    /// <summary>Gets the explicit authentication and policy source; absent metadata does not grant authentication trust.</summary>
    public ServerAuthentication? Authentication { get; init; }

    /// <summary>
    /// Gets every client build this node is configured to serve, so a list of one build's nodes also says which other
    /// builds each node serves; a server's builds are the union over its nodes. Null where the source does not say.
    /// </summary>
    [JsonPropertyName("client_builds")]
    public IReadOnlyList<string>? ClientBuilds { get; init; }

    /// <summary>Gets the source's UTC time for its last accepted node report, or null before any report.</summary>
    [JsonPropertyName("observed_at_utc")]
    public DateTimeOffset? ObservedAtUtc { get; init; }

    /// <summary>Gets the last report's source-owned UTC expiry, conservatively capped on revocation or clock changes; reading the directory never renews it.</summary>
    [JsonPropertyName("expires_at_utc")]
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>Gets an opaque identity for the last accepted report; compare equality only, never lexical or numeric order.</summary>
    [JsonPropertyName("status_revision")]
    public string? StatusRevision { get; init; }

    /// <summary>Gets the node's maintenance period while it is in effect; null when the node is not under maintenance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerMaintenanceWindow? Maintenance { get; init; }
}

/// <summary>The explicit ticket issuer and policy authority for a selected backend.</summary>
/// <param name="AuthorityId">An opaque identity whose origin must match the authenticated Launcher session.</param>
/// <param name="ApiUrl">The authority API origin; this does not redirect account credentials automatically.</param>
/// <param name="Mode">master-issued for shared issuer tickets, or own-authority for a separately authenticated session.</param>
/// <param name="PolicyMode">authority requires the authority's catalog and selected build policy; no implicit inheritance or override.</param>
public sealed record ServerAuthentication(string AuthorityId, string ApiUrl, string Mode, string PolicyMode);

/// <summary>A discoverable room without passwords, account identities, or implicit joining permission.</summary>
/// <param name="NodeId">The authoritative node that published the room.</param>
/// <param name="Epoch">The node lease generation; stale generations cannot update the directory.</param>
/// <param name="ClientBuild">The build whose packet layouts describe the room.</param>
/// <param name="RoomId">The room identity local to its owning node and build.</param>
/// <param name="Title">The room display title.</param>
/// <param name="Mode">The room's game mode.</param>
/// <param name="Map">The map key, when selected.</param>
/// <param name="Phase">The owner-reported room lifecycle phase.</param>
/// <param name="Players">The occupied seat count.</param>
/// <param name="Capacity">The room's seat limit.</param>
/// <param name="Locked">Whether admission requires a password.</param>
/// <param name="JoinAllowed">Whether a verified admission path exists for the requesting node; directory sharing alone never grants it.</param>
public sealed record FederatedRoom(string NodeId, long Epoch, string ClientBuild, int RoomId, string Title,
    string Mode, string? Map, string Phase, int Players, int Capacity, bool Locked, bool JoinAllowed)
{
    /// <summary>Gets the creation identity; an empty value is an informational summary without native admission identity.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>Gets the optional native list entry; its presence grants no admission permission.</summary>
    public Rooms.RoomListEntry? Listing { get; init; }
}
