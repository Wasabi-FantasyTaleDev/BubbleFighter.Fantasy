namespace BubbleFighter.Fantasy.Contracts.CashInbox;

/// <summary>
/// Profile-scoped cash inbox operations exposed when a shop row enables <c>cash_inbox</c>.
/// </summary>
public interface ICashInboxService
{
    /// <summary>Gets the protocol name served by this cash inbox instance.</summary>
    string Protocol { get; }

    /// <summary>
    /// Returns the bounded list of active, unexpired cash inbox entries for <paramref name="userNum"/> in this profile.
    /// </summary>
    /// <param name="userNum">Target player.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<CashInboxEntryRecord>> GetInboxSnapshotAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Resolves <paramref name="stockKey"/> against this profile's catalog and inserts a new active entry into the
    /// target player's cash inbox, respecting <paramref name="claimKey"/> idempotency and configured bounds.
    /// </summary>
    /// <param name="userNum">Receiving player.</param>
    /// <param name="stockKey">Catalog <c>Stock</c> or <c>StockSet</c> key.</param>
    /// <param name="note">Short text stored alongside the inbox entry.</param>
    /// <param name="claimKey">Optional once-only claim key scoped per player and build.</param>
    /// <param name="ignoreInboxLimit">Whether to allow granting past the configured per-player entry cap.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<CashInboxGrantResult> EnqueueStockAsync(
        long userNum,
        int stockKey,
        string note = "",
        string? claimKey = null,
        bool ignoreInboxLimit = false,
        CancellationToken ct = default);
}
