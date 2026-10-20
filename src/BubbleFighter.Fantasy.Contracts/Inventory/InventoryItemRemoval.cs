namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Plans the removal of one expired identity without erasing an active entitlement in either bag.</summary>
public static class InventoryItemRemoval
{
    /// <summary>
    /// Selects the requested identity only when all its rows have ended. The caller must hold the same
    /// player lock or transaction from this check through deletion, including purchase and renewal writes.
    /// </summary>
    /// <param name="userNum">The positive player number whose rows are inspected.</param>
    /// <param name="itemId">The positive item identity requested by the player.</param>
    /// <param name="rows">A bounded snapshot of this player's character and equipped bags.</param>
    /// <param name="now">The instant used for all expiration checks.</param>
    /// <returns>All matching expired rows, an empty array when absent, or null if any matching row is active.</returns>
    /// <exception cref="ArgumentException">The snapshot targets another player or an unsupported bag.</exception>
    public static IReadOnlyList<InventorySlotRecord>? SelectExpired(long userNum, int itemId,
        IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(rows), "The snapshot exceeds both inventory bag limits.");
        if (rows.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new ArgumentOutOfRangeException(nameof(rows), "Each inventory bag must stay within its slot limit.");
        var matches = new List<InventorySlotRecord>();
        foreach (var row in rows)
        {
            if (row.UserNum != userNum || row.Bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag))
                throw new ArgumentException("The snapshot contains another player's row or an unsupported bag.", nameof(rows));
            if (row.ItemId == itemId) matches.Add(row);
        }
        return matches.Any(row => !InventoryExpiry.IsExpired(row, now)) ? null : matches.ToArray();
    }
}
