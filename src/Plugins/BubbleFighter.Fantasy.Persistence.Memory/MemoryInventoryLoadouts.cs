using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
        InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag, nameof(maxCount));
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([]);
        lock (entry.Gate)
        {
            var state = ReadLoadout(entry, scope);
            foreach (var row in entry.Bags.GetValueOrDefault((int)bag) ?? []) row.Pet?.Validate(maxLoadoutsPerPlayer);
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(InventoryLoadouts.Project(
                entry.Bags.GetValueOrDefault((int)bag) ?? [], state, scope).Take(maxCount).ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var state = ReadLoadout(entry, scope);
            entry.Loadouts[scope.Key] = state;
            return ValueTask.FromResult(state.Copy());
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
        (await UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct).ConfigureAwait(false))?.CharacterBag;

    /// <inheritdoc />
    public ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization = null) =>
        UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct, initialization, null);

    /// <inheritdoc />
    public ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization, ArticleInitializationCommand? articleInitialization,
        InventorySelectionTarget? target = null)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        initialization?.ValidateFor(userNum, scope);
        articleInitialization?.ValidateFor(userNum, scope);
        target?.ValidateFor(userNum, scope, itemId);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            if (target is not null && !target.Matches(InventoryLoadouts.Project(ArticleRows(entry), ReadLoadout(entry, scope), scope)))
                return ValueTask.FromResult<PetInventorySnapshot?>(null);
            return ValueTask.FromResult(WithPetPreparation<PetInventorySnapshot?>(entry, scope, initialization, plan =>
            {
                var projected = InventoryLoadouts.Project(plan.Rows, plan.Loadout, scope);
                var changed = InventoryWearSelection.Apply(projected, groups, itemId, worn);
                if (changed is null) return null;
                var state = InventoryLoadouts.Selection(plan.Loadout, changed);
                var result = new PetInventorySnapshot(changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray(),
                    changed.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
                CommitPetPreparation(entry, scope, plan, plan.Rows, state, ct);
                return result;
            }, articleInitialization));
        }
    }

    /// <inheritdoc />
    public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        var copy = pages is null ? null : WeaponPresetPages.CopyTable(pages);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var state = InventoryLoadouts.Presets(ReadLoadout(entry, scope), scope, activeSlot, copy, initializePages);
            entry.Loadouts[scope.Key] = state;
            return ValueTask.FromResult(state.Copy());
        }
    }

    private InventoryLoadoutState ReadLoadout(PlayerEntry entry, InventoryLoadoutScope scope)
    {
        if (!entry.Loadouts.TryGetValue(scope.Key, out var state))
        {
            if (maxLoadoutsPerPlayer is < 1 or > 256 || entry.Loadouts.Count >= maxLoadoutsPerPlayer)
                throw new InvalidOperationException("The player's build loadout capacity is exhausted or invalid.");
            state = new(null, entry.ActiveWeaponPresetSlot, entry.WeaponPresetPages);
        }
        return InventoryLoadouts.Compatible(state, scope);
    }
}
