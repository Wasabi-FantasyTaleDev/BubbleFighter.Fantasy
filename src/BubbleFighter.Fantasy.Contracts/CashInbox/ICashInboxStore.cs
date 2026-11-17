namespace BubbleFighter.Fantasy.Contracts.CashInbox;

/// <summary>
/// Durable persistence contract for cash inbox entries. Slot identifiers are monotonically allocated per store and
/// never reused even after pickup or expiry. Pickups commit inventory grants and inbox state transitions together in
/// a single atomic transaction.
/// </summary>
public interface ICashInboxStore
{
    /// <summary>
    /// Stores a new active cash inbox entry after enforcing idempotency (when <see cref="CashInboxGrantCommand.ClaimKey"/>
    /// is present) and the active-entry limit per player.
    /// </summary>
    /// <param name="command">Validated grant inputs.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<CashInboxGrantResult> GrantAsync(CashInboxGrantCommand command, CancellationToken ct);

    /// <summary>
    /// Lists the active, unexpired entries owned by <paramref name="userNum"/> for <paramref name="profileId"/>, ordered
    /// ascending by <see cref="CashInboxEntryRecord.CashSlotId"/> and bounded by <paramref name="maxEntries"/>.
    /// </summary>
    /// <param name="profileId">Client build identifier.</param>
    /// <param name="userNum">Owning player.</param>
    /// <param name="maxEntries">Maximum active rows returned.</param>
    /// <param name="nowUnixMilliseconds">Current UTC time for filtering expired entries.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<CashInboxEntryRecord>> ListActiveAsync(
        string profileId,
        long userNum,
        int maxEntries,
        long nowUnixMilliseconds,
        CancellationToken ct);

    /// <summary>Returns the stored entry for <paramref name="cashSlotId"/> in <paramref name="profileId"/>, or null.</summary>
    /// <param name="profileId">Client build identifier.</param>
    /// <param name="cashSlotId">Slot key to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<CashInboxEntryRecord?> FindBySlotIdAsync(string profileId, long cashSlotId, CancellationToken ct);

    /// <summary>
    /// Atomically verifies slot ownership, active state, expiry, and conditional inventory grants, then writes the
    /// granted regular-inventory items (and any bonus Lucci) while marking the inbox slot picked up.
    /// </summary>
    /// <param name="command">Validated pickup transaction plan.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<CashInboxPickupResult> PickUpAsync(CashInboxPickupCommand command, CancellationToken ct);
}
