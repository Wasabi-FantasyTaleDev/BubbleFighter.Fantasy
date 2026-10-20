using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using System.Collections.Concurrent;

namespace BubbleFighter.Fantasy.Fighters.Data;

/// <summary>
/// A roster whose unlocked set is the player's character bag
/// (<see cref="IInventoryRepository"/>, S2C 802) and whose equipped id is the
/// stored fighter slot (<see cref="IFighterRepository"/>). Buying a character
/// item unlocks it because the bag is the same store the shop writes to, and
/// the equipped id survives a restart because it lives in the fighters table.
/// A player with no stored fighter slot is seeded with the catalog default on
/// first read, mirroring the lobby's starter-inventory seeding.
/// </summary>
public sealed class StoreBackedFighterRoster(
    IInventoryRepository inventory,
    IFighterRepository fighters,
    FighterCatalog catalog,
    IEquipmentSelection? selection = null) : IFighterRoster
{
    private readonly ConcurrentDictionary<long, byte> _seeded = new();

    /// <inheritdoc />
    public async ValueTask UnlockAsync(long userNum, int itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (itemId == 0)
            throw new InvalidOperationException("Cannot unlock character-item id 0.");
        var row = catalog.Find(itemId)
            ?? throw new InvalidOperationException($"Item {itemId} is not a playable 300000-range character.");

        // The bag is the unlocked set: add the item to the character bag when it
        // is not there yet (one store operation, so a concurrent purchase is kept),
        // then make sure an equipped slot exists.
        await inventory.AppendMissingItemsAsync(userNum, InventoryBag.CharacterBag,
            [new InventorySlotRecord(userNum, InventoryBag.CharacterBag, NewSlotKey(), itemId, 0, 0)], ct)
            .ConfigureAwait(false);

        var stored = await FindStoredAsync(userNum, ct).ConfigureAwait(false);
        if (stored is null)
        {
            await fighters.UpsertAsync(
                new FighterRecord(userNum, 0, row.FieldId, 1, itemId, string.Empty), ct).ConfigureAwait(false);
            _seeded[userNum] = 1;
            return;
        }

        if (stored.EquippedItemId is < 300000 or > 301999)
        {
            await fighters.UpsertAsync(
                stored with { EquippedItemId = itemId }, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask EquipAsync(long userNum, int itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = catalog.Find(itemId)
            ?? throw new InvalidOperationException($"Item {itemId} is not a playable 300000-range character.");
        var unlocked = await ListUnlockedAsync(userNum, ct).ConfigureAwait(false);
        if (!unlocked.Contains(itemId))
            throw new InvalidOperationException($"User {userNum} has not unlocked item {itemId}.");
        var stored = await FindStoredAsync(userNum, ct).ConfigureAwait(false);
        if (stored is null)
        {
            await fighters.UpsertAsync(
                new FighterRecord(userNum, 0, row.FieldId, 1, itemId, string.Empty), ct).ConfigureAwait(false);
            return;
        }

        await fighters.UpsertAsync(
            stored with { CharacterType = row.FieldId, EquippedItemId = itemId }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<int>> ListUnlockedAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var slots = await inventory.ListSlotsAsync(
            userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        var order = new List<int>(slots.Count);
        foreach (var slot in slots)
        {
            var id = slot.ItemId;
            if (id is >= 300000 and <= 301999 && !order.Contains(id))
                order.Add(id);
        }

        return order;
    }

    /// <inheritdoc />
    public async ValueTask<int> GetEquippedAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (selection is not null)
        {
            var bag = await selection.ReadAsync(userNum, ct).ConfigureAwait(false);
            var selected = bag.LastOrDefault(row => (row.Flags & 1) != 0 && catalog.Find(row.ItemId) is not null);
            if (selected is not null) return selected.ItemId;
        }
        var stored = await FindStoredAsync(userNum, ct).ConfigureAwait(false);
        if (stored is not null && stored.EquippedItemId is >= 300000 and <= 301999)
            return stored.EquippedItemId;

        // No stored slot: seed with the catalog default the first time so a
        // fresh account still resolves a renderable character id.
        var unlocked = await ListUnlockedAsync(userNum, ct).ConfigureAwait(false);
        var fallback = unlocked.Count > 0 ? unlocked[0] : catalog.Default.ItemId;
        if (stored is null && _seeded.TryAdd(userNum, 1))
        {
            await fighters.UpsertAsync(
                new FighterRecord(userNum, 0, catalog.Default.FieldId, 1, fallback, string.Empty), ct)
                .ConfigureAwait(false);
        }

        return fallback;
    }

    private async ValueTask<FighterRecord?> FindStoredAsync(long userNum, CancellationToken ct)
    {
        var rows = await fighters.ListByUserNumAsync(userNum, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.Slot == 0)
                return row;
        }

        return null;
    }

    private static long NewSlotKey() => DateTime.UtcNow.Ticks ^ Environment.CurrentManagedThreadId;
}
