namespace BubbleFighter.Fantasy.Contracts.Fighters;

/// <summary>
/// Optional service supplying a seat's equipped character identity from server-side state.
/// The room row uses this identity when constructing profile-map entries and falls back
/// to the seat's login character when no resolver is published or no valid item is returned.
///
/// </summary>
public interface ISeatCharacterResolver
{
    /// <summary>
    /// Returns the equipped character id for one player, or 0 when the player
    /// has none. Ids outside 300000..301999 are ignored by the room push.
    ///
    /// </summary>
    /// <param name="userNum">The seat's identity number.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<int> GetEquippedAsync(int userNum, CancellationToken ct);
}
