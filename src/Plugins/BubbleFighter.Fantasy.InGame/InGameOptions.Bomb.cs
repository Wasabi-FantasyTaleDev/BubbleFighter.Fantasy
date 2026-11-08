namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>
    /// Gets or sets whether audited bomb rooms play their native round series with one final settlement.
    /// A whole team leaving forfeits the match without adding unplayed round wins. Disabled by default;
    /// respawn remains independently controlled by <see cref="RespawnDisabledModeIds"/>.
    /// YAML key <c>bomb_round_series</c>.
    /// </summary>
    public bool BombRoundSeries { get; set; }
}
