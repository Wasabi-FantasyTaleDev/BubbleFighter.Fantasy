using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Progress;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Presents;

/// <summary>
/// Grants configured gifts for attained levels. Durable claims prevent duplicates, and login reconciles saved progress
/// with those claims so an interrupted grant can be retried without replaying a round.
/// </summary>
/// <remarks>
/// Eligibility includes configured levels reached before the table was enabled. Removing a gift never removes its
/// claim. A failed grant retries on the next login or level rise while the corresponding reward remains configured.
/// </remarks>
internal sealed class LevelRewardService
{
    private readonly ShopOptions _options;
    private readonly IPresentStore _store;
    private readonly PresentService _presents;
    private readonly ShopLevelReward[] _rewards;
    private readonly IPlayerProgressRepository _progress;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>Creates the service for one shop row.</summary>
    /// <param name="options">The row configuration, already validated.</param>
    /// <param name="store">The gift store.</param>
    /// <param name="presents">The gift box service that announces a new gift to a live receiver.</param>
    /// <param name="progress">The durable level used to recover interrupted grants at login.</param>
    /// <param name="clock">The clock the grant time is read from.</param>
    /// <param name="logger">The row logger.</param>
    internal LevelRewardService(ShopOptions options, IPresentStore store, PresentService presents,
        IPlayerProgressRepository progress, TimeProvider clock, ILogger logger)
    {
        _options = options;
        _store = store;
        _presents = presents;
        _progress = progress;
        _clock = clock;
        _logger = logger;
        _rewards = options.LevelRewards.OrderBy(entry => entry.Level).ToArray();
    }

    /// <summary>Gets the once-only claim key of one level's reward.</summary>
    /// <param name="level">The level reached.</param>
    /// <returns>The key the store records, for example <c>level:12</c>.</returns>
    internal static string ClaimKey(int level) => "level:" + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reconciles all attained reward levels after a valid stored rise, including previously interrupted grants.</summary>
    /// <param name="raised">The level rise.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask OnLevelRaisedAsync(PlayerLevelRaised raised, CancellationToken ct)
    {
        if (raised.Protocol != _options.Protocol || raised.NewLevel <= raised.OldLevel)
            return;
        if (await GrantThroughAsync(raised.UserNum, raised.NewLevel, ct).ConfigureAwait(false))
            await _presents.AnnounceAsync(raised.UserNum, ct).ConfigureAwait(false);
    }

    /// <summary>Recovers outstanding rewards before the ordinary login notice reads the waiting count.</summary>
    internal async ValueTask LoginAsync(LoginMenuInstalled login, CancellationToken ct)
    {
        if (login.Protocol != _options.Protocol)
            return;
        try
        {
            var progress = await _progress.FindAsync(login.Login.UserNumber, ct).ConfigureAwait(false);
            if (progress is not null)
                await GrantThroughAsync(progress.UserNum, progress.Level, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Player {Player}: level reward recovery failed; the next login or level rise will retry.",
                login.Login.UserNumber);
        }
    }

    private async ValueTask<bool> GrantThroughAsync(long userNum, int attainedLevel, CancellationToken ct)
    {
        var granted = false;
        foreach (var reward in _rewards)
        {
            var level = reward.Level;
            if (level > attainedLevel)
                break;
            ct.ThrowIfCancellationRequested();
            var message = _options.LevelRewardMessage.Replace("{level}", level.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var result = await _store.GrantRewardAsync(new PresentRewardCommand(_options.ClientProfile, userNum,
                ClaimKey(level), _options.LevelRewardSender, reward.StockKeys, message, _clock.GetUtcNow().ToUnixTimeMilliseconds()), ct)
                .ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PresentRewardOutcome.Granted:
                    granted = true;
                    _logger.LogInformation("Player {Player} reached level {Level}: {Count} gift(s) put in the gift box.",
                        userNum, level, result.Presents.Count);
                    break;
                case PresentRewardOutcome.AlreadyClaimed:
                    _logger.LogDebug("Player {Player}: the level {Level} reward was granted before.", userNum, level);
                    break;
                case PresentRewardOutcome.ReceiverMissing:
                    _logger.LogWarning("Player {Player} does not exist; the level {Level} reward is not granted.", userNum, level);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown reward outcome {result.Outcome}.");
            }
        }
        return granted;
    }
}
