namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Retains an exact owned row and its expiry check time across equipment policy and persistence.</summary>
/// <param name="ExpectedRow">The build-projected character-bag row whose ownership authorized the selection.</param>
/// <param name="Now">The authoritative time at which the selection must still be valid.</param>
public sealed record InventorySelectionTarget(InventorySlotRecord ExpectedRow, DateTimeOffset Now)
{
    /// <summary>Rejects an ownership precondition belonging to another player, catalog or requested item.</summary>
    public void ValidateFor(long userNum, InventoryLoadoutScope scope, int? itemId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ExpectedRow);
        if (ExpectedRow.UserNum != userNum || ExpectedRow.Bag != InventoryBag.CharacterBag
            || ExpectedRow.SlotKey <= 0 || ExpectedRow.ItemId != itemId || !scope.Supports(ExpectedRow.ItemId))
            throw new ArgumentException("Equipment selection must retain its exact owned catalog row.");
    }

    /// <summary>Checks unique, unchanged, live ownership under the store lock before changing any selection.</summary>
    public bool Matches(IReadOnlyList<InventorySlotRecord> projected)
    {
        ArgumentNullException.ThrowIfNull(projected);
        var rows = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == ExpectedRow.ItemId).Take(2).ToArray();
        if (rows.Length != 1 || rows[0] != ExpectedRow || rows[0].Quantity <= 0
            || InventoryExpiry.IsExpired(rows[0], Now)) return false;
        InventoryArticles.RequireMirrors(rows[0], projected.Where(row => row.Bag == InventoryBag.EquippedBag
            && row.ItemId == ExpectedRow.ItemId).ToArray());
        return true;
    }
}
