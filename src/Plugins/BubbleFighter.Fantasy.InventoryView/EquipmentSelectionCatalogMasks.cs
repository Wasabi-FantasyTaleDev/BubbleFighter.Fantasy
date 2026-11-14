using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// Applies the catalog's own location columns as the equipment conflict rule, replacing the native item-id ranges:
/// two articles conflict when their slot name sets or part name sets intersect, positions <see cref="ItemCatalogMasks"/>
/// derives from the workbook's four location columns (reader kr-1256 <c>sub_A17CF0</c>). The character seat stays a
/// one-choice position, and a wear whose <c>allowChr</c>/<c>denyChr</c> excludes the worn character is refused before
/// the atomic commit, mirroring the one local mask test the client itself runs (kr-1256 <c>sub_5F59D0</c>, the
/// "%s는 %s 전용 아이텸입니다." notice); the client's wear send (kr-1256 <c>sub_805950</c>, kr-812 <c>sub_977E70</c>)
/// never decides conflicts, so nothing client-side fights this rule. This mode is default-off: unlike the id-range
/// selection, a weapon position emptied by an unequip does not return to its initial item, because the location pass
/// of <see cref="InventoryWearSelection.Apply"/> keeps whichever owned articles still occupy their positions.
/// </summary>
/// <param name="inventory">Persistence for the worn state; supplies the bag the current character is read from.</param>
/// <param name="masks">The parsed catalog positions and exclusivity rules.</param>
/// <param name="contributions">Protocol-owned optional positions committed with the catalog selections.</param>
/// <param name="pets">Resolves pet ownership inside the build's selection transaction.</param>
/// <param name="articles">Commits exact ordinary Article selections.</param>
/// <param name="scope">The canonical catalog and loadout used for Article selection.</param>
/// <param name="articleResolver">Supplies native equipment metadata initialization for the selection transaction.</param>
/// <param name="loadouts">Commits ordinary selections with metadata even when pets are disabled.</param>
internal sealed class EquipmentSelectionCatalogMasks(IInventoryRepository inventory, ItemCatalogMasks masks,
    EquipmentWearGroups contributions, IPetEntitlements? pets = null,
    IItemEnhancementStore? articles = null, InventoryLoadoutScope? scope = null, IEquipmentArticleResolver? articleResolver = null, IInventoryLoadoutStore? loadouts = null) : IEquipmentSelection
{
    /// <summary>Normalizes the worn set against the catalog positions without changing any item.</summary>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
        await inventory.UpdateWornSelectionAsync(userNum, await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false),
            null, false, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Equipment normalization returned no inventory.");

    /// <summary>Refuses an exclusivity violation before persistence, then commits through the shared atomic selection.</summary>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        if (worn)
        {
            var character = await CurrentCharacterAsync(userNum, ct).ConfigureAwait(false);
            if (!masks.AllowsCharacter(itemId, character))
                return null;
        }
        return await inventory.UpdateWornSelectionAsync(userNum, await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false),
            itemId, worn, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the bag's worn seat item; the same bag the commit normalizes, read once before it.</summary>
    private async ValueTask<int> CurrentCharacterAsync(long userNum, CancellationToken ct)
    {
        var bag = await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct)
            .ConfigureAwait(false);
        return bag.LastOrDefault(row => row.ItemId is >= ItemCatalogMasks.SeatFirstItemId and <= ItemCatalogMasks.SeatLastItemId
            && (row.Flags & InventorySlotRecord.WornFlagBit) != 0)?.ItemId ?? 0;
    }

    /// <summary>Checks the worn character's catalog restrictions before committing the resolved pet Article against the catalog's equipment groups.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyPetAsync(long userNum, PetEquipmentTarget target, bool worn, CancellationToken ct)
    {
        if (pets is null) throw new InvalidOperationException("Article selection requires a pet entitlement provider.");
        if (worn && !masks.AllowsCharacter(target.ItemId, await CurrentCharacterAsync(userNum, ct).ConfigureAwait(false)))
            return null;
        return await pets.SelectAsync(userNum, target, await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false),
            worn, ct, articleResolver?.CreateInitialization(userNum, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    /// <summary>Checks the current character mask before committing an exact owned Article with catalog conflicts.</summary>
    public async ValueTask<InventorySnapshot?> ApplyArticleAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        if (articles is null || scope is null || articleResolver is null) throw new InvalidOperationException("Article selection requires its build-scoped store and native metadata catalog.");
        if (worn && !masks.AllowsCharacter(expectedTarget.ItemId, await CurrentCharacterAsync(userNum, ct).ConfigureAwait(false))) return null;
        var now = DateTimeOffset.UtcNow;
        return await articles.SelectArticleAsync(new(userNum, scope, expectedTarget,
            await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false), worn, now)
            { ArticleInitialization = articleResolver.CreateInitialization(userNum, now), PetInitialization = pets?.CreateInitialization(userNum, now) }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Checks character compatibility and retains exact ordinary ownership through the catalog's atomic selection.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyOwnedSnapshotAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (loadouts is null || scope is null)
            throw new InvalidOperationException("Owned selection requires the build-scoped inventory store.");
        if (worn && !masks.AllowsCharacter(expectedTarget.ItemId, await CurrentCharacterAsync(userNum, ct).ConfigureAwait(false))) return null;
        var groups = await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return await loadouts.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, expectedTarget.ItemId, worn, ct,
            pets?.CreateInitialization(userNum, now), articleResolver?.CreateInitialization(userNum, now), new(expectedTarget, now))
            .ConfigureAwait(false);
    }

    /// <summary>Checks character compatibility and commits an item selection with pet metadata under the catalog's equipment groups.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplySnapshotAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        if (worn && !masks.AllowsCharacter(itemId, await CurrentCharacterAsync(userNum, ct).ConfigureAwait(false))) return null;
        var initialization = articleResolver?.CreateInitialization(userNum, DateTimeOffset.UtcNow);
        var groups = await contributions.ResolveAsync(masks.Groups, ct).ConfigureAwait(false);
        if (pets is not null) return await pets.SelectItemAsync(userNum, itemId, groups, worn, ct, initialization).ConfigureAwait(false);
        if (loadouts is null || scope is null)
            throw new InvalidOperationException("Complete metadata selection requires a build-scoped initialization provider.");
        return await loadouts.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct,
            initialization: null, articleInitialization: initialization).ConfigureAwait(false);
    }
}
