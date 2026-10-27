namespace BubbleFighter.Fantasy.Contracts.Fighters;

/// <summary>
/// Provides each account's unlocked fighter-item ids and equipped selection. Implementations
/// may use memory or persistence; storage policy and catalog bounds remain with the provider.
/// </summary>
public interface IFighterRoster
{
    /// <summary>Adds <paramref name="itemId"/> to the unlocked set and equips it when none is equipped.</summary>
    /// <param name="userNum">Login player id; 0 when the session has no proven identity.</param>
    /// <param name="itemId">A 300000-range character-item id from the catalog.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the id is not a cataloged playable character.</exception>
    ValueTask UnlockAsync(long userNum, int itemId, CancellationToken ct);

    /// <summary>Marks <paramref name="itemId"/> as equipped; the stored selection is replaced only after the unlock check passes.</summary>
    /// <param name="userNum">Login player id.</param>
    /// <param name="itemId">Unlocked bag item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the id is not a cataloged playable character or the account has not unlocked it.</exception>
    ValueTask EquipAsync(long userNum, int itemId, CancellationToken ct);

    /// <summary>Returns unlocked item ids in unlock order, or empty when none were granted.</summary>
    /// <param name="userNum">Login player id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<int>> ListUnlockedAsync(long userNum, CancellationToken ct);

    /// <summary>Returns the equipped item id, or 0 when the account has no unlock yet.</summary>
    /// <param name="userNum">Login player id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<int> GetEquippedAsync(long userNum, CancellationToken ct);
}
