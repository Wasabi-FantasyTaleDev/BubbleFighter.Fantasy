using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets the native room modes allowed to retain computer-player seats; empty disables additions.</summary>
    public int[] AiModeIds { get; set; } = [];

    /// <summary>Gets or sets explicit server loadouts indexed by the client's difficulty and preset selectors.</summary>
    public RoomAiPresetOptions[] AiPresets { get; set; } = [];

    private IEnumerable<ValidationResult> ValidateAi()
    {
        if (AiModeIds is null || AiPresets is null)
        {
            yield return new ValidationResult("ai_mode_ids and ai_presets must be arrays.");
            yield break;
        }
        if (AiModeIds.Any(mode => mode is <= 0 or > ushort.MaxValue)
            || AiModeIds.Distinct().Count() != AiModeIds.Length)
            yield return new ValidationResult("ai_mode_ids must contain distinct positive native mode identifiers.");
        if ((AiModeIds.Length > 0) != (AiPresets.Length > 0))
            yield return new ValidationResult("ai_mode_ids and ai_presets must be configured together.");
        if (AiPresets.Any(preset => preset is null || preset.Difficulty is < 0 or > 4
                || preset.Preset < 0 || string.IsNullOrWhiteSpace(preset.Nickname)
                || preset.NicknameKey is null || (preset.NicknameKey.Length > 0 && string.IsNullOrWhiteSpace(preset.NicknameKey)) || preset.Level < 1
                || preset.CharacterItemId is < 300001 or > 301999
                || preset.EquippedItemIds is null || preset.EquippedItemIds.Any(id => id <= 0)
                || preset.EquippedItemIds.Distinct().Count() != preset.EquippedItemIds.Length
                || preset.ItemAttributes is null || preset.ItemAttributes.Any(attribute => attribute is null
                    || attribute.ItemId <= 0 || attribute.Values is null || attribute.Unknown32 is null
                    || (attribute.ItemId != preset.CharacterItemId && !preset.EquippedItemIds.Contains(attribute.ItemId))))
            || AiPresets.Where(preset => preset is not null).Select(preset => (preset.Difficulty, preset.Preset)).Distinct().Count() != AiPresets.Length)
            yield return new ValidationResult("ai_presets requires unique native difficulty/preset pairs, a nickname, an empty or nonblank nickname_key, a positive level, a character and distinct positive equipment items.");
        if (AiPresets.Any(preset => preset is not null && preset.ItemAttributes is not null
            && preset.EquippedItemIds is not null
            && 44L + preset.EquippedItemIds.LongLength * 4
                + preset.ItemAttributes.Where(attribute => attribute is not null && attribute.Values is not null
                    && attribute.Unknown32 is not null)
                    .Sum(attribute => 20L + attribute.Values.LongLength * 4 + attribute.Unknown32.Length * 2L) > ushort.MaxValue))
            yield return new ValidationResult("ai_presets appearance profiles must fit the native packet body limit.");
    }
}

/// <summary>An explicit room bot loadout; the native preset selector alone carries no equipment identifiers.</summary>
public sealed class RoomAiPresetOptions
{
    /// <summary>Gets or sets the build's native difficulty selector; identical tier names can use different numbers.</summary>
    public int Difficulty { get; set; }

    /// <summary>Gets or sets the preset index within this difficulty's native popup.</summary>
    public int Preset { get; set; }

    /// <summary>Gets or sets the bot name displayed in the room roster.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>Gets or sets a localized preset name key; empty displays the literal nickname for every recipient.</summary>
    public string NicknameKey { get; set; } = "";

    /// <summary>Gets or sets the roster level icon independently of the native AI difficulty selector.</summary>
    public int Level { get; set; } = 1;

    /// <summary>Gets or sets the character item resolved from this build's catalog.</summary>
    public int CharacterItemId { get; set; }

    /// <summary>Gets or sets the worn item identifiers supplied by this preset, independently of the host's inventory.</summary>
    public int[] EquippedItemIds { get; set; } = [];

    /// <summary>Gets or sets the native material color selector for this preset.</summary>
    public ushort ColorIndex { get; set; }

    /// <summary>Gets or sets native worn-item modifiers, including material selections and equipment upgrades.</summary>
    public RoomAiItemAttributeOptions[] ItemAttributes { get; set; } = [];
}

/// <summary>Configures one preset item's native modifier while preserving the uninterpreted payload words.</summary>
public sealed class RoomAiItemAttributeOptions
{
    /// <summary>Gets or sets the worn item carrying this modifier.</summary>
    public int ItemId { get; set; }

    /// <summary>Gets or sets the native modifier selector.</summary>
    public uint Kind { get; set; }

    /// <summary>Gets or sets the modifier words without converting signed or floating-point bit patterns.</summary>
    public uint[] Values { get; set; } = [];

    /// <summary>Gets or sets the trailing modifier word whose purpose remains kind-dependent.</summary>
    public uint Unknown28 { get; set; }

    /// <summary>Gets or sets the trailing modifier text without assigning it an unproven meaning.</summary>
    public string Unknown32 { get; set; } = "";
}
