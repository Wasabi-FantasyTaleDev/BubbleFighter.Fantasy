using System.Net;

namespace BubbleFighter.Fantasy.Abstractions.Net;

/// <summary>A handler for one listener's connectionless datagrams.</summary>
/// <param name="remoteEndPoint">The endpoint that sent the datagram.</param>
/// <param name="payload">The datagram bytes, copied from the socket receive buffer.</param>
/// <param name="ct">Cancellation for listener shutdown or consumer removal.</param>
public delegate ValueTask UdpDatagramHandler(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> payload, CancellationToken ct);

/// <summary>
/// Owns one listener consumer. Disposal revokes admission and cancels its callback; asynchronous disposal
/// also waits for the admitted callback to finish before the consumer's services may be released.
/// </summary>
public interface IUdpDatagramSubscription : IDisposable, IAsyncDisposable;

/// <summary>
/// A configured UDP listener endpoint. Datagram payloads are preserved as individual messages;
/// the endpoint does not apply a game packet codec or stream framing.
/// </summary>
public interface IUdpDatagramEndpoint
{
    /// <summary>Gets the protocol name assigned to this listener.</summary>
    string Protocol { get; }

    /// <summary>Gets the bound local endpoint.</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Registers the handler that owns datagrams received by this listener.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A subscription whose asynchronous disposal drains an admitted callback.</returns>
    IUdpDatagramSubscription Register(UdpDatagramHandler handler);

    /// <summary>Sends one datagram to a remote endpoint.</summary>
    /// <param name="payload">The complete datagram.</param>
    /// <param name="remoteEndPoint">The destination endpoint.</param>
    /// <param name="ct">Cancellation for the send.</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint remoteEndPoint, CancellationToken ct);
}
