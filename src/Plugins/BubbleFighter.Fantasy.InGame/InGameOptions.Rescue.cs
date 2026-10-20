using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets the mode with scheduled native rescue missions; zero disables the policy.</summary>
    [Range(0, ushort.MaxValue)]
    public int RescueModeId { get; set; }

    /// <summary>Gets or sets rescue start times relative to the round start, ordered by native map mission index.</summary>
    [MaxLength(6)]
    public int[] RescueStartTimesMs { get; set; } = [];

    /// <summary>Gets or sets how long the human team has to finish each rescue.</summary>
    [Range(1, 600000)]
    public int RescueDurationMs { get; set; } = 25000;

    /// <summary>Gets or sets the quiet interval after a rescue succeeds or fails, before the next alarm.</summary>
    [Range(1, 600000)]
    public int RescueCooldownMs { get; set; } = 25000;

    /// <summary>Gets or sets the native map clock at which Boss spawning replaces rescues; zero leaves scheduling unrestricted.</summary>
    [Range(0, 600000)]
    public int RescueStopTimeMs { get; set; }

    /// <summary>Gets or sets presentation row IDs paired with the configured native citizen property names.</summary>
    [MaxLength(24)]
    public int[] RescueMissionIds { get; set; } = [];

    /// <summary>Gets or sets native citizen property names paired with the configured presentation row IDs.</summary>
    [MaxLength(24)]
    public string[] RescueCitizenNames { get; set; } = [];

    /// <summary>Gets or sets each supported map's hostile counts in native mission index order.</summary>
    [MaxLength(128)]
    public RescueMapDefinition[] RescueMapEnemyCounts { get; set; } = [];

    private IEnumerable<ValidationResult> ValidateRescue()
    {
        if (RescueModeId == 0) yield break;
        if (RescueStartTimesMs.Length is < 1 or > 6 || RescueStartTimesMs.Any(time => time < 0)
            || RescueStartTimesMs.Zip(RescueStartTimesMs.Skip(1)).Any(pair => pair.Second < pair.First + RescueDurationMs))
            yield return new ValidationResult("rescue_start_times_ms must contain up to six ordered, non-overlapping mission windows.");
        if (RescueMissionIds.Length is < 1 or > 24 || RescueMissionIds.Length != RescueCitizenNames.Length
            || RescueMissionIds.Any(id => id <= 0) || RescueMissionIds.Distinct().Count() != RescueMissionIds.Length
            || RescueCitizenNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 64))
            yield return new ValidationResult("rescue_mission_ids and rescue_citizen_names must contain bounded, paired native definitions.");
        if (RescueMapEnemyCounts.Length is < 1 or > 128
            || RescueMapEnemyCounts.Select(map => map.MapKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RescueMapEnemyCounts.Length || RescueMapEnemyCounts.Any(pair => string.IsNullOrWhiteSpace(pair.MapKey)
            || (pair.EnemyCounts.Length != 0 && pair.EnemyCounts.Length != RescueStartTimesMs.Length) || pair.EnemyCounts.Any(count => count is < 1 or > 32)))
            yield return new ValidationResult("rescue_map_enemy_counts must define bounded hostile counts for every scheduled map mission.");
    }
}

/// <summary>Pairs a native map key with its per-mission hostile spawn counts.</summary>
public sealed class RescueMapDefinition
{
    /// <summary>Gets or sets the resource map name for which the rescue definitions apply.</summary>
    [Required]
    public string MapKey { get; set; } = "";

    /// <summary>Gets or sets hostile counts in native mission index order; an empty array identifies a map without native rescue missions.</summary>
    [MaxLength(6)]
    public int[] EnemyCounts { get; set; } = [];
}
