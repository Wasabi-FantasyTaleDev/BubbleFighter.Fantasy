namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Removes one owned expired identity without enabling an automatic inventory sweep.</summary>
public interface IInventoryItemRemoval
{
    /// <summary>
    /// Removes one expired identity from both bags and restores the default character if needed.
    /// The caller orders the reply and equipment publications after this operation.
    /// </summary>
    /// <param name="userNum">The authenticated player's positive user number.</param>
    /// <param name="itemId">The positive item identity to discard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Removed rows, an empty list when absent, or null when any matching entitlement is active.
    /// Removal is serialized with renewals; character repair follows in a separate transaction.
    /// An absent character identity still checks repair so a failed prior repair can be retried.
    /// </returns>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveItemAsync(long userNum, int itemId, CancellationToken ct);
}
