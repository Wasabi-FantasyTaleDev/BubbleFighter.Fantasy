using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Equipment.Sessions;

/// <summary>
/// Owns the per-connection <see cref="EquippedItemSet"/> entries this row tracks. One set
/// per live game connection: it is removed when the connection closes and every remaining
/// entry is dropped when the row unloads, so the table never outgrows
/// <see cref="EquipmentOptions.MaxTrackedConnections"/>.
/// </summary>
public sealed class EquippedItemScope : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, EquippedItemSet> _sets = new();
    private readonly int _maxConnections;
    private readonly int _maxEquippedPerSession;
    private bool _disposed;

    /// <summary>
    /// Creates an empty table whose connection and per-set key bounds come from the
    /// validated equipment row configuration.
    /// </summary>
    /// <param name="maxConnections">Hard cap on distinct connection entries.</param>
    /// <param name="maxEquippedPerSession">Hard cap on worn keys inside each set.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either bound is below 1.</exception>
    public EquippedItemScope(int maxConnections, int maxEquippedPerSession)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEquippedPerSession, 1);
        _maxConnections = maxConnections;
        _maxEquippedPerSession = maxEquippedPerSession;
    }

    /// <summary>Gets the configured bound on distinct tracked connections.</summary>
    public int MaxConnections => _maxConnections;

    /// <summary>Gets the configured bound on worn keys per connection set.</summary>
    public int MaxEquippedPerSession => _maxEquippedPerSession;

    /// <summary>Gets the number of connection entries currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _sets.Count;
        }
    }

    /// <summary>
    /// Returns the worn-key set for a live connection, creating an empty one when the
    /// table still has room. A second call for the same connection reuses the existing set.
    /// </summary>
    /// <param name="connectionId">The <see cref="IConnection.Id"/> of the caller.</param>
    /// <returns>The connection's equipped set.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the row has already unloaded.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the table already holds
    /// <see cref="MaxConnections"/> entries and <paramref name="connectionId"/> is new.</exception>
    public EquippedItemSet GetOrAdd(long connectionId)
    {
        if (_sets.TryGetValue(connectionId, out var existing))
            return existing;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sets.TryGetValue(connectionId, out existing))
                return existing;
            if (_sets.Count >= _maxConnections)
                throw new InvalidOperationException(
                    $"Equipment equipped-item table reached its max_tracked_connections cap ({_maxConnections}); refusing connection {connectionId}.");
            var created = new EquippedItemSet(_maxEquippedPerSession);
            _sets[connectionId] = created;
            return created;
        }
    }

    /// <summary>Looks up a connection's set without creating one.</summary>
    /// <param name="connectionId">The connection id to resolve.</param>
    /// <param name="set">Receives the set when one is tracked.</param>
    /// <returns><see langword="true"/> when the connection has an entry.</returns>
    public bool TryGet(long connectionId, out EquippedItemSet set) =>
        _sets.TryGetValue(connectionId, out set!);

    /// <summary>Drops the set of a closed connection.</summary>
    /// <param name="connectionId">The closed <see cref="IConnection.Id"/>.</param>
    public void Remove(long connectionId) => _sets.TryRemove(connectionId, out _);

    /// <summary>Releases every set this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _sets.Clear();
        }
    }
}
