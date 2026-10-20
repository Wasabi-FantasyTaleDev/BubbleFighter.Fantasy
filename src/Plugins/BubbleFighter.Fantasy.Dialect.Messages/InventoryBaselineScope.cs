namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Each live connection's inventory time origin. Legacy slot records carry an f64
/// "remaining days" field the client anchors at the moment the full inventory list was
/// published, so every S2C 801/802 body on one connection must share that origin. The
/// shop state and the inventory restatements both write this family, which is why the
/// table is owned by the plugin row rather than a single binding.
/// </summary>
internal sealed class InventoryBaselineScope(int capacity, TimeProvider clock) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _baselines = new();
    private bool _disposed;

    /// <summary>Establishes a fresh origin for one connection; throws past the connection cap.</summary>
    internal DateTimeOffset Reset(long connectionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_baselines.ContainsKey(connectionId) && _baselines.Count >= capacity)
                throw new InvalidOperationException("Inventory time origins reached their connection limit.");
            var baseline = clock.GetUtcNow();
            _baselines[connectionId] = baseline;
            return baseline;
        }
    }

    /// <summary>Returns the origin a full publication established; an update cannot precede it.</summary>
    internal DateTimeOffset Require(long connectionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_baselines.TryGetValue(connectionId, out var baseline))
                throw new InvalidOperationException("An inventory update requires a previously published full inventory.");
            return baseline;
        }
    }

    /// <summary>Drops one connection's origin.</summary>
    internal void Remove(long connectionId)
    {
        lock (_gate)
            _baselines.Remove(connectionId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _baselines.Clear();
        }
    }
}
