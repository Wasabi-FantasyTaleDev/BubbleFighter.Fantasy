using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Pets.Mileage;

/// <summary>Chooses whether a reset converts one affordable threshold or every affordable threshold.</summary>
public enum PetMileageConversionMode
{
    /// <summary>Converts at most one threshold per accepted reset.</summary>
    Single,
    /// <summary>Converts every affordable threshold, refusing a reset that exceeds the operation budget.</summary>
    All,
}

/// <summary>Chooses how an explicitly configured conversion treats unspent mileage.</summary>
public enum PetMileageRemainder
{
    /// <summary>Retains every point not spent on a conversion.</summary>
    Preserve,
    /// <summary>Discards the remaining points after an affordable conversion.</summary>
    Discard,
}

/// <summary>Explicit policy for one build's shared native mileage balance and reset consumables.</summary>
public sealed class PetMileageOptions : IValidatableOptions
{
    /// <summary>Gets or sets whether this row supplies native mileage configuration and transactional gains.</summary>
    public bool Enabled { get; set; }
    /// <summary>Gets or sets the positive native meter maximum, also required for manual feeding when mileage gains are disabled.</summary>
    public int? DisplayMaximum { get; set; }
    /// <summary>Gets or sets the lower bound for ordinary reward slot allocation under the account lock.</summary>
    public long SlotKeyBase { get; set; } = 1;
    /// <summary>Gets or sets each reset consumable's explicit mileage gain; zero must be chosen deliberately.</summary>
    public PetMileageGrantOptions[] Grants { get; set; } = [];
    /// <summary>Gets or sets optional threshold conversion rules; null means accumulation without conversion.</summary>
    public PetMileageConversionOptions? Conversion { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (DisplayMaximum is <= 0 || Enabled && DisplayMaximum is null)
            yield return new("Enabled pet mileage requires an explicit positive display maximum.", [nameof(DisplayMaximum)]);
        if (SlotKeyBase <= 0) yield return new("Mileage slot key base must be positive.", [nameof(SlotKeyBase)]);
        if (Grants is null || Grants.Length > InventoryLimits.MaxInventorySlotsPerBag
            || Grants.Any(grant => grant is null || grant.ConsumableItemId <= 0 || grant.Quantity is null or < 0)
            || Grants.Select(grant => grant.ConsumableItemId).Distinct().Count() != Grants.Length
            || Enabled && Grants.Length == 0)
            yield return new("Mileage requires bounded distinct consumables with explicit nonnegative quantities.", [nameof(Grants)]);
        if (Conversion is not null)
            foreach (var result in Conversion.Validate())
                yield return new(result.ErrorMessage, result.MemberNames.Select(member => $"{nameof(Conversion)}.{member}"));
    }
}

/// <summary>Assigns an explicit mileage amount to one native reset consumable.</summary>
public sealed class PetMileageGrantOptions
{
    /// <summary>Gets or sets the native inventory item used for regression or selected skill removal.</summary>
    public int ConsumableItemId { get; set; }
    /// <summary>Gets or sets the amount awarded by one accepted use; omission is invalid.</summary>
    public int? Quantity { get; set; }
}

/// <summary>Defines bounded deterministic conversion with explicit cost, repeat and remainder behavior.</summary>
public sealed class PetMileageConversionOptions : IValidatableOptions
{
    /// <summary>Gets or sets the positive number of mileage points spent per conversion.</summary>
    public int Threshold { get; set; }
    /// <summary>Gets or sets the explicitly selected number-of-conversions rule.</summary>
    public PetMileageConversionMode? Mode { get; set; }
    /// <summary>Gets or sets the explicit disposition of points left after conversion.</summary>
    public PetMileageRemainder? Remainder { get; set; }
    /// <summary>Gets or sets the finite number of conversions permitted within one reset transaction.</summary>
    public int MaximumPerOperation { get; set; }
    /// <summary>Gets or sets whether configuration must use the same value for the meter and conversion threshold.</summary>
    public bool RequireDisplayMaximumMatch { get; set; }
    /// <summary>Gets or sets the complete deterministic reward bundle granted by each conversion.</summary>
    public PetMileageRewardOptions[] Rewards { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (Threshold <= 0) yield return new("A conversion threshold must be positive.", [nameof(Threshold)]);
        if (Mode is not { } mode || !Enum.IsDefined(mode))
            yield return new("Mileage conversion mode must be explicitly selected.", [nameof(Mode)]);
        if (Remainder is not { } remainder || !Enum.IsDefined(remainder))
            yield return new("Mileage remainder policy must be explicitly selected.", [nameof(Remainder)]);
        if (MaximumPerOperation is < 1 or > InventoryLimits.MaxInventorySlotsPerBag)
            yield return new("Mileage conversion count must be between one and the inventory slot bound.", [nameof(MaximumPerOperation)]);
        if (Rewards is null || Rewards.Length is < 1 or > InventoryLimits.MaxInventorySlotsPerBag
            || Rewards.Any(reward => reward is null || reward.ItemId <= 0 || reward.Amount <= 0
                || reward.Kind is not { } kind || !Enum.IsDefined(kind))
            || Rewards.Select(reward => reward.ItemId).Distinct().Count() != Rewards.Length)
            yield return new("Mileage rewards require distinct items, explicit grant kinds and positive amounts.", [nameof(Rewards)]);
    }
}

/// <summary>Specifies one ordinary reward's amount and ownership lifetime, validated against native item metadata.</summary>
public sealed class PetMileageRewardOptions
{
    /// <summary>Gets or sets the native item granted in the selected build.</summary>
    public int ItemId { get; set; }
    /// <summary>Gets or sets count, days, permanent or life ownership explicitly.</summary>
    public InventoryGrantKind? Kind { get; set; }
    /// <summary>Gets or sets the quantity or days per conversion; permanent grants require one.</summary>
    public int Amount { get; set; }
}
