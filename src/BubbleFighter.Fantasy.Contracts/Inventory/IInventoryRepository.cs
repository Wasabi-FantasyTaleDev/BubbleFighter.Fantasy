namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Loads and replaces a player's bag (S2C 802 and S2C 803 container 1). The server
/// sends S2C 803 containers 2 and 3 and S2C 810 empty, so nothing is stored for them.
/// Empty snapshots are legal.
/// </summary>
public interface IInventoryRepository
{
    /// <summary>
    /// Atomically removes one item's expired rows from both bags, serialized with purchases and renewals.
    /// Every matching row must have ended at <paramref name="now"/>; an active or unspecified row in either
    /// bag rejects the whole operation. Other identities and other players are never changed.
    /// </summary>
    /// <param name="userNum">The authenticated player's positive user number.</param>
    /// <param name="itemId">The positive item identity requested by the client.</param>
    /// <param name="now">The instant at which stored lifetimes are checked inside the transaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Removed rows in stored order, an empty list when already absent, or null when any matching entitlement
    /// is still active. An empty result performs no mutation and makes repeated requests harmless.
    /// </returns>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId,
        DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("This inventory store does not implement atomic expired-item removal.");

    /// <summary>Atomically consumes one owned, unexpired item, synchronizes both bag views, and removes exhausted rows. Returns the remaining count or null when unavailable.</summary>
    ValueTask<int?> ConsumeItemAsync(long userNum, int itemId, CancellationToken ct) =>
        throw new NotSupportedException("This inventory store does not implement atomic consumable use.");
    /// <summary>
    /// Atomically deletes every row of both bags whose deadline is at or before <paramref name="now"/>
    /// (<see cref="InventoryExpiry.IsExpired"/>), under the same per-player serialization as purchases.
    /// Rows without a deadline and other players' rows are never touched.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="now">The instant the deadlines are compared with.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deleted rows of both bags, in stored order.</returns>
    ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long userNum, DateTimeOffset now, CancellationToken ct) =>
        throw new NotSupportedException("This inventory store does not implement atomic expiry removal.");

    /// <summary>
    /// Applies the daily life rule for one login, atomically and under the same per-player serialization as
    /// purchases: it lowers the quantity of every <see cref="InventoryLifetimeKind.Life"/> row of both bags by
    /// the whole days since the stored last-login day (<see cref="InventoryLife.DaysMissed"/>, floor at zero)
    /// and stores <paramref name="today"/>. Rows of other kinds and other players' rows are never touched.
    /// Rows left at zero are removed by <see cref="RemoveExpiredAsync"/>, not here.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="today">The current UTC day.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The whole days charged; 0 on the first login ever, on a repeat login the same day and when the clock went back.</returns>
    ValueTask<int> AdvanceLifeAsync(long userNum, DateOnly today, CancellationToken ct) =>
        throw new NotSupportedException("This inventory store does not implement the daily life rule.");

    /// <summary>
    /// Atomically repairs exclusive equipment positions, optionally changes an owned item, and synchronizes
    /// both bag representations. Returns the complete character bag or null for an unowned requested item.
    /// Implementations must serialize with purchases and inventory appends; a partial fallback is unsafe.
    /// </summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
        throw new NotSupportedException("This inventory store does not implement atomic equipment selection.");

    /// <summary>Returns bag slots for one player and bag, bounded by <paramref name="maxCount"/>.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="bag">802 character bag or 803 container 1.</param>
    /// <param name="maxCount">Requested cap; must be 1.. <see cref="InventoryLimits.MaxInventorySlotsPerBag"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
        long userNum, InventoryBag bag, int maxCount, CancellationToken ct);

    /// <summary>Replaces every bag slot for one player and bag. An empty list stores an empty bag.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="bag">802 character bag or 803 container 1.</param>
    /// <param name="slots">Rows to store; count must not exceed the bag cap.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ReplaceSlotsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct);

    /// <summary>
    /// Appends, in order, every candidate whose item id the bag does not already hold, and
    /// returns the whole bag afterwards. Existing rows are never changed or removed. Callers
    /// that only add items use this instead of a read followed by
    /// <see cref="ReplaceSlotsAsync"/>, which would erase a purchase committed in between.
    /// Persistent stores override it with one operation serialized against purchases for the
    /// same player; this default is a read-then-replace meant only for single-threaded test stores.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="bag">802 character bag or 803 container 1.</param>
    /// <param name="candidates">Rows to add when their item id is absent; all must target this player and bag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the additions would exceed the bag cap.</exception>
    async ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
    {
        var current = await ListSlotsAsync(userNum, bag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        var merged = InventoryAppend.Merge(userNum, bag, current, candidates);
        if (merged.Count != current.Count)
            await ReplaceSlotsAsync(userNum, bag, merged, ct).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    /// Sets or clears the worn bit (<see cref="InventorySlotRecord.WornFlagBit"/>) of the
    /// character-bag rows carrying one item id. The C2S 804 / 805 body is the item id and
    /// the client's bag map is keyed by that id, so every stored row with the id flips and
    /// the row the client's map shows after a full re-parse (the highest ordinal) comes
    /// back for the S2C 803 container-1 re-push.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="itemId">Item id (<see cref="InventorySlotRecord.ItemId"/>) of the slot to restate.</param>
    /// <param name="worn">True sets the worn bit, false clears it; other flag bits are preserved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The updated row with the applied flag, or <see langword="null"/> when the
    /// character bag holds no row with that item id (nothing is mutated then).
    /// </returns>
    ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct);
}
