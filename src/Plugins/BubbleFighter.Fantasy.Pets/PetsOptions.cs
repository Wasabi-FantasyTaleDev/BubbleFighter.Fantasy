using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Rewards;
using BubbleFighter.Fantasy.Pets.Mileage;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Identifies the native source that must agree with the server's pet feeding policy.</summary>
public enum PetClientConfigurationSource
{
    /// <summary>The client has no feeding cooldown configuration and the server uses zero cooldown.</summary>
    None,

    /// <summary>The dialect publishes the complete cooldown and mileage maximum through an audited category.</summary>
    DynamicConfig,

    /// <summary>The client reads its cooldown from the installed PetConfig workbook, which the server must match.</summary>
    NativePetConfig,
}

/// <summary>Native catalog inputs and explicit server policy for one client build's pet system.</summary>
public sealed class PetsOptions : IValidatableOptions
{
    internal const int MaximumFeedCooldownSeconds = 31536000;

    /// <summary>Gets or sets whether this row activates native pet persistence or operation handlers.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whether level regression and selective skill removal are enabled with complete reward policy.</summary>
    public bool SkillResetsEnabled { get; set; }

    /// <summary>Gets or sets explicit shared mileage gains and optional catalog rewards for reset operations.</summary>
    [Required] public PetMileageOptions Mileage { get; set; } = new();

    /// <summary>Gets or sets the game protocol served by this row.</summary>
    [Required] public string Protocol { get; set; } = "game";

    /// <summary>Gets or sets the shared persistence key of this client's equipment selection.</summary>
    [Required] public string BuildKey { get; set; } = "";

    /// <summary>Gets or sets the maximum sessions whose pet writes and publications may be in flight on this row.</summary>
    [Range(1, 65536)] public int MaxInFlightSessions { get; set; } = 128;

    /// <summary>Gets or sets the maximum room starts retained while pet mutations finish publication.</summary>
    [Range(1, 65536)] public int MaxHeldRooms { get; set; } = 128;

    /// <summary>Gets or sets the native item workbook containing pets, attributes and random tables.</summary>
    [Required] public string CatalogPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets how this client's native feeding configuration is supplied or verified.</summary>
    public PetClientConfigurationSource ClientConfigurationSource { get; set; }

    /// <summary>Gets or sets the native workbook path required only when the client reads PetConfig directly.</summary>
    public string? NativePetConfigPath { get; set; }

    /// <summary>Gets or sets the maximum native workbook byte size.</summary>
    [Range(1024, 134217728)] public int MaxWorkbookBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the total maximum row count across the parsed native worksheets.</summary>
    [Range(1, 65536)] public int MaxCatalogRows { get; set; } = 65536;

    /// <summary>Gets or sets the maximum column count in each parsed native worksheet.</summary>
    [Range(4, 1024)] public int MaxCatalogColumns { get; set; } = 256;

    /// <summary>Gets or sets the maximum characters in one native worksheet cell.</summary>
    [Range(1, 65536)] public int MaxCellCharacters { get; set; } = 16384;

    /// <summary>Gets or sets whether full experience awaits the client's separate level-up request.</summary>
    public bool ManualLevelUp { get; set; }

    /// <summary>Gets or sets whether a pet's native parameter supplies its maximum internal grade.</summary>
    public bool GradeCapFromPetParameter { get; set; }

    /// <summary>Gets or sets the internal grade cap for clients whose pet parameter has no such meaning.</summary>
    [Range(1, 2)] public int MaximumGrade { get; set; } = 2;

    /// <summary>Gets or sets whether all growth stages select from native attribute grade one.</summary>
    public bool SharedSkillPool { get; set; }

    /// <summary>Gets or sets whether growth may award a selector that this pet has already earned.</summary>
    public bool AllowDuplicateSkills { get; set; }

    /// <summary>Gets or sets whether unused experience carries into the next stage after automatic growth.</summary>
    public bool CarryGrowthExperience { get; set; }

    /// <summary>Gets or sets whether food experience comes from its native ShopItem parameter.</summary>
    public bool ExperienceFromFoodParameter { get; set; }

    /// <summary>Gets or sets experience granted by normal food when its parameter describes hunger instead.</summary>
    [Range(0, 1000000)] public int NormalFoodExperience { get; set; } = 5;

    /// <summary>Gets or sets experience granted by premium food when its parameter describes hunger instead.</summary>
    [Range(0, 1000000)] public int PremiumFoodExperience { get; set; } = 100;

    /// <summary>Gets or sets the hunger points restored by normal food.</summary>
    [Range(0, 1000000)] public int NormalFoodFullness { get; set; } = 50;

    /// <summary>Gets or sets the hunger points restored by premium food.</summary>
    [Range(0, 1000000)] public int PremiumFoodFullness { get; set; } = 100;

