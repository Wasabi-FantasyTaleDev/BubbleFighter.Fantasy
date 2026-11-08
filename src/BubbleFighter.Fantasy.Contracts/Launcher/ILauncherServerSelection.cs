using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Contracts.Launcher;

/// <summary>Captures authorized endpoint membership at the ticket issuer and refreshes health without expanding a launch's original endpoint membership.</summary>
public interface ILauncherServerSelection
{
    /// <summary>Gets the issuer's explicit game node identity used when its local listener redeems a scoped ticket.</summary>
    string LocalNodeId { get; }

    /// <summary>Returns the issuer-owned authority, data scope and configured selector members, independently of their current health.</summary>
    LauncherServerSelectionSnapshot Capture(string build);

    /// <summary>Refreshes only original selectors and exact endpoint bindings; health remains explicit and changed endpoints cannot replace their original bindings.</summary>
    LauncherServerSelectionSnapshot Refresh(string build, LauncherServerSelectionSnapshot original);
}

/// <summary>A source-owned selection view, independent of the longer-lived launch ticket that authorizes its members.</summary>
/// <param name="AuthorityId">Explicit authentication authority that may issue tickets for these endpoints.</param>
/// <param name="DataScope">Persistent account scope shared by every admitted endpoint.</param>
/// <param name="GeneratedAtUtc">UTC response generation time, which never renews a node observation.</param>
/// <param name="Servers">Native selector identities, configured physical endpoint membership and actual health observations.</param>
public sealed record LauncherServerSelectionSnapshot(string AuthorityId, string DataScope, DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ServerSelectorEntry> Servers);

/// <summary>Redeems a launch ticket for an authenticated game node, enforcing endpoint membership atomically with its one-use consumption.</summary>
public interface ILauncherNodeTicketService
{
    /// <summary>Consumes a valid ticket only when its build and authenticated node are permitted; rejection does not spend it.</summary>
    ValueTask<LauncherTicketIdentity?> RedeemForNodeAsync(string ticket, string build, string nodeId, CancellationToken cancellationToken);
}

/// <summary>The immutable authority and original endpoint membership attached to one game ticket.</summary>
/// <param name="GrantId">Opaque per-launch admission identity, independent of the ticket credential.</param>
/// <param name="Build">Client build whose catalog and policy were accepted when the ticket was issued.</param>
/// <param name="AuthorityId">Issuer-owned authentication authority.</param>
/// <param name="PolicyRevision">Equality-only identity of the issuer's catalog and policy at issuance.</param>
/// <param name="DataScope">Shared persistent scope authorized by this ticket.</param>
/// <param name="ExpiresAtUtc">Hard admission expiry, bounded by the ticket and authorizing bearer rather than a node heartbeat.</param>
/// <param name="Snapshot">Original authorized endpoint bindings; later health refreshes cannot add or replace them.</param>
public sealed record LauncherServerSelectionGrant(string GrantId, string Build, string AuthorityId, string PolicyRevision,
    string DataScope, DateTimeOffset ExpiresAtUtc, LauncherServerSelectionSnapshot Snapshot);
