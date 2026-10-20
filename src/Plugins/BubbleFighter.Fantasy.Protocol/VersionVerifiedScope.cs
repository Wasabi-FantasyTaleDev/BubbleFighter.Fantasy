using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// Plugin-owned table of connections that completed the game patch version handshake.
/// One entry per live connection: it is removed when the connection closes and every
/// remaining entry is dropped when the protocol row unloads, so the table never outgrows
/// <see cref="ProtocolOptions.MaxTrackedConnections"/>.
/// </summary>
internal sealed class VersionVerifiedScope : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, byte> _verified = new();
    private readonly int _maxConnections;
    private bool _disposed;

    /// <summary>
    /// Creates an empty table whose connection bound comes from the validated protocol
    /// row configuration.
    /// </summary>
    /// <param name="maxConnections">Hard cap on distinct verified connection entries.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConnections"/> is below 1.</exception>
    public VersionVerifiedScope(int maxConnections)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        _maxConnections = maxConnections;
    }

    /// <summary>Gets the configured bound on distinct tracked connections.</summary>
    public int MaxConnections => _maxConnections;

    /// <summary>Gets the number of verified connection entries currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _verified.Count;
        }
    }

    /// <summary>
    /// Records that the patch handshake passed for a connection. A second mark for the
    /// same connection is a no-op; a new connection beyond the bound fails loudly.
    /// </summary>
    /// <param name="connectionId">The <see cref="IConnection.Id"/> that completed the handshake.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the row has already unloaded.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the table already holds
    /// <see cref="MaxConnections"/> entries and <paramref name="connectionId"/> is new.</exception>
    public void Mark(long connectionId)
    {
        if (_verified.ContainsKey(connectionId))
            return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_verified.ContainsKey(connectionId))
                return;
            if (_verified.Count >= _maxConnections)
                throw new InvalidOperationException(
                    $"Protocol version-verified table reached its max_tracked_connections cap ({_maxConnections}); refusing connection {connectionId}.");
            _verified[connectionId] = 0;
        }
    }

    /// <summary>Returns whether the connection has passed the patch handshake.</summary>
    /// <param name="connectionId">The connection id to query.</param>
    public bool IsVerified(long connectionId) => _verified.ContainsKey(connectionId);

    /// <summary>Drops the verified flag of a closed connection.</summary>
    /// <param name="connectionId">The closed <see cref="IConnection.Id"/>.</param>
    public void Remove(long connectionId) => _verified.TryRemove(connectionId, out _);

    /// <summary>Releases every verified entry this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _verified.Clear();
        }
    }
}
