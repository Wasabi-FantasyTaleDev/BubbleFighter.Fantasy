using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Freezes the accepted room equipment and instance modifiers used to resolve round-start combat tables.</summary>
public sealed class MatchSeedEquipment : IEquatable<MatchSeedEquipment>
{
    private readonly int[] _items;

    /// <summary>Copies the worn set and retains the room's independently accepted weapon-spec overrides.</summary>
    /// <param name="modeId">The shared round mode, not a client-supplied seed field.</param>
    /// <param name="wornItemIds">The authoritative worn item snapshot already published in the room.</param>
    /// <param name="primarySpecification">The accepted positive primary specification; null or nonpositive selects the worn primary.</param>
    /// <param name="sidearmSpecification">The accepted positive sidearm specification; null or nonpositive selects the worn sidearm.</param>
    /// <param name="itemAttributes">The published native modifiers; their order and value vectors are copied.</param>
    public MatchSeedEquipment(int modeId, IReadOnlyList<int> wornItemIds,
        int? primarySpecification = null, int? sidearmSpecification = null,
        IReadOnlyList<RoomItemAttribute>? itemAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(wornItemIds);
        if (wornItemIds.Count > InventoryLimits.MaxEquippedSlots)
            throw new ArgumentException("Seed equipment exceeds the accepted inventory snapshot bound.", nameof(wornItemIds));
        ModeId = modeId;
        _items = wornItemIds.Distinct().Order().ToArray();
        WornItemIds = Array.AsReadOnly(_items);
        ItemAttributes = new RoundEquipmentSnapshot(_items, itemAttributes ?? []).ItemAttributes;
        // cn-324 sub_805DE0 uses a temporary selector only when positive; zero and negative sentinels use the worn slot.
        PrimarySpecification = primarySpecification is > 0 ? primarySpecification : null;
        SidearmSpecification = sidearmSpecification is > 0 ? sidearmSpecification : null;
    }

    /// <summary>Gets the round mode whose resource replacement rules apply.</summary>
    public int ModeId { get; }

    /// <summary>Gets a private, canonical copy of the accepted worn set.</summary>
    public IReadOnlyList<int> WornItemIds { get; }

    /// <summary>Gets the privately copied native modifiers in their original per-item application order.</summary>
    public IReadOnlyList<RoomItemAttribute> ItemAttributes { get; }

    /// <summary>Gets the temporary primary specification when the room publishes one.</summary>
    public int? PrimarySpecification { get; }

    /// <summary>Gets the temporary sidearm specification when the room publishes one.</summary>
    public int? SidearmSpecification { get; }

    /// <summary>Compares equipment values so repeated snapshots do not become distinct seed installations.</summary>
    public bool Equals(MatchSeedEquipment? other) => other is not null && ModeId == other.ModeId
        && PrimarySpecification == other.PrimarySpecification && SidearmSpecification == other.SidearmSpecification
        && _items.AsSpan().SequenceEqual(other._items)
        && ItemAttributes.Count == other.ItemAttributes.Count
        && ItemAttributes.Zip(other.ItemAttributes).All(pair =>
            pair.First.ItemId == pair.Second.ItemId && pair.First.Kind == pair.Second.Kind
            && pair.First.Unknown28 == pair.Second.Unknown28 && pair.First.Unknown32 == pair.Second.Unknown32
            && pair.First.Values.SequenceEqual(pair.Second.Values));

    /// <summary>Compares another object's frozen equipment values.</summary>
    public override bool Equals(object? obj) => obj is MatchSeedEquipment other && Equals(other);

    /// <summary>Hashes the same canonical inputs used by seed-request equality.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ModeId);
        hash.Add(PrimarySpecification);
        hash.Add(SidearmSpecification);
        foreach (var item in _items) hash.Add(item);
        foreach (var attribute in ItemAttributes)
        {
            hash.Add(attribute.ItemId);
            hash.Add(attribute.Kind);
            hash.Add(attribute.Unknown28);
            hash.Add(attribute.Unknown32, StringComparer.Ordinal);
            hash.Add(attribute.Values.Count);
            foreach (var value in attribute.Values) hash.Add(value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Checks immutable round-start seed inputs before any countdown or seed publication.</summary>
public interface IMatchSeedInputValidator
{
    /// <summary>Gets the match protocol whose resource policy validates the request.</summary>
    string Protocol { get; }

    /// <summary>Checks whether this mode's seed policy requires refusing a live weapon replacement.</summary>
    /// <param name="modeId">The authoritative mode of the active round.</param>
    /// <returns>True only for modes that apply a fixed-loadout weapon contribution.</returns>
    bool RequiresFixedEquipment(int modeId);

    /// <summary>Rejects unsupported or missing inputs without drawing bits, sending frames, or changing issuance state.</summary>
    /// <param name="request">The complete owned round and equipment snapshot.</param>
    void Validate(MatchSeedTablesRequested request);
}
