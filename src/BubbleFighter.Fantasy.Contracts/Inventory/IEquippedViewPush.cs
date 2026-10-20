using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Publishes a player's equipped set as the server-to-client
/// <c>gspInventoryViewUpdateView</c> (S2C 806) record. The client handler
/// <c>sub_9AF2F0</c> upserts the profile map <c>dword_18E55D8</c> through
/// <c>sub_81CCE0(map, identity, payload, 1)</c> and, when the identity equals the
/// login <c>UserNum</c>, refreshes the my-fighter view with <c>sub_5F50F0</c> -
/// so this is the write that puts a player's equipped character and weapons into
/// the map the in-room spawner and the appearance rebuilder read.
/// </summary>
public interface IEquippedViewPush
{
    /// <summary>Updates another player's visible equipment without changing the recipient's local preset index or contents.</summary>
    /// <param name="recipient">Game session observing the player.</param>
    /// <param name="userNum">Observed player's profile identity.</param>
    /// <param name="characterId">Observed player's selected character.</param>
    /// <param name="equippedIds">Committed worn items belonging to the observed player.</param>
    /// <param name="ct">Cancellation of this delivery.</param>
    ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        throw new NotSupportedException("This view provider does not support remote appearance updates.");

    /// <summary>
    /// Refreshes the supplied committed equipment snapshot after its acknowledgement and inventory update.
    /// Implementations must use this snapshot rather than resolving a different selection from storage.
    /// </summary>
    ValueTask SendSelectionAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        SendAsync(session, userNum, characterId, equippedIds, ct);

    /// <summary>Sends one S2C 806 record for a session.</summary>
    /// <param name="session">The session to send to.</param>
    /// <param name="userNum">The profile-map identity; the client compares it against the login UserNum.</param>
    /// <param name="characterId">The equipped character item id, stored as the record payload.</param>
    /// <param name="equippedIds">
    /// The equipped set written into the first <c>i32</c> vector: the character id
    /// followed by the two equipped weapon ids.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SendAsync(
        ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct);
}
