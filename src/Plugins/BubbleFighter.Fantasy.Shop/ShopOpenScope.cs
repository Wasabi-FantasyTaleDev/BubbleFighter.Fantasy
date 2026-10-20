using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Owns the per-connection shop-open records this row keeps. One entry per live
/// game connection: it is removed when the connection closes and every remaining
/// one is dropped when the row unloads, so the table never outlives the plugin
/// and never grows past <see cref="Capacity"/>.
/// </summary>
internal sealed class ShopOpenScope : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, ShopOpenRecord> _opens = new();
    private readonly int _capacity;

    /// <summary>
    /// Creates a scope that refuses a new connection once it already holds
    /// <paramref name="capacity"/> distinct connection ids.
    /// </summary>
    /// <param name="capacity">Maximum distinct connections tracked at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is below 1.
    /// </exception>
    public ShopOpenScope(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "max_open_sessions must be at least 1.");
        _capacity = capacity;
    }

    /// <summary>Gets the configured connection cap for this scope.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets how many connections currently have a remembered open.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _opens.Count;
        }
    }

    /// <summary>
    /// Records the latest shop open for a connection. A second open on the same
    /// connection replaces the previous record; a new connection when the table
    /// already holds <see cref="Capacity"/> entries fails loudly.
    /// </summary>
    /// <param name="connectionId">Live game connection id.</param>
    /// <param name="record">Parsed open remember for that connection.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when adding a new connection would exceed <see cref="Capacity"/>.
    /// </exception>
    public void Remember(long connectionId, ShopOpenRecord record)
    {
        lock (_gate)
        {
            if (_opens.ContainsKey(connectionId))
            {
                _opens[connectionId] = record;
                return;
            }

            if (_opens.Count >= _capacity)
            {
                throw new InvalidOperationException(
                    $"Shop open scope already tracks {_opens.Count} connection(s) (cap {_capacity}); "
                    + $"refusing connection {connectionId}.");
            }

            _opens[connectionId] = record;
        }
    }

    /// <summary>Returns the remembered open for a connection, if any.</summary>
    /// <param name="connectionId">Live game connection id.</param>
    /// <param name="record">The stored open when found.</param>
    /// <returns><see langword="true"/> when a record was stored for that connection.</returns>
    public bool TryGet(long connectionId, out ShopOpenRecord record)
    {
        lock (_gate)
            return _opens.TryGetValue(connectionId, out record!);
    }

    /// <summary>Drops the remembered open of a closed connection.</summary>
    /// <param name="connectionId">Closed game connection id.</param>
    public void Remove(long connectionId)
    {
        lock (_gate)
            _opens.Remove(connectionId);
    }

    /// <summary>Clears every open this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
            _opens.Clear();
    }
}
