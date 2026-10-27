using BubbleFighter.Fantasy.Contracts.Inventory;

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
internal sealed class EquipmentSelectionCatalogMasks(IInventoryRepository inventory, ItemCatalogMasks masks) : IEquipmentSelection
{
    /// <summary>Normalizes the worn set against the catalog positions without changing any item.</summary>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
        await inventory.UpdateWornSelectionAsync(userNum, masks.Groups, null, false, ct).ConfigureAwait(false)
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
        return await inventory.UpdateWornSelectionAsync(userNum, masks.Groups, itemId, worn, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the bag's worn seat item; the same bag the commit normalizes, read once before it.</summary>
    private async ValueTask<int> CurrentCharacterAsync(long userNum, CancellationToken ct)
    {
        var bag = await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct)
            .ConfigureAwait(false);
        return bag.LastOrDefault(row => row.ItemId is >= ItemCatalogMasks.SeatFirstItemId and <= ItemCatalogMasks.SeatLastItemId
            && (row.Flags & InventorySlotRecord.WornFlagBit) != 0)?.ItemId ?? 0;
    }
}
