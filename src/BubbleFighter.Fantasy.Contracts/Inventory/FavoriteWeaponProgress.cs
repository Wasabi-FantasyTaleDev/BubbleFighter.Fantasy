using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Reads an account's Favorite weapon standing for rules that depend on it, such as the Favorite titles.</summary>
public interface IFavoriteWeaponProgress
{
    /// <summary>Gets the game protocol whose catalogue and Favorite sheet this reader answers for.</summary>
    string Protocol { get; }

    /// <summary>
    /// Counts the account's weapon Articles whose Favorite grade is Master. The grade is derived from the stored points with the
    /// native Upgrade thresholds, never stored, so a changed sheet changes the answer.
    /// </summary>
    /// <param name="userNum">The account whose committed inventory is read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The number of weapon Articles at Master.</returns>
    ValueTask<int> CountMasterWeaponsAsync(long userNum, CancellationToken ct);
}

/// <summary>
/// Raised after a Favorite award is committed and its inventory has been published, so rules that depend on the standing
/// can run again. Handlers re-read committed state and never trust the event for values.
/// </summary>
/// <param name="Protocol">The game protocol whose Favorite catalogue awarded the points.</param>
/// <param name="Session">The connection of the account that earned the points.</param>
public sealed record FavoriteProgressCommitted(string Protocol, ISession Session) : IEvent;