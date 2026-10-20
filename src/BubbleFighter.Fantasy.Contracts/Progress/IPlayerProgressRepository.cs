namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>Reads a player's level and experience and records what a round earned.</summary>
public interface IPlayerProgressRepository
{
    /// <summary>Returns the player's progress, or <see langword="null"/> for an unknown player.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Settles one round for one player in one transaction: under the player's lock it reads the stored progress,
    /// stores what <see cref="RoundSettlementCommand.Advance"/> computes from it, adds the Lucci to the wallet and
    /// records a receipt for the match and player. A settlement whose receipt exists changes nothing and returns the
    /// receipt, so a retry after an unacknowledged commit cannot credit twice. An unknown player, or one without a
    /// wallet row (created at login), fails and stores nothing.
    /// </summary>
    /// <param name="command">The match, the player, the Lucci and the progress rule.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The progress before and after, the credited Lucci and the balance.</returns>
    ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct);
}
