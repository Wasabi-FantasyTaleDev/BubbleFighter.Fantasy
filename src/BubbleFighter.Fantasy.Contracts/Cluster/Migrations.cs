using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Why a logged-in player moves to another node.</summary>
public enum MigrationKind
{
    /// <summary>Joining a room owned by the destination node.</summary>
    Room,

    /// <summary>Returning to the player's home node after leaving a remote room.</summary>
    Home,
}

/// <summary>
/// A source node's request that a destination node accept one logged-in player. It carries no credential: the native
/// move request (kr-812 sub_813060 sends gcpReqMoveInto with only the retained player id) cannot carry one, so the
/// destination binds the arrival to the address the source observed instead.
/// </summary>
/// <param name="MigrationId">An unpredictable 32-character lower-case hexadecimal identity, also used for the account handoff.</param>
/// <param name="Kind">Why the player moves.</param>
/// <param name="SourceNode">The node the player is connected to.</param>
/// <param name="DestinationNode">The node that must accept the player.</param>
/// <param name="DestinationEpoch">The destination lease generation the source selected; a restarted destination refuses.</param>
/// <param name="ClientBuild">The player's client build; the destination serves it on its own listener for that build.</param>
/// <param name="UserNum">The account being moved, equal to the native retained player id.</param>
/// <param name="ClientAddress">The player's public address as the source node observed it.</param>
/// <param name="Room">The destination room for <see cref="MigrationKind.Room"/>, otherwise null.</param>
public sealed record MigrationRequest(string MigrationId, MigrationKind Kind, string SourceNode, string DestinationNode,
    long DestinationEpoch, string ClientBuild, long UserNum, IPAddress ClientAddress, RoomAdmissionRequest? Room)
{
    /// <summary>Gets the source connection's native channel for the destination move acknowledgement.</summary>
    public int SourceChannel { get; init; }

    /// <summary>Gets the node where the player first logged in and should return after a remote visit.</summary>
    public string HomeNode { get; init; } = "";

    /// <summary>Gets the idle scene to restore on a home return: main menu or plaza; room arrivals use main menu.</summary>
    public SessionPhase HomeScene { get; init; } = SessionPhase.MainMenu;

    /// <summary>Gets the read-only visitor snapshot for an independent data domain, or null for shared-account migration.</summary>
    public MigrationGuest? Guest { get; init; }

    /// <summary>
    /// Gets the party leader this player travels with, or null for a solo move. The leader's own request names itself;
    /// each follower's names the leader, and the destination rebuilds the party as they arrive. Omitted from the node
    /// transport when null, so a solo move reads the same on nodes that predate party travel.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? PartyLeader { get; init; }

    /// <summary>Describes the request without the room password.</summary>
    public override string ToString() => $"Migration {MigrationId} ({Kind}) of account {UserNum} {SourceNode}->{DestinationNode} on {ClientBuild}";
}

/// <summary>A read-only visitor snapshot; the destination must never persist it to its player database.</summary>
/// <param name="Nickname">The player's original nickname, before adding the source server label.</param>
/// <param name="ServerName">The source server's configured display name.</param>
/// <param name="Level">The player's displayed level at departure.</param>
/// <param name="EquippedItemIds">The equipped item identifiers used to display the visitor.</param>
/// <param name="ColorIndex">The player's displayed nickname color index.</param>
public sealed record MigrationGuest(string Nickname, string ServerName, int Level, int[] EquippedItemIds, int ColorIndex);

/// <summary>The destination's answer: an endpoint to send in the native move offer, or the native refusal.</summary>
/// <param name="Ipv4">The destination game listener address for this build, present only when accepted.</param>
/// <param name="Port">The destination game listener port for this build, present only when accepted.</param>
/// <param name="Refusal">The native room refusal, present only when refused for a room reason.</param>
/// <param name="Lifetime">How long the destination waits for the player's new connection.</param>
public sealed record MigrationOffer(IPAddress? Ipv4, int Port, RoomJoinRefusal? Refusal, TimeSpan Lifetime)
{
    /// <summary>Gets whether the destination accepted the player.</summary>
    public bool Accepted => Ipv4 is not null && Port is > 0 and <= ushort.MaxValue && Refusal is null;
}

/// <summary>Carries migration requests between nodes through the authenticated cluster directory.</summary>
public interface IMigrationBroker
{
    /// <summary>
    /// Asks the destination to accept the player and returns its offer. Transport and authentication failures
    /// propagate; a refused or unreachable destination never moves the player.
    /// </summary>
    ValueTask<MigrationOffer> RequestAsync(MigrationRequest request, CancellationToken cancellationToken);

    /// <summary>Tells the destination the source abandoned this migration; repeated cancellation is harmless.</summary>
    ValueTask CancelAsync(string destinationNode, string migrationId, CancellationToken cancellationToken);

    /// <summary>Serves requests addressed to this node; dispose to stop. At most one handler may be registered.</summary>
    IDisposable Serve(Func<MigrationRequest, CancellationToken, ValueTask<MigrationOffer>> accept,
        Func<string, CancellationToken, ValueTask> cancel);
}

/// <summary>
/// The destination's bounded table of accepted migrations. An arrival is consumed at most once, only by a new game
/// connection of the same build and destination listener whose remote address equals the source's observation, before it expires.
/// </summary>
public interface IMigrationArrivals
{
    /// <summary>Records an accepted migration; throws when the configured capacity is exhausted or the account already has one.</summary>
    void Expect(MigrationArrival arrival);

    /// <summary>
    /// Consumes the arrival matching the native move request, or returns null when none is pending, it expired, or the
    /// build, transport-observed listener or address differs. A mismatch leaves the arrival for the real player.
    /// </summary>
    MigrationArrival? TryConsume(string clientBuild, long userNum, IConnection connection);

    /// <summary>Removes a pending arrival and returns it, or null when it was already consumed or expired.</summary>
    MigrationArrival? Cancel(string migrationId);
}

/// <summary>A migration the destination accepted and is waiting for.</summary>
/// <param name="Request">The accepted request.</param>
/// <param name="Reservation">The held destination seat, for room migrations.</param>
/// <param name="Lifetime">How long the arrival stays pending.</param>
public sealed record MigrationArrival(MigrationRequest Request, RoomAdmissionReservation? Reservation, TimeSpan Lifetime)
{
    /// <summary>Gets the destination transport row that must accept the arriving connection.</summary>
    public string ListenerId { get; init; } = "";
}
