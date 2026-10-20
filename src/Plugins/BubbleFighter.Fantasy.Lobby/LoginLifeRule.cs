using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// The login half of the life rule: before the bag is read, every life-counted item of the player loses one
/// life per whole UTC day since the last login, and today becomes the stored login day. Items left at zero
/// are removed by the expiry sweep that follows (<see cref="InventoryExpiry"/>).
/// </summary>
/// <param name="inventory">The store that applies the rule atomically per player.</param>
/// <param name="clock">The time source; the UTC date of its instant is "today".</param>
public sealed class LoginLifeRule(IInventoryRepository inventory, TimeProvider clock)
{
    /// <summary>Charges the player's life items for the days since the last login.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The whole days charged; 0 on a first login or a second login the same day.</returns>
    public ValueTask<int> ApplyAsync(long userNum, CancellationToken ct) =>
        inventory.AdvanceLifeAsync(userNum, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct);
}
