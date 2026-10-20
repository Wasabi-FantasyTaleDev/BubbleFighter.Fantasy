using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets the cooperative mode receiving native score-unit reports; zero disables scoring.</summary>
    [Range(0, ushort.MaxValue)]
    public int PvePointModeId { get; set; }

    /// <summary>Gets or sets deployment point awards indexed by the NPC resource's serverPangScoreUnitnum.</summary>
    [MaxLength(64)]
    public int[] PvePointUnitValues { get; set; } = [];

    private IEnumerable<ValidationResult> ValidatePvePoints()
    {
        if (PvePointModeId > 0 && (PvePointUnitValues.Length is < 1 or > 64 || PvePointUnitValues.Any(value => value is < 1 or > 1000000)))
            yield return new ValidationResult("pve_point_mode_id requires one to 64 positive bounded pve_point_unit_values.");
    }
}
