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

    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
        (await UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct).ConfigureAwait(false))?.CharacterBag;

    public ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization = null) =>
        UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct, initialization, null);

    public ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization, ArticleInitializationCommand? articleInitialization,
        InventorySelectionTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        initialization?.ValidateFor(userNum, scope);
        articleInitialization?.ValidateFor(userNum, scope);
        target?.ValidateFor(userNum, scope, itemId);
        return new(database.Write<PetInventorySnapshot?>((db, transaction) =>
        {
            var state = db.RequireState(userNum);
            if (target is not null && !target.Matches(InventoryLoadouts.Project(state.Slots, ReadLoadout(state, scope), scope))) return null;
            var plan = PreparePetInventory(db, transaction, state, scope, initialization, articleInitialization);
            var projected = InventoryLoadouts.Project(plan.Rows, plan.Loadout, scope);
            var changed = InventoryWearSelection.Apply(projected, groups, itemId, worn);
            if (changed is null) return null;
            var loadout = InventoryLoadouts.Selection(plan.Loadout, changed);
            var result = new PetInventorySnapshot(changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray(),
                changed.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
            CommitPetPreparation(db, transaction, userNum, state, scope, plan, plan.Rows, loadout);
            return result;
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
