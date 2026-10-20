namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// The additive merge behind <see cref="IInventoryRepository.AppendMissingItemsAsync"/>, shared
/// by every store so they agree on which candidates are added and in what order.
/// </summary>
public static class InventoryAppend
{
    /// <summary>
    /// Returns <paramref name="current"/> followed by each candidate whose item id appears neither
    /// in the bag nor earlier among the candidates.
    /// </summary>
    /// <param name="userNum">The player every row must belong to.</param>
    /// <param name="bag">The bag every row must belong to.</param>
    /// <param name="current">The stored bag, in stored order.</param>
    /// <param name="candidates">Rows to add when absent.</param>
    /// <returns>The merged bag; the same count as <paramref name="current"/> when nothing was missing.</returns>
    /// <exception cref="ArgumentException">Thrown when a candidate targets another player or bag.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the merged bag would exceed the bag cap.</exception>
    public static IReadOnlyList<InventorySlotRecord> Merge(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> current, IReadOnlyList<InventorySlotRecord> candidates)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidates);
        var held = current.Select(s => s.ItemId).ToHashSet();
        var merged = current.ToList();
        foreach (var candidate in candidates)
        {
            if (candidate.UserNum != userNum || candidate.Bag != bag)
                throw new ArgumentException($"append candidate targets user {candidate.UserNum} bag {candidate.Bag}, not {userNum} {bag}.", nameof(candidates));
            if (held.Add(candidate.ItemId))
                merged.Add(candidate);
        }
        if (merged.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidOperationException(
                $"Bag {bag} for user {userNum} would hold {merged.Count} slots; the cap is {InventoryLimits.MaxInventorySlotsPerBag}.");
        return merged;
    }
}
