using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets whether loaded racing clients receive one shared map-state selection before countdown.</summary>
    public bool ExtremeRacingMapState { get; set; }

    /// <summary>Gets or sets server selection policy: random draws per match, or seeded draws using the explicit seed.</summary>
    [RegularExpression("^(random|seeded)$")]
    public string ExtremeRacingMapStateSelection { get; set; } = "random";

    /// <summary>Gets or sets the seed for reproducible server selections in seeded mode; random mode requires no seed.</summary>
    [Range(0, int.MaxValue)]
    public int? ExtremeRacingMapStateSeed { get; set; }

    /// <summary>Gets or sets the maximum native vanish groups accepted and sent for one selected map.</summary>
    [Range(1, 1024)]
    public int ExtremeRacingMaximumVanishGroups { get; set; } = 32;

    /// <summary>Gets or sets the maximum route definitions accepted for one native vanish group.</summary>
    [Range(1, 4096)]
    public int ExtremeRacingMaximumVanishRoutes { get; set; } = 128;

    /// <summary>Gets or sets the maximum tile helpers and route entries accepted for one vanish group.</summary>
    [Range(1, 65536)]
    public int ExtremeRacingMaximumVanishTiles { get; set; } = 1024;

    internal IEnumerable<ValidationResult> ValidateExtremeRacingMapState()
    {
        if (ExtremeRacingMapStateSelection is not ("random" or "seeded")
            || (ExtremeRacingMapStateSelection == "seeded") != ExtremeRacingMapStateSeed.HasValue
            || ExtremeRacingMapStateSeed < 0)
            yield return new ValidationResult("extreme_racing_map_state_selection requires random without a seed or seeded with an explicit non-negative extreme_racing_map_state_seed.");
    }
}
