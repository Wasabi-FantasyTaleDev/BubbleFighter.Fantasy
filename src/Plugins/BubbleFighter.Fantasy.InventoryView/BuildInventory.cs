using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Publishes shared entitlements with a build-specific equipment projection and preset store.</summary>
internal sealed class BuildInventory(IInventoryLoadoutStore store, InventoryLoadoutScope scope,
    IEventBus? events = null) : IInventoryRepository, IPlayerInventoryState, IInventoryBuildScope
{
    /// <summary>Gets the canonical build loadout scope used to select this inventory's persisted bags.</summary>
    public InventoryLoadoutScope Scope => scope;

    /// <summary>Reads the requested bounded bag from prepared metadata, or from the canonical build loadout when no preparation provider responds.</summary>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
    {
        if (maxCount is < 1 or > InventoryLimits.MaxInventorySlotsPerBag) throw new ArgumentOutOfRangeException(nameof(maxCount));
        if (bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag)) throw new ArgumentOutOfRangeException(nameof(bag));
        var snapshot = await PrepareAsync(userNum, ct).ConfigureAwait(false);
        if (snapshot is null) return await store.ListLoadoutSlotsAsync(userNum, scope, bag, maxCount, ct).ConfigureAwait(false);
        return (bag == InventoryBag.CharacterBag ? snapshot.CharacterBag : snapshot.EquippedBag).Take(maxCount).ToArray();
    }

    /// <summary>Prepares feature metadata before normalizing or changing this build's persisted worn selection.</summary>
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        await PrepareAsync(userNum, ct).ConfigureAwait(false);
        return await store.UpdateLoadoutSelectionAsync(userNum, scope, groups, itemId, worn, ct).ConfigureAwait(false);
    }

    private ValueTask<PetInventorySnapshot?> PrepareAsync(long userNum, CancellationToken ct) => events is null
        ? ValueTask.FromResult<PetInventorySnapshot?>(null)
        : events.WaterfallAsync<InventoryMetadataPreparing, PetInventorySnapshot?>(new(scope.Key, userNum),
            static _ => ValueTask.FromResult<PetInventorySnapshot?>(null), ct);

    public async ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        var rows = await store.UpdateLoadoutSelectionAsync(userNum, scope, [], itemId, worn, ct).ConfigureAwait(false);
        return rows?.LastOrDefault(row => row.ItemId == itemId);
    }

    public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
        store.ReplaceSlotsAsync(userNum, bag, slots, ct);

    public async ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(long userNum, InventoryBag bag,
        IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
    {
        await store.AppendMissingItemsAsync(userNum, bag, candidates, ct).ConfigureAwait(false);
        return await ListSlotsAsync(userNum, bag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
    }

    public ValueTask<int> AdvanceLifeAsync(long userNum, DateOnly today, CancellationToken ct) => store.AdvanceLifeAsync(userNum, today, ct);

    public ValueTask<int?> ConsumeItemAsync(long userNum, int itemId, CancellationToken ct) => store.ConsumeItemAsync(userNum, itemId, ct);

    public async ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long userNum, DateTimeOffset now, CancellationToken ct) =>
        await ProjectAsync(userNum, await store.RemoveExpiredAsync(userNum, now, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await store.RemoveExpiredItemAsync(userNum, itemId, now, ct).ConfigureAwait(false);
        return rows is null ? null : await ProjectAsync(userNum, rows, ct).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<InventorySlotRecord>> ProjectAsync(long userNum, IReadOnlyList<InventorySlotRecord> rows, CancellationToken ct) =>
        InventoryLoadouts.Project(rows, await store.GetLoadoutAsync(userNum, scope, ct).ConfigureAwait(false), scope);

    public async ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
        (await store.GetLoadoutAsync(userNum, scope, ct).ConfigureAwait(false)).ActivePresetSlot;

    public async ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct) =>
        _ = await store.UpdateLoadoutPresetsAsync(userNum, scope, slot, null, false, ct).ConfigureAwait(false);

    public async ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct) =>
        (await store.GetLoadoutAsync(userNum, scope, ct).ConfigureAwait(false)).PresetPages;

    public async ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct) =>
        _ = await store.UpdateLoadoutPresetsAsync(userNum, scope, null, pages, false, ct).ConfigureAwait(false);

    public async ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct) =>
        (await store.UpdateLoadoutPresetsAsync(userNum, scope, null, initialPages, true, ct).ConfigureAwait(false)).PresetPages
        ?? throw new InvalidOperationException("Loadout preset initialization returned no table.");
}
