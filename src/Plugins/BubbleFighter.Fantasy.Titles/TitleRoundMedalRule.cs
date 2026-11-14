using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.Titles;

/// <summary>Resolves one catalogue medal from accepted terminal counters under explicit deployment policy.</summary>
public sealed class TitleRoundMedalRule
{
    /// <summary>Gets or sets the native medal identity credited when this rule matches.</summary>
    [Range(1, int.MaxValue)] public int MedalId { get; set; }

    /// <summary>Gets or sets the exact native game modes admitted by this rule.</summary>
    public int[] ModeIds { get; set; } = [];

    /// <summary>Gets or sets the accepted counter: Completed, Wins, Kills, Knockdowns, Rescues, Score, Deaths or Stars.</summary>
    public string Metric { get; set; } = "";

    /// <summary>Gets or sets the inclusive lower counter bound; no historical threshold is inferred.</summary>
    [Range(0, int.MaxValue)] public int Minimum { get; set; }

    /// <summary>Gets or sets an optional inclusive upper bound, allowing disjoint medal tiers.</summary>
    [Range(0, int.MaxValue)] public int? Maximum { get; set; }

    /// <summary>Gets or sets the medal count awarded once for this completed match.</summary>
    [Range(1, int.MaxValue)] public int Count { get; set; } = 1;

    /// <summary>Gets or sets the minimum number of admitted human participants eligible for this rule.</summary>
    [Range(1, int.MaxValue)] public int MinimumParticipants { get; set; } = 2;
}
