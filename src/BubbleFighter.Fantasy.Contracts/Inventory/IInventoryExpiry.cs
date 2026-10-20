namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Removes a player's expired time-limited items and repairs what depended on them. It changes stored
/// state only; the caller republishes the bag and sends the notice, so each call site orders those
/// writes with its own packets.
/// </summary>
public interface IInventoryExpiry
{
    /// <summary>
    /// Deletes this player's expired rows from both bags, clears their worn state and, when the worn
    /// character was among them, wears the default character again. Other players' rows and unexpired
    /// rows are never touched.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The distinct item ids removed, empty when nothing had expired.</returns>
    ValueTask<IReadOnlyList<int>> SweepAsync(long userNum, CancellationToken ct);
}
