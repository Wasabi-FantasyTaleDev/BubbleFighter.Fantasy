using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>
    /// Gets or sets the WeaponMaster round's levels (round mode 26), first level first. The client carries no weapon
    /// table for the mode, so the server's weapon pool is configuration only: each level states the experience at which
    /// a seat reaches it (the first level 0) and, optionally, weapons of its own; each assignment draws one weapon of the
    /// seat's current level by weight, from <see cref="WeaponMasterWeapons"/> when the level lists none. The first seat to reach the last level wins and ends the round; when the clock runs out first,
    /// the seats at the highest level win. An empty list leaves the mode without weapon logic (it still runs as an
    /// ordinary timed round). YAML key <c>weapon_master_levels</c>.
    /// </summary>
    public WeaponMasterLevelOption[] WeaponMasterLevels { get; set; } = [];

    /// <summary>
    /// Gets or sets the weapon pool shared by every level that lists no weapons of its own, so a deployment whose
    /// weapons do not depend on the level states them once. YAML key <c>weapon_master_weapons</c>.
    /// </summary>
    public WeaponMasterWeaponOption[] WeaponMasterWeapons { get; set; } = [];

    /// <summary>
    /// Gets or sets the experience each accepted score event gives: <c>kill</c> (a finishing blow), <c>knockdown</c>
    /// (a bubbled opponent) and <c>rescue</c>. An event that is not listed gives none. The client states no amount, so
    /// the amounts are deployment policy with no default. YAML key <c>weapon_master_experience</c>.
    /// </summary>
    public WeaponMasterExperienceOption[] WeaponMasterExperience { get; set; } = [];

    internal IEnumerable<ValidationResult> ValidateWeaponMasterOptions() => ValidateWeaponMaster();

    private IEnumerable<ValidationResult> ValidateWeaponMaster()
    {
        if (WeaponMasterLevels is null || WeaponMasterExperience is null || WeaponMasterWeapons is null)
        {
            yield return new ValidationResult("weapon_master_levels, weapon_master_weapons and weapon_master_experience must be lists.");
            yield break;
        }
        if (WeaponMasterLevels.Length == 0)
        {
            if (WeaponMasterExperience.Length > 0 || WeaponMasterWeapons.Length > 0)
                yield return new ValidationResult("weapon_master_experience and weapon_master_weapons require weapon_master_levels.",
                    [nameof(WeaponMasterExperience)]);
            yield break;
        }
        for (var index = 0; index < WeaponMasterLevels.Length; index++)
        {
            var level = WeaponMasterLevels[index];
            var weapons = level?.Weapons is { Length: > 0 } own ? own : WeaponMasterWeapons;
            if (weapons is null || weapons.Length == 0 || weapons.Any(weapon => weapon is null || weapon.Item <= 0 || weapon.Weight <= 0))
                yield return new ValidationResult($"weapon_master_levels level {index + 1} needs weapons with a positive item id and weight, its own or weapon_master_weapons.",
                    [nameof(WeaponMasterLevels)]);
            else if (weapons.Select(weapon => weapon.Item).Distinct().Count() != weapons.Length)
                yield return new ValidationResult($"weapon_master_levels level {index + 1} lists an item twice.", [nameof(WeaponMasterLevels)]);
            else if (weapons.Sum(weapon => (long)weapon.Weight) > int.MaxValue)
                yield return new ValidationResult($"weapon_master_levels level {index + 1} weights add up beyond {int.MaxValue}.", [nameof(WeaponMasterLevels)]);
            else if (index == 0 ? level!.Experience != 0
                : level!.Experience <= WeaponMasterLevels[index - 1]!.Experience || level.Experience >= ushort.MaxValue)
                // The live score carries experience as an unsigned 16-bit value, one more than the last level's start included.
                yield return new ValidationResult("weapon_master_levels experience must start at 0 and rise with every level, below 65535.",
                    [nameof(WeaponMasterLevels)]);
        }
        if (WeaponMasterExperience.Length == 0
            || WeaponMasterExperience.Any(entry => entry is null || !Data.WeaponMasterEvents.TryParse(entry.Event, out _) || entry.Points <= 0)
            || WeaponMasterExperience.Select(entry => entry!.Event).Distinct(StringComparer.Ordinal).Count() != WeaponMasterExperience.Length)
            yield return new ValidationResult("weapon_master_experience must give positive points to distinct events of kill, knockdown and rescue.",
                [nameof(WeaponMasterExperience)]);
        if (!NativeLifeEveryMode && !NativeLifeModeIds.Contains((int)Contracts.Rooms.GameModeId.WeaponMaster))
            yield return new ValidationResult("weapon_master_levels needs the round's native life reports: set native_life_every_mode or include mode 26 in native_life_mode_ids.",
                [nameof(WeaponMasterLevels)]);
    }
}

/// <summary>One level of the WeaponMaster round: the weapons a seat of that level can be handed.</summary>
public sealed class WeaponMasterLevelOption
{
    /// <summary>Gets or sets the experience at which a seat reaches the level; the first level is 0.</summary>
    public int Experience { get; set; }

    /// <summary>Gets or sets the level's own weapon pool; empty uses the shared <c>weapon_master_weapons</c>.</summary>
    public WeaponMasterWeaponOption[] Weapons { get; set; } = [];
}

/// <summary>The experience one accepted score event gives in the WeaponMaster round.</summary>
public sealed class WeaponMasterExperienceOption
{
    /// <summary>Gets or sets the event: <c>kill</c>, <c>knockdown</c> or <c>rescue</c>.</summary>
    public string Event { get; set; } = "";

    /// <summary>Gets or sets the experience the event gives, positive.</summary>
    public int Points { get; set; }
}

/// <summary>One weapon of a WeaponMaster level and its weight in the draw.</summary>
public sealed class WeaponMasterWeaponOption
{
    /// <summary>Gets or sets the item id the client equips.</summary>
    public int Item { get; set; }

    /// <summary>Gets or sets the weight of the item in the draw; a weight of 2 is drawn twice as often as 1.</summary>
    public int Weight { get; set; }
}
