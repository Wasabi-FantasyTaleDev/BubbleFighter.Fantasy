using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>A build's selections, without ownership, quantities, lifetimes or wallet data.</summary>
/// <param name="WornItemIds">Selected owned item identities; null means legacy wear has not yet been normalized.</param>
/// <param name="ActivePresetSlot">The active native slot, from zero through four.</param>
/// <param name="PresetPages">Five materialized pages, or null before the first materialization.</param>
public sealed record InventoryLoadoutState([property: JsonRequired] int[]? WornItemIds,
    [property: JsonRequired] byte ActivePresetSlot, [property: JsonRequired] WeaponPresetPage[]? PresetPages)
{
    /// <summary>Gets selected pet ownership identities; null means legacy item selections have not been materialized.</summary>
    public ulong[]? WornPetEntitlementIds { get; init; }

    /// <summary>Gets selected ordinary Article ownership identities; null retains the legacy item selection until initialization.</summary>
    public ulong[]? WornArticleEntitlementIds { get; init; }

    /// <summary>Validates persisted state and copies every mutable collection before crossing a store boundary.</summary>
    public InventoryLoadoutState Copy()
    {
        if (ActivePresetSlot > InventoryLimits.MaxWeaponPresetSlot
            || WornItemIds is { } ids && (ids.Length > InventoryLimits.MaxInventorySlotsPerBag
                || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            || WornPetEntitlementIds is { } pets && (pets.Length > InventoryLimits.MaxInventorySlotsPerBag
                || pets.Any(id => id == 0) || pets.Distinct().Count() != pets.Length)
            || WornArticleEntitlementIds is { } articles && (articles.Length > InventoryLimits.MaxInventorySlotsPerBag
                || articles.Any(id => id == 0) || articles.Distinct().Count() != articles.Length))
            throw new InvalidDataException("Stored loadout selections exceed their native bounds or contain invalid identities.");
        return new(WornItemIds?.ToArray(), ActivePresetSlot,
            PresetPages is null ? null : WeaponPresetPages.CopyTable(PresetPages))
        { WornPetEntitlementIds = WornPetEntitlementIds?.ToArray(), WornArticleEntitlementIds = WornArticleEntitlementIds?.ToArray() };
    }
}

/// <summary>Projects shared ownership through a build selection without modifying any shared inventory row.</summary>
public static class InventoryLoadouts
{
    /// <summary>Compares complete loadout values so unchanged preparation does not write new persistence state.</summary>
    public static bool SameState(InventoryLoadoutState? left, InventoryLoadoutState right) => left is not null
        && left.ActivePresetSlot == right.ActivePresetSlot && Same(left.WornItemIds, right.WornItemIds)
        && Same(left.WornPetEntitlementIds, right.WornPetEntitlementIds)
        && Same(left.WornArticleEntitlementIds, right.WornArticleEntitlementIds) && Same(left.PresetPages, right.PresetPages);

    private static bool Same<T>(T[]? left, T[]? right) => left is null ? right is null : right is not null && left.SequenceEqual(right);

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
        var pets = state.WornPetEntitlementIds?.ToHashSet();
        var articles = state.WornArticleEntitlementIds?.ToHashSet();
        return rows.Where(row => scope.Supports(row.ItemId)).Select(row => selected is null && (pets is null || row.PetIdentity is null)
                && (articles is null || row.ArticleIdentity is null) ? row
            : row with { Flags = (row.ArticleIdentity is { } article && articles is not null ? articles.Contains(article.EntitlementId)
                : row.PetIdentity is { } identity && pets is not null
                ? pets.Contains(identity.EntitlementId) : selected?.Contains(row.ItemId) ?? (row.Flags & 1) != 0)
                ? (byte)(row.Flags | InventorySlotRecord.WornFlagBit)
                : (byte)(row.Flags & ~InventorySlotRecord.WornFlagBit) }).ToArray();
    }

    /// <summary>Captures item selections and exact pet ownership from the same completed wear operation.</summary>
    public static InventoryLoadoutState Selection(InventoryLoadoutState state, IReadOnlyList<InventorySlotRecord> rows,
        bool materializePets = false) => Selection(state, rows, materializePets, materializeArticles: false);

    /// <summary>Captures item, pet and ordinary Article selections, materializing either ownership domain when requested.</summary>
    public static InventoryLoadoutState Selection(InventoryLoadoutState state, IReadOnlyList<InventorySlotRecord> rows,
        bool materializePets, bool materializeArticles)
    {
        var selected = rows.Where(row => row.Bag == InventoryBag.CharacterBag && (row.Flags & 1) != 0).ToArray();
        return (state with
        {
            WornItemIds = selected.Select(row => row.ItemId).Distinct().ToArray(),
            WornPetEntitlementIds = !materializePets && state.WornPetEntitlementIds is null && rows.All(row => row.PetIdentity is null)
                ? null : selected.Where(row => row.PetIdentity is not null)
                    .Select(row => row.PetIdentity!.EntitlementId).Distinct().ToArray(),
            WornArticleEntitlementIds = !materializeArticles && state.WornArticleEntitlementIds is null && rows.All(row => row.ArticleIdentity is null)
                ? null : selected.Where(row => row.ArticleIdentity is not null)
                    .Select(row => row.ArticleIdentity!.EntitlementId).Distinct().ToArray(),
        }).Copy();
    }

    /// <summary>Freezes legacy selected pets after identity initialization without selecting later replacement ownership.</summary>
    public static InventoryLoadoutState MaterializePets(InventoryLoadoutState state,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutScope scope) => state.WornPetEntitlementIds is null
        ? Selection(state, Project(rows, state, scope), materializePets: true) : state.Copy();

    /// <summary>Freezes ordinary equipment ownership selections after Article allocation without selecting later replacements.</summary>
    public static InventoryLoadoutState MaterializeArticles(InventoryLoadoutState state,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutScope scope) => state.WornArticleEntitlementIds is null
        ? Selection(state, Project(rows, state, scope), materializePets: false, materializeArticles: true) : state.Copy();

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
