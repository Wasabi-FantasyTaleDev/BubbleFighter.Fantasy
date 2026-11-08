using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>A build's selections, without ownership, quantities, lifetimes or wallet data.</summary>
/// <param name="WornItemIds">Selected owned item identities; null means legacy wear has not yet been normalized.</param>
/// <param name="ActivePresetSlot">The active native slot, from zero through four.</param>
/// <param name="PresetPages">Five materialized pages, or null before the first materialization.</param>
public sealed record InventoryLoadoutState([property: JsonRequired] int[]? WornItemIds,
    [property: JsonRequired] byte ActivePresetSlot, [property: JsonRequired] WeaponPresetPage[]? PresetPages)
{
    /// <summary>Validates persisted state and copies every mutable collection before crossing a store boundary.</summary>
    public InventoryLoadoutState Copy()
    {
        if (ActivePresetSlot > InventoryLimits.MaxWeaponPresetSlot
            || WornItemIds is { } ids && (ids.Length > InventoryLimits.MaxInventorySlotsPerBag
                || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length))
            throw new InvalidDataException("Stored loadout selections exceed their native bounds or contain invalid identities.");
        return new(WornItemIds?.ToArray(), ActivePresetSlot,
            PresetPages is null ? null : WeaponPresetPages.CopyTable(PresetPages));
    }
}

/// <summary>Projects shared ownership through a build selection without modifying any shared inventory row.</summary>
public static class InventoryLoadouts
{
    /// <summary>Copies legacy or stored selections while removing identities absent from the current build.</summary>
    public static InventoryLoadoutState Compatible(InventoryLoadoutState state, InventoryLoadoutScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var copy = state.Copy();
        return copy with
        {
            WornItemIds = copy.WornItemIds?.Where(scope.Supports).ToArray(),
            // kr-1256 sub_808F10 consumes word 0 as the primary weapon; word 1 is the server's special slot.
            // Word 2 remains opaque and is retained rather than interpreted as an item identity.
            PresetPages = copy.PresetPages?.Select(page => page with
            {
                Unknown0 = page.Unknown0 == 0 || scope.Supports(page.Unknown0) ? page.Unknown0 : 0,
                Unknown4 = page.Unknown4 == 0 || scope.Supports(page.Unknown4) ? page.Unknown4 : 0,
            }).ToArray(),
        };
    }

    /// <summary>Returns only client-compatible rows with this build's worn bit; all other row fields are preserved.</summary>
    public static IReadOnlyList<InventorySlotRecord> Project(IReadOnlyList<InventorySlotRecord> rows,
        InventoryLoadoutState state, InventoryLoadoutScope scope)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag
            || rows.GroupBy(row => row.Bag).Any(group => group.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new InvalidDataException("Shared inventory exceeds the native bag bounds.");
        var selected = state.WornItemIds?.ToHashSet();
        return rows.Where(row => scope.Supports(row.ItemId)).Select(row => selected is null ? row
            : row with { Flags = selected.Contains(row.ItemId)
                ? (byte)(row.Flags | InventorySlotRecord.WornFlagBit)
                : (byte)(row.Flags & ~InventorySlotRecord.WornFlagBit) }).ToArray();
    }

    /// <summary>Updates one build's preset fields without overwriting its worn selection or another build's state.</summary>
    public static InventoryLoadoutState Presets(InventoryLoadoutState state, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages)
    {
        if (activeSlot > InventoryLimits.MaxWeaponPresetSlot) throw new ArgumentOutOfRangeException(nameof(activeSlot));
        var copy = pages is null ? null : WeaponPresetPages.CopyTable(pages);
        var compatible = Compatible(state, scope);
        return Compatible(compatible with
        {
            ActivePresetSlot = activeSlot ?? compatible.ActivePresetSlot,
            PresetPages = copy is null || initializePages && compatible.PresetPages is not null ? compatible.PresetPages : copy,
        }, scope);
    }
}
