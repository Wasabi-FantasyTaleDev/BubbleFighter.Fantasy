namespace BubbleFighter.Fantasy.RoomCore;

/// <summary>State shared by room membership rules independently of a client's packet layout.</summary>
public interface IRoomMember
{
    /// <summary>Gets the occupied seat index.</summary>
    int Slot { get; }

    /// <summary>Gets whether the member has completed the room's ready action.</summary>
    bool Ready { get; }

    /// <summary>Gets whether the member is currently in the shop dialog.</summary>
    bool InShop { get; }

    /// <summary>Gets whether the member is currently in the inventory dialog.</summary>
    bool InInventory { get; }
}

/// <summary>
/// Owns a bounded, join-ordered room roster. The caller serializes mutations through its room
/// executor or gate; the first remaining member becomes host when the current host leaves.
/// </summary>
/// <typeparam name="TMember">The build-independent member state supplied by the owning plugin.</typeparam>
public sealed class RoomMembers<TMember> where TMember : IRoomMember
{
    private readonly List<TMember> _members;
    private readonly HashSet<int> _closedSlots = [];
    private readonly HashSet<int> _reservedSlots = [];
    private readonly int _capacity;
    private int? _delegatedMaster;

    /// <summary>Creates an empty roster with a fixed number of seats.</summary>
    /// <param name="capacity">Positive maximum seat count.</param>
    public RoomMembers(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _members = new List<TMember>(capacity);
    }

    /// <summary>Gets the members in join order; read while holding the owning room's gate.</summary>
    public IReadOnlyList<TMember> Members => _members;

    /// <summary>Gets closed empty seats; read while holding the owning room's gate.</summary>
    public IReadOnlyCollection<int> ClosedSlots => _closedSlots;

    /// <summary>Gets whether no open seat remains.</summary>
    public bool IsFull => _members.Count + _closedSlots.Count + _reservedSlots.Count >= _capacity;

    /// <summary>Reserves an open seat for a non-connection occupant without making it a closed seat.</summary>
    /// <param name="slot">The seat to reserve.</param>
    /// <returns>Whether the seat was open and is now reserved.</returns>
    public bool Reserve(int slot)
    {
        if (slot < 0 || slot >= _capacity || _closedSlots.Contains(slot)
            || _members.Any(member => member.Slot == slot))
            return false;
        return _reservedSlots.Add(slot);
    }

    /// <summary>Releases a non-connection occupant's seat so a human may join it.</summary>
    /// <param name="slot">The reserved seat.</param>
    /// <returns>Whether a reservation was released.</returns>
    public bool Release(int slot) => _reservedSlots.Remove(slot);

    /// <summary>Gets the delegated master's seat, or the first remaining seat when no delegation is held.</summary>
    public int MasterSlot => _delegatedMaster ?? (_members.Count == 0 ? -1 : _members[0].Slot);

    /// <summary>Delegates ownership to an occupied seat without changing member or profile order.</summary>
    /// <param name="slot">The target occupied seat.</param>
    /// <returns>Whether ownership moved to another member.</returns>
    public bool TransferMaster(int slot)
    {
        if (slot == MasterSlot || !_members.Any(member => member.Slot == slot))
            return false;
        _delegatedMaster = slot;
        return true;
    }

    /// <summary>Gets whether every non-host member is ready and outside blocking dialogs.</summary>
    public bool CanStart => _members.Count > 0 && _members.All(member => member.Slot == MasterSlot
        || (member.Ready && !member.InShop && !member.InInventory));

    /// <summary>Occupies the first open seat with a member produced for that seat.</summary>
    /// <param name="create">Creates the member with its assigned seat index.</param>
    /// <returns>The member added to the roster.</returns>
    public TMember Add(Func<int, TMember> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (IsFull)
            throw new InvalidOperationException($"Room is full ({_capacity} seats, {_closedSlots.Count} closed).");
        var slot = Enumerable.Range(0, _capacity).First(candidate =>
            !_closedSlots.Contains(candidate) && !_reservedSlots.Contains(candidate)
            && _members.All(member => member.Slot != candidate));
        var member = create(slot);
        if (member.Slot != slot)
            throw new InvalidOperationException($"Member occupies seat {member.Slot}, expected {slot}.");
        _members.Add(member);
        return member;
    }

    /// <summary>Removes matching members without changing the join order of survivors.</summary>
    /// <param name="match">Selects departing members.</param>
    /// <returns>The number removed.</returns>
    public int RemoveAll(Predicate<TMember> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var removed = _members.RemoveAll(match);
        if (_delegatedMaster is { } master && !_members.Any(member => member.Slot == master))
            _delegatedMaster = null;
        return removed;
    }

    /// <summary>Closes or reopens an empty seat; occupied or out-of-range seats are rejected.</summary>
    /// <param name="slot">The seat index.</param>
    /// <param name="closed">Whether the seat should be closed.</param>
    /// <returns>Whether the state changed.</returns>
    public bool SetSlotClosed(int slot, bool closed)
    {
        if (slot < 0 || slot >= _capacity)
            throw new InvalidOperationException($"Room has no seat {slot}.");
        if (_reservedSlots.Contains(slot) || _members.Any(member => member.Slot == slot))
            throw new InvalidOperationException($"Room seat {slot} holds a player and cannot be closed.");
        return closed ? _closedSlots.Add(slot) : _closedSlots.Remove(slot);
    }
}
