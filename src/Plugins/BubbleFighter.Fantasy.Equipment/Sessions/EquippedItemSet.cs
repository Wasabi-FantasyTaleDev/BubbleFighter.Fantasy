namespace BubbleFighter.Fantasy.Equipment.Sessions;

/// <summary>
/// Bounded set of the inventory / article keys a single connection currently has worn.
/// One instance is created on the first C2S 804 inside <see cref="EquippedItemScope"/>
/// and is released when that connection closes or the equipment row unloads. Capacity is
/// the validated row tunable <see cref="EquipmentOptions.MaxEquippedPerSession"/>.
/// </summary>
public sealed class EquippedItemSet
{
    private readonly object _gate = new();
    private readonly HashSet<long> _keys;
    private readonly int _capacity;

    /// <summary>Creates an empty set with a hard key capacity.</summary>
    /// <param name="capacity">The maximum number of distinct keys that may be tracked.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is below 1.</exception>
    public EquippedItemSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _keys = new HashSet<long>(capacity);
    }

    /// <summary>Gets the hard key capacity of this set.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets the number of tracked keys.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _keys.Count;
        }
    }

    /// <summary>Returns whether a key is currently tracked as worn.</summary>
    /// <param name="key">The inventory / article key.</param>
    public bool Contains(long key)
    {
        lock (_gate)
            return _keys.Contains(key);
    }

    /// <summary>
    /// Records a key as worn. The operation is idempotent: a key that is already tracked
    /// is left in place and reported as not newly added.
    /// </summary>
    /// <param name="key">The inventory / article key.</param>
    /// <returns><see langword="true"/> when the key was newly added; <see langword="false"/> when it was already tracked.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the set is at capacity and the key is new.</exception>
    public bool Equip(long key)
    {
        lock (_gate)
        {
            if (_keys.Contains(key))
                return false;
            if (_keys.Count >= _capacity)
                throw new InvalidOperationException($"Equipped set is full ({_capacity}); refusing key {key}.");
            _keys.Add(key);
            return true;
        }
    }

    /// <summary>
    /// Clears a key. Removing a key that was never tracked is a no-op, not an error.
    /// </summary>
    /// <param name="key">The inventory / article key.</param>
    /// <returns><see langword="true"/> when the key was tracked and is now cleared.</returns>
    public bool Unequip(long key)
    {
        lock (_gate)
            return _keys.Remove(key);
    }
}
