using System.Collections.Immutable;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.Contracts.Pets;

/// <summary>Resolves a native pet modifier into ordered combat operations without exposing the pet catalog.</summary>
public interface IPetCombatEffects
{
    /// <summary>Gets the owning client build's game protocol.</summary>
    string Protocol { get; }

    /// <summary>Validates recognized pet modifiers and converts their four positional values into native float effects.</summary>
    /// <param name="attribute">An authoritative worn-item modifier.</param>
    /// <param name="effects">The immutable ordered operations, empty for unrelated or noncombat modifiers.</param>
    /// <returns>True when the modifier belongs to a native pet; false for unrelated item identities.</returns>
    bool TryResolve(RoomItemAttribute attribute, out ImmutableArray<PetCombatEffect> effects);
}

/// <summary>One native float operation produced by a pet's persisted ItemAttr payload.</summary>
/// <param name="Kind">The attribute operation and its unit.</param>
/// <param name="Amount">The converted native float stored before the operation is applied.</param>
public readonly record struct PetCombatEffect(PetCombatEffectKind Kind, float Amount);

/// <summary>Distinguishes native assignment from accumulation and critical percentages from damage ratios.</summary>
public enum PetCombatEffectKind
{
    /// <summary>Replaces the ordinary critical chance in percentage points.</summary>
    AssignCriticalChancePercent,

    /// <summary>Adds a critical-cancellation ratio without a percentage conversion.</summary>
    AddCriticalCancellationRatio,

    /// <summary>Adds an ordinary minimum damage-reduction ratio before endpoint conversion.</summary>
    AddDefenceMinimumRatio,

    /// <summary>Adds an ordinary maximum damage-reduction ratio before endpoint conversion.</summary>
    AddDefenceMaximumRatio,
}
