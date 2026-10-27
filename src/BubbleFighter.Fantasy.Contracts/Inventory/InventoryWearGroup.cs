namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>A mutually exclusive equipment position expressed as an inclusive item-id range.</summary>
/// <param name="FirstItemId">Lowest item id accepted by this position.</param>
/// <param name="LastItemId">Highest item id accepted by this position.</param>
/// <param name="LegacyDefaultItemId">Preferred owned item when repairing an ambiguous legacy equipped snapshot.</param>
/// <param name="RequireEquipped">Whether an empty position returns to its owned initial item.</param>
public sealed record InventoryWearGroup(int FirstItemId, int LastItemId, int LegacyDefaultItemId, bool RequireEquipped = false)
{
    /// <summary>Gets the explicit item identities sharing a client body part or accessory slot, or null for a numeric position.</summary>
    public IReadOnlyList<int>? ItemIds { get; init; }

    /// <summary>Tests whether the item belongs to this equipment position.</summary>
    public bool Contains(int itemId) => ItemIds is { } ids ? ids.Contains(itemId) : itemId >= FirstItemId && itemId <= LastItemId;
}

/// <summary>Applies an equipment selection to both persisted inventory representations without adding or removing items.</summary>
public static class InventoryWearSelection
{
    /// <summary>
    /// Normalizes exclusive positions and optionally changes one owned item's worn state.
    /// Character-bag selections take precedence; the legacy update snapshot is consulted only for an empty position.
    /// Input order breaks historical duplicate selections; stores may order it by last update time.
    /// Unknown flag bits are preserved. An unknown requested item returns null before any changes.
    /// </summary>
    /// <param name="rows">The complete both-bag snapshot to rewrite; the character bag is authoritative and is not modified.</param>
    /// <param name="groups">The position rules. Numeric ranges must be positive, mutually disjoint and contain their legacy default.</param>
    /// <param name="itemId">The item whose worn state changes, or null to repair the current selections only.</param>
    /// <param name="worn">The target worn state of <paramref name="itemId"/> when it is given.</param>
    /// <returns>Every input row with its worn bit rewritten, or null when the requested item is not in the character bag.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the row or bag bounds are exceeded.</exception>
    /// <exception cref="ArgumentException">Thrown when a position range is invalid or overlaps another numeric position.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a required position lacks its initial item in the character bag.</exception>
    public static IReadOnlyList<InventorySlotRecord>? Apply(
        IReadOnlyList<InventorySlotRecord> rows, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(groups);
        if (rows.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag || groups.Count > 64)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (rows.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new ArgumentOutOfRangeException(nameof(rows), "Each inventory bag must stay within its slot limit.");
        foreach (var group in groups)
            if (group.FirstItemId <= 0 || group.LastItemId < group.FirstItemId || !group.Contains(group.LegacyDefaultItemId)
                || group.ItemIds is { } members && (group.RequireEquipped || members.Count == 0 || members.Count > 65536 || members.Any(id => id <= 0))
                || groups.Any(other => !ReferenceEquals(other, group) && group.ItemIds is null && other.ItemIds is null
                    && other.FirstItemId <= group.LastItemId && other.LastItemId >= group.FirstItemId))
                throw new ArgumentException("Equipment ranges must be positive, disjoint and contain their legacy default.", nameof(groups));
        var bag = rows.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray();
        if (itemId is { } requested && (requested <= 0 || !bag.Any(row => row.ItemId == requested))) return null;
        var selections = new Dictionary<InventoryWearGroup, int>();
        foreach (var group in groups.Where(group => group.ItemIds is null))
        {
            var selected = bag.LastOrDefault(row => group.Contains(row.ItemId) && (row.Flags & 1) != 0)?.ItemId ?? 0;
            if (selected == 0)
            {
                var legacy = rows.Where(row => row.Bag == InventoryBag.EquippedBag && group.Contains(row.ItemId)
                    && (row.Flags & 1) != 0 && bag.Any(owned => owned.ItemId == row.ItemId)).ToArray();
                selected = legacy.Any(row => row.ItemId == group.LegacyDefaultItemId)
                    ? group.LegacyDefaultItemId : legacy.LastOrDefault()?.ItemId ?? 0;
            }
            if (itemId is { } id && group.Contains(id))
                selected = worn ? id : selected == id ? 0 : selected;
            if (selected == 0 && group.RequireEquipped)
                selected = bag.Any(row => row.ItemId == group.LegacyDefaultItemId) ? group.LegacyDefaultItemId
                    : throw new InvalidOperationException($"Required equipment position lacks its initial item {group.LegacyDefaultItemId}.");
            selections.Add(group, selected);
        }
        var locationGroups = groups.Where(group => group.ItemIds is not null).ToArray();
        var locationItems = new HashSet<int>();
        var occupied = new HashSet<InventoryWearGroup>();
        var candidates = bag.Where(row => (row.Flags & 1) != 0).Reverse().Select(row => row.ItemId)
            .Concat(rows.Where(row => row.Bag == InventoryBag.EquippedBag && (row.Flags & 1) != 0)
                .Reverse().Select(row => row.ItemId).Where(id => bag.Any(row => row.ItemId == id)));
        if (itemId is { } changed)
            candidates = (worn ? new[] { changed } : []).Concat(candidates.Where(id => id != changed));
        foreach (var candidate in candidates.Distinct())
        {
            var locations = locationGroups.Where(group => group.Contains(candidate)).ToArray();
            if (locations.Length == 0 || locations.Any(occupied.Contains)) continue;
            locationItems.Add(candidate);
            occupied.UnionWith(locations);
        }
        return rows.Select(row =>
        {
            var group = groups.FirstOrDefault(group => group.Contains(row.ItemId));
            bool? state = group is { ItemIds: not null } ? locationItems.Contains(row.ItemId)
                : group is not null ? selections[group] == row.ItemId
                : itemId == row.ItemId ? worn : null;
            return state is null ? row : row with { Flags = state.Value ? (byte)(row.Flags | 1) : (byte)(row.Flags & ~1) };
        }).ToArray();
    }
}
