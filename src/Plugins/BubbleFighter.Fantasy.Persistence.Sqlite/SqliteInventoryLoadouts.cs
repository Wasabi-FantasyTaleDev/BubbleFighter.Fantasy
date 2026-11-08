using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IInventoryLoadoutStore
{
    public ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
        InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag);
        ArgumentNullException.ThrowIfNull(scope);
        return new(database.Read<IReadOnlyList<InventorySlotRecord>>(db =>
        {
            var state = db.State(userNum);
            return state is null ? [] : InventoryLoadouts.Project(state.Slots.Where(row => row.Bag == bag).ToArray(),
                ReadLoadout(state, scope), scope).Take(maxCount).ToArray();
        }, ct));
    }

    public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(Mutate(userNum, state =>
        {
            var loadout = ReadLoadout(state, scope);
            state.Loadouts[scope.Key] = loadout;
            return loadout.Copy();
        }, ct));
    }

    public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(Mutate<IReadOnlyList<InventorySlotRecord>?>(userNum, state =>
        {
            var loadout = ReadLoadout(state, scope);
            var changed = InventoryWearSelection.Apply(InventoryLoadouts.Project(state.Slots, loadout, scope), groups, itemId, worn);
            if (changed is null) return null;
            state.Loadouts[scope.Key] = loadout with
            {
                WornItemIds = changed.Where(row => row.Bag == InventoryBag.CharacterBag
                    && (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).Distinct().ToArray(),
            };
            return changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray();
        }, ct));
    }

    public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var copy = pages is null ? null : WeaponPresetPages.CopyTable(pages);
        if (activeSlot > InventoryLimits.MaxWeaponPresetSlot) throw new ArgumentOutOfRangeException(nameof(activeSlot));
        return new(Mutate(userNum, state =>
        {
            var loadout = InventoryLoadouts.Presets(ReadLoadout(state, scope), scope, activeSlot, copy, initializePages);
            state.Loadouts[scope.Key] = loadout;
            return loadout.Copy();
        }, ct));
    }

    private InventoryLoadoutState ReadLoadout(SaveState state, InventoryLoadoutScope scope)
    {
        if (!state.Loadouts.TryGetValue(scope.Key, out var loadout))
        {
            if (state.Loadouts.Count >= database.Options.MaxLoadoutsPerPlayer)
                throw new InvalidOperationException("The player's build loadout capacity is exhausted.");
            loadout = new(null, state.ActivePreset, state.WeaponPresetPages);
        }
        return InventoryLoadouts.Compatible(loadout, scope);
    }
}
