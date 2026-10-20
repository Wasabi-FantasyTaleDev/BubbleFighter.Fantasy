using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Owns the last clan-open record this row remembers per live game connection. One entry
/// per connection: it is removed when the connection closes and every remaining one is
/// dropped when the row unloads, so the table never outgrows
/// <see cref="ClanOptions.MaxTrackedOpens"/>.
/// </summary>
internal sealed class ClanOpenScope : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, ClanOpenRecord> _opens = [];
    private readonly int _capacity;
    private bool _disposed;

    /// <summary>Creates a scope whose retained opens cannot exceed <paramref name="capacity"/>.</summary>
    /// <param name="capacity">Inclusive upper bound on distinct connection ids.</param>
    public ClanOpenScope(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        _capacity = capacity;
    }

    /// <summary>
    /// Remembers the open for a connection. A second write on the same connection replaces
    /// the prior record; a new connection is refused once the table is full.
    /// </summary>
    public void Remember(long connectionId, ClanOpenRecord record)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opens.ContainsKey(connectionId))
            {
                _opens[connectionId] = record;
                return;
            }

            if (_opens.Count >= _capacity)
                throw new InvalidOperationException(
                    $"Clan open table is full ({_capacity} connections). Raise max_tracked_opens or close idle clients.");

            _opens[connectionId] = record;
        }
    }

    /// <summary>Returns the last open for <paramref name="connectionId"/>, or <c>null</c>.</summary>
    public ClanOpenRecord? TryGet(long connectionId)
    {
        lock (_gate)
            return _opens.TryGetValue(connectionId, out var record) ? record : null;
    }

    /// <summary>Drops the open of a closed connection.</summary>
    public void Remove(long connectionId)
    {
        lock (_gate)
            _opens.Remove(connectionId);
    }

    /// <summary>Releases every open this row still holds.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _opens.Clear();
        }
    }
}
