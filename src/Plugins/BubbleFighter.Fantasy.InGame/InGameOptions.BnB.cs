using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets the native BnB map, bomb and pickup authority; disabled pending client acceptance.</summary>
    public bool BnB { get; set; }

    /// <summary>Gets or sets the maximum bytes read from each BnB map or specification resource.</summary>
    [Range(1, 16_777_216)]
    public int BnBMaximumFileBytes { get; set; } = 1_048_576;

    /// <summary>Gets or sets the maximum grid cells retained for one BnB map.</summary>
    [Range(1, 4096)]
    public int BnBMaximumCells { get; set; } = 1024;

    /// <summary>Gets or sets the allowed Manhattan grid distance between a request and the sender's latest movement sample.</summary>
    [Range(0, 4)]
    public int BnBMaximumPositionCellDistance { get; set; } = 1;

    /// <summary>Gets or sets the grace after a native fuse deadline before the server publishes an unreported explosion.</summary>
    [Range(0, 10_000)]
    public int BnBExplosionReportGraceMs { get; set; } = 1000;

    /// <summary>Gets or sets the supported map directories and their original TileLayer coordinate bounds.</summary>
    public BnBStageOptions[] BnBStages { get; set; } = [];

    internal IEnumerable<ValidationResult> ValidateBnB()
    {
        if (BnBStages is null || BnBStages.Length > 128 || BnBStages.Any(stage => stage is null || !stage.IsValid())
            || BnBStages.Select(stage => stage.StageDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != BnBStages.Length)
            yield return new ValidationResult("bnb_stages requires at most 128 distinct safe stage directories and finite original TileLayer bounds.");
        else if (BnB && BnBStages.Length == 0)
            yield return new ValidationResult("bnb requires an explicit bnb_stages map policy.");
        if (BnB && (!NativeMatchRelay || !NativeLifeEveryMode && !NativeLifeModeIds.Contains((int)GameModeId.BnB)))
            yield return new ValidationResult("bnb requires native_match_relay and native life admission for mode 41.");
    }
}

/// <summary>A supported BnB map and the native TileLayer bounds used before its JSON coordinate offset.</summary>
public sealed class BnBStageOptions
{
    /// <summary>Gets or sets the exact stage directory containing CustomMap.json and BnBItemLayout.json.</summary>
    public string StageDirectory { get; set; } = "";

    /// <summary>Gets or sets the original TileLayer minimum x coordinate; omitted bounds are rejected.</summary>
    public float? TileLayerMinimumX { get; set; }

    /// <summary>Gets or sets the original TileLayer maximum z coordinate; omitted bounds are rejected.</summary>
    public float? TileLayerMaximumZ { get; set; }

    internal bool IsValid() => !string.IsNullOrWhiteSpace(StageDirectory) && StageDirectory.Length <= 128
        && StageDirectory.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        && TileLayerMinimumX is { } x && float.IsFinite(x) && TileLayerMaximumZ is { } z && float.IsFinite(z);
}
