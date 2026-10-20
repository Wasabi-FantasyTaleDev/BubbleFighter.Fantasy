using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Players;

/// <summary>
/// The players logged in to one build's game service, found by account or by nickname. Features that name another
/// player (party invites, fighter info, later clan and couple) resolve the target here instead of scanning sessions.
/// </summary>
public interface IOnlinePlayers
{
    /// <summary>Finds the game connection of a logged-in player by account id.</summary>
    /// <param name="userNum">The account id.</param>
    /// <returns>The connection, or <see langword="null"/> when the player is not logged in.</returns>
    ISession? FindByUserNum(int userNum);

    /// <summary>Finds the game connection of a logged-in player by display nickname, compared ordinally.</summary>
    /// <param name="nickname">The nickname as the client sent it.</param>
    /// <returns>The connection, or <see langword="null"/> when no logged-in player has that nickname.</returns>
    ISession? FindByNickname(string nickname);
}
