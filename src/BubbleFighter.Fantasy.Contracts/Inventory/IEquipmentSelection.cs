namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Resolves and changes the player's exclusive equipped positions using the current build's item rules.</summary>
public interface IEquipmentSelection
{
    /// <summary>
    /// Normalizes every exclusive position of the character bag and returns the complete authoritative
    /// character bag. Ambiguous legacy selections are repaired through the same serialized store
    /// operation as an explicit change, so the repair is committed, not a view-only fixup.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the store returns no inventory.</exception>
    ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Selects or removes one owned item and returns the updated bag, or
    /// <see langword="null"/> when the item is unowned. The change runs in the
    /// inventory store's atomic, purchase-serialized equipment operation; a
    /// required position left empty falls back to its owned initial item.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="itemId">The item the change concerns; only character-bag items are selectable.</param>
    /// <param name="worn">True equips the item, false unequips it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when a required position lacks its initial item.</exception>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct);
}