    /// <summary>Gets or sets the explicit modern normal-food cooldown; legacy omission means zero and premium food bypasses it.</summary>
    [Range(0, MaximumFeedCooldownSeconds)] public int? FeedCooldownSeconds { get; set; }

    /// <summary>Gets or sets the explicit client civil-clock UTC offset required before native feeding dates are published.</summary>
    [Range(-840, 840)] public int? ClientUtcOffsetMinutes { get; set; }

    /// <summary>Gets or sets whether normal food is refused when the native hunger meter is full.</summary>
    public bool RefuseNormalFoodWhenFull { get; set; } = true;

    /// <summary>Gets or sets the hunger points assigned when an existing uninitialized pet is first materialized.</summary>
    [Range(0, 1000000)] public int InitialFullness { get; set; }

    /// <summary>Gets or sets the maximum replanning attempts when concurrent inventory changes invalidate a mutation.</summary>
    [Range(1, 16)] public int MutationAttempts { get; set; } = 3;

    /// <summary>Gets or sets reward eligibility and native skill multiplier policy for terminal settlements.</summary>
    [Required] public PetRewardOptions Rewards { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Mileage is null) yield return new("Pet mileage policy is required.", [nameof(Mileage)]);
        else
            foreach (var result in Mileage.Validate())
                yield return new(result.ErrorMessage, result.MemberNames.Select(member => $"{nameof(Mileage)}.{member}"));
        string? error = null;
        try { InventoryLoadoutScope.ValidateKey(BuildKey); }
        catch (ArgumentException exception) { error = exception.Message; }
        if (error is not null) yield return new(error, [nameof(BuildKey)]);
        foreach (var result in ValidateClientConfiguration(Enabled)) yield return result;
        if (Enabled && ClientUtcOffsetMinutes is null)
            yield return new("Enabled pets require an explicit client_utc_offset_minutes policy.", [nameof(ClientUtcOffsetMinutes)]);
        if (Rewards is null) yield return new("Pet reward policy is required.", [nameof(Rewards)]);
        else
            foreach (var result in Rewards.Validate())
                yield return new(result.ErrorMessage, result.MemberNames.Select(member => $"{nameof(Rewards)}.{member}"));
    }

    internal IEnumerable<ValidationResult> ValidateClientConfiguration(bool requireSettings)
    {
        if (!Enum.IsDefined(ClientConfigurationSource))
            yield return new("Pet client configuration source must be None, DynamicConfig or NativePetConfig.", [nameof(ClientConfigurationSource)]);
        if (FeedCooldownSeconds is < 0 or > MaximumFeedCooldownSeconds)
            yield return new("Pet feed cooldown must be between zero and one year in seconds.", [nameof(FeedCooldownSeconds)]);
        if (ClientConfigurationSource == PetClientConfigurationSource.None)
        {
            if (requireSettings && ManualLevelUp)
                yield return new("Enabled manual-growth pets require an explicit native client configuration source.", [nameof(ClientConfigurationSource)]);
            if (FeedCooldownSeconds is not (null or 0))
                yield return new("Pets without a native cooldown source require zero or omitted feed_cooldown_seconds.", [nameof(FeedCooldownSeconds)]);
        }
        else if (!ManualLevelUp)
            yield return new("Automatic-growth clients have no audited native cooldown configuration source.", [nameof(ClientConfigurationSource)]);
        if (requireSettings && ManualLevelUp && FeedCooldownSeconds is null)
            yield return new("Enabled manual-growth pets require an explicit feed_cooldown_seconds policy.", [nameof(FeedCooldownSeconds)]);
        if (ClientConfigurationSource == PetClientConfigurationSource.NativePetConfig)
        {
            if (string.IsNullOrWhiteSpace(NativePetConfigPath))
                yield return new("NativePetConfig requires an explicit native_pet_config_path.", [nameof(NativePetConfigPath)]);
        }
        else if (NativePetConfigPath is not null)
            yield return new("native_pet_config_path is valid only with NativePetConfig.", [nameof(NativePetConfigPath)]);
        if (requireSettings && ClientConfigurationSource == PetClientConfigurationSource.DynamicConfig
            && Mileage?.DisplayMaximum is not > 0)
            yield return new("DynamicConfig requires an explicit positive mileage.display_maximum even without mileage gains.", [nameof(Mileage) + "." + nameof(PetMileageOptions.DisplayMaximum)]);
        if (Mileage is { Enabled: true } && ClientConfigurationSource != PetClientConfigurationSource.DynamicConfig)
            yield return new("Enabled pet mileage requires DynamicConfig to publish its native display maximum.", [nameof(ClientConfigurationSource)]);
    }
}
