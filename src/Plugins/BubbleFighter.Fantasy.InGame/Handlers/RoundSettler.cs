using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.InGame.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Settles one seat's round through the progress store: the earned experience is added to the progress the store
/// holds under the player's lock, and the round start's match id makes every attempt after the first a no-op.
/// Both result flows (typed and raw) share it, so every build settles the same way.
/// </summary>
internal static class RoundSettler
{
    /// <summary>Settles one player, retrying a failed attempt up to <paramref name="attempts"/> times in all.</summary>
    /// <param name="progress">The progress store.</param>
    /// <param name="levels">The level table the new level is read from.</param>
    /// <param name="matchId">The round start's identity.</param>
    /// <param name="userNum">The player.</param>
    /// <param name="expEarned">Experience the round earned, 0 or more.</param>
    /// <param name="lucciEarned">Lucci the round earned, 0 or more.</param>
    /// <param name="attempts">How many attempts in all, 1 or more.</param>
    /// <param name="logger">Logs a failed attempt before the next one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored settlement; the last failure is thrown when every attempt failed.</returns>
    internal static async ValueTask<RoundSettlementResult> SettleAsync(IPlayerProgressRepository progress, LevelTable levels,
        string matchId, long userNum, int expEarned, int lucciEarned, int attempts, ILogger logger, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expEarned);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var command = new RoundSettlementCommand(matchId, userNum, lucciEarned, stored =>
        {
            // A stored experience below its level's start (an older save) counts from that start.
            var before = Math.Max(stored.Exp, levels.Start(Math.Max(1, stored.Level)));
            var total = checked(before + expEarned);
            return new PlayerProgress(userNum, levels.LevelFor(total), total);
        });
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await progress.SettleRoundAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < attempts)
            {
                logger.LogWarning(exception, "Match {Match} player {User}: settlement attempt {Attempt} of {Attempts} failed; retrying.",
                    matchId, userNum, attempt, attempts);
            }
        }
    }
}
