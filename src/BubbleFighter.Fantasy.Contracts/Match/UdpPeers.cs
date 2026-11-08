using System.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A native UDP discovery report; the identity must match a previously authenticated game session.</summary>
/// <param name="ProbeIdentity">Opaque identity supplied over that game session.</param>
/// <param name="LocalAddress">Client-reported local IPv4 address; never an authentication credential.</param>
/// <param name="LocalPort">Client-reported local UDP port.</param>
public sealed record UdpPeerDiscovery(uint ProbeIdentity, IPAddress LocalAddress, ushort LocalPort);

/// <summary>A room-scoped peer candidate delivered over the authenticated game connection.</summary>
/// <param name="Slot">Occupied room seat.</param>
/// <param name="PeerIdentity">Opaque identity of that seat for the current round.</param>
/// <param name="LocalAddresses">Optional local candidates belonging to the same observed NAT.</param>
/// <param name="LocalPort">Client-reported local port; zero when local candidates are withheld.</param>
/// <param name="OuterEndPoint">Endpoint observed by the server's UDP socket.</param>
public sealed record UdpPeerCandidate(int Slot, uint PeerIdentity, IReadOnlyList<IPAddress> LocalAddresses,
    ushort LocalPort, IPEndPoint OuterEndPoint);

/// <summary>The client consumed its ordered peer-table rows.</summary>
public sealed record UdpPeerTableAcknowledged : IMessage;

/// <summary>Untrusted client observations after its peer punching window.</summary>
/// <param name="OccupiedSeats">Number of occupied seats the client counted.</param>
/// <param name="ConnectedPeers">Number of peers for which the client observed a usable path.</param>
public sealed record UdpPeerPunchReported(uint OccupiedSeats, uint ConnectedPeers) : IMessage;

/// <summary>A native relay request to resume or suspend this sender's TCP delivery to one peer.</summary>
/// <param name="PeerIdentity">Identity delivered in this round's peer table.</param>
/// <param name="Relayed">True to restore relay delivery after UDP failure; false after a direct path succeeds.</param>
public sealed record UdpPeerRelayMembershipChanged(uint PeerIdentity, bool Relayed) : IMessage;

/// <summary>Encodes one build's native game controls and reads its UDP endpoint discovery reports.</summary>
public interface IUdpPeerDialect
{
    /// <summary>Gets the game protocol that owns this adapter.</summary>
    string Protocol { get; }

    /// <summary>Gets whether discovery must be prepared before the client leaves its waiting-room scene.</summary>
    bool DiscoveryInWaitingRoom { get; }

    /// <summary>Decodes a complete discovery datagram, returning null for a different opcode.</summary>
    UdpPeerDiscovery? ReadDiscovery(ReadOnlyMemory<byte> datagram);

    /// <summary>Identifies native movement frames for which the client supports a direct peer path.</summary>
    bool IsPeerMovement(ushort nativeOpcode);

    /// <summary>Offers the discovery listener and a fresh opaque identity over the game connection.</summary>
    ValueTask OfferAsync(ISession session, ushort port, uint identity, CancellationToken ct);

    /// <summary>Stops discovery attempts once the server has observed the endpoint or abandoned discovery.</summary>
    ValueTask StopDiscoveryAsync(ISession session, CancellationToken ct);

    /// <summary>Publishes one occupied seat, including the destination's own seat.</summary>
    ValueTask SendCandidateAsync(ISession session, UdpPeerCandidate candidate, CancellationToken ct);

    /// <summary>Marks the end of the table; the client responds with its empty table acknowledgment.</summary>
    ValueTask EndTableAsync(ISession session, CancellationToken ct);

    /// <summary>Starts the native inner and outer peer punch attempts.</summary>
    ValueTask StartPunchAsync(ISession session, CancellationToken ct);

    /// <summary>Finalizes the native peer attempts, preserving TCP fallback for unsuccessful peers.</summary>
    ValueTask FinishPunchAsync(ISession session, CancellationToken ct);
}
