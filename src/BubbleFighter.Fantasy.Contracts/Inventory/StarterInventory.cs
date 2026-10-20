namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// The initial inventory a new player owns when no stored rows exist: one character and the
/// initial weapons, with one character and one weapon per position worn. This static facade
/// exposes the default kr-1256 set; profiles whose client knows fewer ids configure a
/// per-build <see cref="StarterKit"/> on the lobby row instead.
/// </summary>
public static class StarterInventory
{
    /// <summary>The default (Dao) character item id.</summary>
    public const int DaoCharacterItemId = StarterKit.DaoCharacterItemId;

    /// <summary>Gets the ids worn by default, the character first.</summary>
    public static IReadOnlyList<int> WornItemIds => StarterKit.Default.WornItemIds;

    /// <summary>Builds the starter rows of one bag; only worn items have flag bit 0 set.</summary>
    /// <param name="userNum">The player the rows belong to.</param>
    /// <param name="bag">The character bag or the equipped bag.</param>
    /// <param name="now">The instant the limited grants start counting from.</param>
    /// <returns>The rows, in the recorded key order.</returns>
    public static IReadOnlyList<InventorySlotRecord> Slots(long userNum, InventoryBag bag, DateTimeOffset now) =>
        StarterKit.Default.Slots(userNum, bag, now);
}
