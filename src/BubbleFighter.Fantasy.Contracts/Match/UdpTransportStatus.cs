using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>How a room member's round traffic travels right now.</summary>
public enum UdpPathState
{
    /// <summary>Everything travels over the match TCP connection.</summary>
    Tcp,

    /// <summary>UDP setup is under way; TCP still carries the traffic.</summary>
    Negotiating,

    /// <summary>A verified UDP path carries the member's movement.</summary>
    Udp,
}

/// <summary>One room member's transport as the UDP coordinator of its build sees it.</summary>
/// <param name="State">The member's current path.</param>
/// <param name="Detail">A short operator-facing reason, such as the setup step or why TCP is kept.</param>
public sealed record UdpPathStatus(UdpPathState State, string Detail);

/// <summary>
/// Collects the UDP status reporters of one game protocol. The rooms provide it; the UDP rows read the rooms' roster,
/// so they start later and register their reporter here instead of being a dependency of the rooms.
/// </summary>
public interface IUdpTransportStatusRegistry
{
    /// <summary>Gets the game protocol whose rooms read the registered reporters.</summary>
    string Protocol { get; }

    /// <summary>Adds a reporter until the returned registration is disposed.</summary>
    /// <param name="status">The reporter.</param>
    /// <returns>The registration.</returns>
    IDisposable Register(IUdpTransportStatus status);
}

/// <summary>
/// Reports the live UDP state of game sessions for one game protocol. Provided by the row that negotiates UDP for
/// that build (server relay or direct peers); a build without one has no UDP and uses TCP only.
/// </summary>
public interface IUdpTransportStatus
{
    /// <summary>Gets the game protocol whose sessions this reports.</summary>
    string Protocol { get; }

    /// <summary>Gets the transport kind shown to players, such as "relay" or "peer".</summary>
    string Kind { get; }

    /// <summary>Describes the transport of one seated game session.</summary>
    /// <param name="game">The member's game connection.</param>
    /// <returns>The member's current path.</returns>
    UdpPathStatus Describe(ISession game);
}
