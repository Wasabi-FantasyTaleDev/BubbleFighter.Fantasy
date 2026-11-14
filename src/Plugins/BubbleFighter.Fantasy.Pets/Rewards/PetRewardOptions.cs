using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Pets.Rewards;

/// <summary>Chooses how multiple earned pet reward effects combine for each currency.</summary>
public enum PetRewardStacking
{
    /// <summary>Uses the largest applicable multiplier.</summary>
    Highest,

    /// <summary>Adds each multiplier's bonus above one to the unmodified reward.</summary>
    AdditiveBonus,

    /// <summary>Multiplies all applicable factors before rounding.</summary>
    Multiplicative,
}

/// <summary>Chooses how a fractional adjusted reward becomes a whole credit.</summary>
public enum PetRewardRounding
{
    /// <summary>Discards the fractional part of a nonnegative reward.</summary>
    Floor,

    /// <summary>Rounds to the nearest whole credit, with half credits rounding upward.</summary>
    Nearest,

    /// <summary>Rounds every positive fractional part up to a whole credit.</summary>
    Ceiling,
}

/// <summary>Explicit server policy for the six reward effects named by native pet catalogs.</summary>
public sealed class PetRewardOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether this row modifies prepared round rewards.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the distinct positive native modes eligible for rewards; enabled rows require an explicit list.</summary>
    public int[] ModeIds { get; set; } = [];

    /// <summary>Gets or sets how earned effects combine independently for experience and Lucci.</summary>
    public PetRewardStacking Stacking { get; set; } = PetRewardStacking.Highest;

    /// <summary>Gets or sets how each adjusted reward component becomes a whole credit.</summary>
    public PetRewardRounding Rounding { get; set; } = PetRewardRounding.Floor;

    /// <summary>Gets or sets whether the Lucci factor also applies to accepted in-match pickups.</summary>
    public bool IncludeCollectedLucci { get; set; }

    /// <summary>Gets or sets the factor for native lucciBonus1, whose catalog description specifies double Lucci.</summary>
    public decimal LucciBonus1Multiplier { get; set; } = 2m;

    /// <summary>Gets or sets the factor for native lucciBonus2, whose catalog description specifies 1.5 times Lucci.</summary>
    public decimal LucciBonus2Multiplier { get; set; } = 1.5m;

    /// <summary>Gets or sets the factor for native lucciBonus3, whose catalog description specifies 1.3 times Lucci.</summary>
    public decimal LucciBonus3Multiplier { get; set; } = 1.3m;

    /// <summary>Gets or sets the factor for native expBonus1, whose catalog description specifies double experience.</summary>
    public decimal ExpBonus1Multiplier { get; set; } = 2m;

    /// <summary>Gets or sets the factor for native expBonus2, whose catalog description specifies 1.5 times experience.</summary>
    public decimal ExpBonus2Multiplier { get; set; } = 1.5m;

    /// <summary>Gets or sets the factor for native expBonus3, whose catalog description specifies 1.3 times experience.</summary>
    public decimal ExpBonus3Multiplier { get; set; } = 1.3m;

    /// <summary>Rejects implicit eligibility, invalid policies and multipliers outside the supported one-to-one-hundred range.</summary>
    public IEnumerable<ValidationResult> Validate()
    {
        if (ModeIds is null || ModeIds.Length > ushort.MaxValue
            || ModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || ModeIds.Distinct().Count() != ModeIds.Length || (Enabled && ModeIds.Length == 0))
            yield return new("Pet rewards require a bounded list of distinct positive native mode identifiers; enabled rewards require at least one mode.", [nameof(ModeIds)]);
        if (!Enum.IsDefined(Stacking))
            yield return new("Pet reward stacking must be Highest, AdditiveBonus or Multiplicative.", [nameof(Stacking)]);
        if (!Enum.IsDefined(Rounding))
            yield return new("Pet reward rounding must be Floor, Nearest or Ceiling.", [nameof(Rounding)]);
        (string Name, decimal Value)[] multipliers =
        [
            (nameof(LucciBonus1Multiplier), LucciBonus1Multiplier), (nameof(LucciBonus2Multiplier), LucciBonus2Multiplier),
            (nameof(LucciBonus3Multiplier), LucciBonus3Multiplier), (nameof(ExpBonus1Multiplier), ExpBonus1Multiplier),
            (nameof(ExpBonus2Multiplier), ExpBonus2Multiplier), (nameof(ExpBonus3Multiplier), ExpBonus3Multiplier),
        ];
        foreach (var multiplier in multipliers)
            if (multiplier.Value is < 1m or > 100m)
                yield return new("Pet reward multipliers must be between one and one hundred inclusive.", [multiplier.Name]);
    }
}
