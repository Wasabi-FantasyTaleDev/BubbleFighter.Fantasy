using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Conserves issued FinalZone items across exclusive pickups, live drops and terminal backpacks.</summary>
internal sealed partial class FinalZoneItemLedger
{
    private readonly IReadOnlyDictionary<int, FinalZoneItemSpec> _specs;
    private readonly Dictionary<int, FinalZoneMapItem> _ground = [];
    private readonly Dictionary<int, Dictionary<int, FinalZoneBackpackEntry>> _held = [];
    private readonly Dictionary<int, List<FinalZoneBackpackEntry>> _backpacks = [];
    private readonly HashSet<int> _drones = [];
    private readonly HashSet<int> _closedInventories = [];
    private readonly int _firstObjectId;
    private readonly int _maximumObjects;
    private readonly int _maximumQuantity;
    private int _issued;
    private bool _initialized;

    internal FinalZoneItemLedger(IReadOnlyDictionary<int, FinalZoneItemSpec> specs, IEnumerable<int> actors,
        int firstObjectId, int maximumObjects, int maximumQuantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstObjectId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumObjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQuantity);
        _ = checked(firstObjectId + maximumObjects - 1);
        _specs = specs;
        _firstObjectId = firstObjectId;
        _maximumObjects = maximumObjects;
        _maximumQuantity = maximumQuantity;
        foreach (var actor in actors)
            if (!_held.TryAdd(actor, [])) throw new ArgumentException("Duplicate FinalZone participant.", nameof(actors));
    }

    internal int GroundCount => _ground.Count;
    internal int BackpackCount => _backpacks.Count;
    internal int DroneCount => _drones.Count;
    internal int IssuedObjects => _issued;
    internal IReadOnlyCollection<FinalZoneBackpackEntry> Held(int actor) =>
        Array.AsReadOnly(_held[actor].Values.ToArray());

    /// <summary>Installs a complete map manifest atomically before any client sees its handles.</summary>
    internal void Initialize(IReadOnlyList<FinalZoneMapItem> items,
        IReadOnlyList<FinalZoneBackpackEntry>? openingInventory = null)
    {
        if (_initialized) throw new InvalidOperationException("FinalZone items are already initialized.");
        if (items.Count > _maximumObjects) throw new InvalidDataException("FinalZone manifest exceeds the round object budget.");
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.ObjectId != checked(_firstObjectId + index))
                throw new InvalidDataException("FinalZone manifest handles must occupy the reserved contiguous range.");
            ValidateEntry(Entry(item));
            ValidatePosition(item.X, item.Y, item.Z);
        }
        var opening = new Dictionary<int, FinalZoneBackpackEntry>();
        foreach (var item in openingInventory ?? [])
        {
            ValidateEntry(item);
            if (!opening.TryAdd(item.ItemSpecId, Freeze(item)))
                throw new InvalidDataException("FinalZone opening inventory contains duplicate specifications.");
        }
        foreach (var inventory in _held.Values)
            foreach (var item in opening.Values) inventory.Add(item.ItemSpecId, item);
        foreach (var item in items) _ground.Add(item.ObjectId, Freeze(item));
        _issued = items.Count;
        _initialized = true;
    }

    /// <summary>Moves a ground item to exactly one known participant; repeated or foreign claims leave both owners unchanged.</summary>
    internal bool TryPickUp(int actor, int objectId)
    {
        if (!_initialized || !_held.TryGetValue(actor, out var inventory) || _closedInventories.Contains(actor)
            || _linkedRods.Contains(objectId) || !_ground.TryGetValue(objectId, out var item)) return false;
        var entry = Entry(item);
        if (!CanAdd(inventory, entry)) return false;
        Add(inventory, entry);
        _ground.Remove(objectId);
        CancelTrapContaining(objectId);
        return true;
    }

    /// <summary>Debits a client-reported live drop before publishing new world handles, without accepting unissued contents.</summary>
    internal IReadOnlyList<FinalZoneMapItem>? TryDrop(int actor, IReadOnlyList<FinalZoneMapItem> requested)
    {
        if (!_initialized || !_held.TryGetValue(actor, out var inventory) || _closedInventories.Contains(actor)) return null;
        if (requested.Count == 0) return [];
        var entries = requested.Select(Entry).ToArray();
        if (!CanReturn(inventory, entries) || requested.Count > _maximumObjects - _issued) return null;
        foreach (var item in requested) ValidatePosition(item.X, item.Y, item.Z);
        var result = requested.Select((item, index) => Freeze(item with
        {
            ObjectId = checked(_firstObjectId + _issued + index),
        })).ToArray();
        foreach (var entry in entries) Remove(inventory, entry);
        foreach (var item in result) _ground.Add(item.ObjectId, item);
        _issued += result.Length;
        return Array.AsReadOnly(result);
    }

    /// <summary>Consumes a terminal inventory once; absent or lower quantities account for locally consumed ammunition and supplies.</summary>
    internal RoundFinalZoneBackpackSpawned? TryDropBackpack(RoundFinalZoneBackpackDropped requested)
    {
        var actor = requested.OwnerActor;
        if (!_initialized || !_held.TryGetValue(actor, out var inventory) || _closedInventories.Contains(actor)
            || !CanReturn(inventory, requested.Items) || _issued == _maximumObjects) return null;
        ValidatePosition(requested.X, requested.Y, requested.Z);
        var contents = requested with { Items = Array.AsReadOnly(requested.Items.Select(Freeze).ToArray()) };
        var id = checked(_firstObjectId + _issued);
        _backpacks.Add(id, contents.Items.ToList());
        _issued++;
        _closedInventories.Add(actor);
        inventory.Clear();
        return new(id, contents);
    }

    /// <summary>Transfers one complete native entry from a world backpack and keeps the handle closed to duplicate claims.</summary>
    internal bool TryPickUpBackpack(int actor, int objectId, int itemSpecId)
    {
        if (!_initialized || !_held.TryGetValue(actor, out var inventory) || _closedInventories.Contains(actor)
            || !_backpacks.TryGetValue(objectId, out var contents) || contents.FirstOrDefault(entry => entry.ItemSpecId == itemSpecId) is not { } item
            || !CanAdd(inventory, item)) return false;
        Add(inventory, item);
        // kr-1256 sub_4627C0 removes the first matching vector entry, preserving repeated specifications.
        contents.Remove(item);
        if (contents.Count == 0) _backpacks.Remove(objectId);
        return true;
    }

    /// <summary>Reserves a nonreusable drone handle from the same round budget as all other world objects.</summary>
    internal int? TryCreateDrone()
    {
        if (!_initialized || _issued == _maximumObjects) return null;
        var id = checked(_firstObjectId + _issued++);
        _drones.Add(id);
        return id;
    }

    /// <summary>Converts one issued drone into one seeded supply box; repeated destruction cannot mint another box.</summary>
    internal int? TryDropDrone(int droneId, IReadOnlyList<FinalZoneBackpackEntry> entries)
    {
        if (!_drones.Contains(droneId)) return null;
        var id = TryCreateSupply(entries);
        if (id is not null) _drones.Remove(droneId);
        return id;
    }

    /// <summary>Registers server-generated supply entries before their shared seed is published to clients.</summary>
    internal int? TryCreateSupply(IReadOnlyList<FinalZoneBackpackEntry> entries)
    {
        if (!_initialized || _issued == _maximumObjects) return null;
        foreach (var entry in entries) ValidateEntry(entry);
        var frozen = entries.Select(Freeze).ToList();
        var id = checked(_firstObjectId + _issued);
        _backpacks.Add(id, frozen);
        _issued++;
        return id;
    }

    private bool CanReturn(Dictionary<int, FinalZoneBackpackEntry> inventory, IReadOnlyList<FinalZoneBackpackEntry> entries)
    {
        var keys = new HashSet<int>();
        foreach (var entry in entries)
        {
            ValidateEntry(entry);
            if (!keys.Add(entry.ItemSpecId) || !inventory.TryGetValue(entry.ItemSpecId, out var held)
                || entry.Quantity > held.Quantity || !SameIdentity(held, entry)) return false;
        }
        return true;
    }

    private bool CanAdd(Dictionary<int, FinalZoneBackpackEntry> inventory, FinalZoneBackpackEntry entry)
    {
        if (!inventory.TryGetValue(entry.ItemSpecId, out var held)) return true;
        return SameIdentity(held, entry) && entry.Quantity <= _maximumQuantity - held.Quantity;
    }

    private static void Add(Dictionary<int, FinalZoneBackpackEntry> inventory, FinalZoneBackpackEntry entry) =>
        inventory[entry.ItemSpecId] = Freeze(entry with
        {
            Quantity = entry.Quantity + (inventory.TryGetValue(entry.ItemSpecId, out var held) ? held.Quantity : 0),
        });

    private static void Remove(Dictionary<int, FinalZoneBackpackEntry> inventory, FinalZoneBackpackEntry entry)
    {
        var held = inventory[entry.ItemSpecId];
        if (held.Quantity == entry.Quantity) inventory.Remove(entry.ItemSpecId);
        else inventory[entry.ItemSpecId] = held with { Quantity = held.Quantity - entry.Quantity };
    }

    private FinalZoneBackpackEntry Entry(FinalZoneMapItem item) => new(item.ItemSpecId,
        _specs.TryGetValue(item.ItemSpecId, out var spec) ? spec.WeaponId : int.MinValue, item.Quantity, item.Reinforcement);

    private void ValidateEntry(FinalZoneBackpackEntry entry)
    {
        if (!_specs.TryGetValue(entry.ItemSpecId, out var spec) || spec.WeaponId != entry.WeaponId
            || entry.Quantity <= 0 || entry.Quantity > _maximumQuantity)
            throw new InvalidDataException("FinalZone entry has an unknown specification, mismatched weapon or invalid amount.");
    }

    private static bool SameIdentity(FinalZoneBackpackEntry a, FinalZoneBackpackEntry b) => a.WeaponId == b.WeaponId
        && a.Reinforcement.Unknown4.SequenceEqual(b.Reinforcement.Unknown4)
        && a.Reinforcement.Unknown20.SequenceEqual(b.Reinforcement.Unknown20);

    private static FinalZoneReinforcement Freeze(FinalZoneReinforcement value) => new(
        Array.AsReadOnly(value.Unknown4.ToArray()), Array.AsReadOnly(value.Unknown20.ToArray()));
    private static FinalZoneBackpackEntry Freeze(FinalZoneBackpackEntry entry) => entry with { Reinforcement = Freeze(entry.Reinforcement) };
    private static FinalZoneMapItem Freeze(FinalZoneMapItem item) => item with { Reinforcement = Freeze(item.Reinforcement) };
    private static void ValidatePosition(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new InvalidDataException("FinalZone world positions must be finite.");
    }
}
