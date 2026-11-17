using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>
    /// Gets or sets whether the plaza booths (basketball, fishing, fortune) answer when the build's dialect binds them.
    /// On by default; YAML key <c>plaza_games</c>.
    /// </summary>
    public bool PlazaGames { get; set; } = true;

    /// <summary>Gets or sets the Lucci a hit pays back, before the shot's cost. Default 100. YAML key <c>plaza_basketball_hit_reward</c>.</summary>
    [Range(0, 1000000)]
    public int PlazaBasketballHitReward { get; set; } = 100;

    /// <summary>Gets or sets the Lucci a landed fish pays. Default 200. YAML key <c>plaza_fishing_catch_reward</c>.</summary>
    [Range(0, 1000000)]
    public int PlazaFishingCatchReward { get; set; } = 200;

    /// <summary>Gets or sets the most fish a player may land in a day. Default 10. YAML key <c>plaza_fishing_daily_limit</c>.</summary>
    [Range(1, 10000)]
    public int PlazaFishingDailyLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets the hours east of UTC at which the plaza day (fish count, fortune paper) resets at midnight.
    /// Default 8. YAML key <c>plaza_games_utc_offset_hours</c>.
    /// </summary>
    [Range(-12, 14)]
    public int PlazaGamesUtcOffsetHours { get; set; } = 8;
}
