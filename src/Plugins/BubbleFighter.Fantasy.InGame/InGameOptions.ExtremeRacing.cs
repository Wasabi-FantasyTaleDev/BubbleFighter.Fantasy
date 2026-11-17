namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>
    /// Gets or sets how many race rounds an ExtremeRacing match (round mode 24) plays when the build's exchange is
    /// mounted. Supported tracks have one to five stages; kr-1256 sub_67B920 reads SavePoint/StageCount with fallback five.
    /// Used when extreme_racing_map_rounds is off; YAML key <c>extreme_racing_rounds</c>.
    /// </summary>
    public int ExtremeRacingRounds { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether the selected map's native SavePoint/StageCount determines its match length instead of
    /// extreme_racing_rounds. The value is frozen before play; YAML key <c>extreme_racing_map_rounds</c>.
    /// </summary>
    public bool ExtremeRacingMapRounds { get; set; }

    /// <summary>
    /// Gets or sets the race clock of one round in milliseconds; a round whose clock runs out ends without waiting
    /// for the remaining players. YAML key <c>extreme_racing_round_ms</c>.
    /// </summary>
    public int ExtremeRacingRoundMs { get; set; } = 180_000;

    /// <summary>
    /// Gets or sets the finish countdown in milliseconds that starts when the first player reaches the round's goal;
    /// the round ends when it runs out or when every player has finished. This is server policy: the client only
    /// displays the countdown. YAML key <c>extreme_racing_finish_countdown_ms</c>.
    /// </summary>
    public int ExtremeRacingFinishCountdownMs { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the pause between race rounds in milliseconds, shown by the client's time-over clocks
    /// (kr-1256 sub_67E8B0 arms one of them for the duration minus 1000, so the value must exceed 1000).
    /// YAML key <c>extreme_racing_intermission_ms</c>.
    /// </summary>
    public int ExtremeRacingIntermissionMs { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets the points a round awards by finishing place, first place first; finishers beyond the list and
    /// players who do not finish get none. Points are server policy and feed the client's round and total records.
    /// YAML key <c>extreme_racing_place_points</c>.
    /// </summary>
    public int[] ExtremeRacingPlacePoints { get; set; } = [10, 8, 6, 5, 4, 3, 2, 1];
}
