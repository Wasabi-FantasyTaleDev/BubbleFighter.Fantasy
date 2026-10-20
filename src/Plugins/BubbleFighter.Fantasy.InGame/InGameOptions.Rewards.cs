using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets whether independent score-based rewards replace the original deployment formula.</summary>
    public bool ScoreBasedRewards { get; set; }

    /// <summary>Gets or sets the unscaled experience granted for completing a round.</summary>
    [Range(1, 1000000)]
    public int RewardCompletionExp { get; set; } = 18;

    /// <summary>Gets or sets the unscaled Lucci granted for completing a round.</summary>
    [Range(1, 1000000)]
    public int RewardCompletionLucci { get; set; } = 28;

    /// <summary>Gets or sets the unscaled experience contribution of each personal score point.</summary>
    [Range(0.0, 1000.0)]
    public double RewardExpPerScore { get; set; } = 0.1;

    /// <summary>Gets or sets the unscaled Lucci contribution of each personal score point.</summary>
    [Range(0.0, 1000.0)]
    public double RewardLucciPerScore { get; set; } = 0.4;

    /// <summary>Gets or sets the unscaled experience bonus for a victory.</summary>
    [Range(0, 1000000)]
    public int RewardVictoryExp { get; set; } = 1;

    /// <summary>Gets or sets the unscaled Lucci bonus for a victory.</summary>
    [Range(0, 1000000)]
    public int RewardVictoryLucci { get; set; } = 4;

    /// <summary>Gets or sets the final experience factor; 1 is normal and 100 is one hundred times normal.</summary>
    [Range(0.01, 1000.0)]
    public double RewardExpMultiplier { get; set; } = 1;

    /// <summary>Gets or sets the final Lucci factor independently of experience.</summary>
    [Range(0.01, 1000.0)]
    public double RewardLucciMultiplier { get; set; } = 1;
}
