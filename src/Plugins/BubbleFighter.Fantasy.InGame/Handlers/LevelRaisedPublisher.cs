using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Progress;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>
/// Announces a stored level rise to whoever grants something for it. The rise is already committed when this runs, so a
/// subscriber that fails is logged and must not hold up the result screen or undo the recorded experience.
/// </summary>
internal static class LevelRaisedPublisher
{
    /// <summary>Publishes <see cref="PlayerLevelRaised"/> when the level went up.</summary>
    /// <param name="events">The bus, or null when the deployment announces nothing.</param>
    /// <param name="logger">The row logger.</param>
    /// <param name="protocol">The game protocol of the round.</param>
    /// <param name="userNum">The player.</param>
    /// <param name="oldLevel">The level stored before the recording.</param>
    /// <param name="newLevel">The level stored after it.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async ValueTask PublishAsync(IEventBus? events, ILogger logger, string protocol, long userNum,
        int oldLevel, int newLevel, CancellationToken ct)
    {
        if (events is null || newLevel <= oldLevel)
            return;
        try
        {
            await events.SerialAsync(new PlayerLevelRaised(protocol, userNum, oldLevel, newLevel), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Player {Player}: a level {Old} to {New} subscriber failed after the level was stored.",
                userNum, oldLevel, newLevel);
        }
    }
}
