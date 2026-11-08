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
    public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var state = ReadLoadout(entry, scope);
            var rows = new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag }
                .SelectMany(bag => entry.Bags.GetValueOrDefault((int)bag) ?? []).ToArray();
            var changed = InventoryWearSelection.Apply(InventoryLoadouts.Project(rows, state, scope), groups, itemId, worn);
            if (changed is null) return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>(null);
            entry.Loadouts[scope.Key] = state with
            {
                WornItemIds = changed.Where(row => row.Bag == InventoryBag.CharacterBag
                    && (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).Distinct().ToArray(),
            };
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>(changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray());
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
