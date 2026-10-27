using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;

namespace BubbleFighter.Fantasy.Abstractions.Net;

/// <summary>
/// A single accepted transport connection. Reads are performed through <see cref="Input"/>
/// and writes through <see cref="Output"/>; the protocol that owns the connection drives
/// both. <see cref="Items"/> carries per-connection state shared across plugins.
/// </summary>
public interface IConnection
{
    /// <summary>Gets the kernel-assigned connection id.</summary>
    long Id { get; }

    /// <summary>Gets the remote endpoint.</summary>
    EndPoint RemoteEndPoint { get; }

    /// <summary>Gets the inbound byte pipe.</summary>
    PipeReader Input { get; }

    /// <summary>Gets the outbound byte pipe.</summary>
    PipeWriter Output { get; }

    /// <summary>Gets arbitrary per-connection state keyed by name.</summary>
    ConcurrentDictionary<string, object> Items { get; }

    /// <summary>Gets whether the connection has been closed.</summary>
    bool IsClosed { get; }

    /// <summary>
    /// Closes the connection. Only the first call closes it and later calls return immediately;
    /// the first reason is the one recorded and logged. Pending reads are cancelled and the
    /// transport's read loop exits on its own, so this method does not wait for it, and writes
    /// queued after the close fail downstream.
    /// </summary>
    /// <param name="reason">A short human-readable reason, logged by the transport.</param>
    ValueTask CloseAsync(string reason);
}

/// <summary>
/// A wire protocol that owns an accepted connection's read loop until the connection ends.
/// </summary>
public interface IConnectionProtocol
{
    /// <summary>Gets the protocol name listeners resolve it by, for example <c>kr-1256/game</c>.</summary>
    string Name { get; }

    /// <summary>Runs the protocol over the connection until it ends.</summary>
    /// <param name="connection">The accepted connection.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RunAsync(IConnection connection, CancellationToken ct);
}

/// <summary>
/// Registry of named connection protocols. Provided by the kernel built-in row
/// <c>core.protocol-registry</c> so it can be replaced like any other row.
/// </summary>
public interface IConnectionProtocolRegistry
{
    /// <summary>Registers a protocol under <see cref="IConnectionProtocol.Name"/>.</summary>
    /// <param name="protocol">The protocol.</param>
    /// <returns>A disposer that removes the registration.</returns>
    IDisposable Register(IConnectionProtocol protocol);

    /// <summary>Resolves a protocol by name.</summary>
    /// <param name="name">The protocol name.</param>
    /// <returns>The registered protocol.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no protocol is registered under the name.</exception>
    IConnectionProtocol Resolve(string name);
}

/// <summary>Raised by a transport after it accepts a connection, dispatched serially before
/// the owning protocol starts.</summary>
/// <param name="Connection">The accepted connection.</param>
/// <param name="ListenerId">The row id of the listener that accepted it.</param>
public sealed record ConnectionAcceptedEvent(IConnection Connection, string ListenerId) : IEvent;

/// <summary>Raised by a transport when a connection ends, dispatched via emit.</summary>
/// <param name="Connection">The closed connection.</param>
/// <param name="Reason">A short human-readable reason.</param>
public sealed record ConnectionClosedEvent(IConnection Connection, string Reason) : IEvent;
