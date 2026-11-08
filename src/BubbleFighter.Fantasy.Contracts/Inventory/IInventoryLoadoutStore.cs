namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Shared item ownership with atomic, independently keyed equipment and weapon-preset selections.</summary>
public interface IInventoryLoadoutStore : IInventoryRepository
{
    /// <summary>Reads one bag and its build selection from the same player transaction, preserving their consistency.</summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
        InventoryBag bag, int maxCount, CancellationToken ct);

    /// <summary>Initializes a missing build selection from compatible legacy state and returns a defensive snapshot.</summary>
    ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct);

    /// <summary>Normalizes or changes only this build's worn selection under the same player transaction as purchases.</summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct);

    /// <summary>Updates this build's active slot or full page table; initialization preserves already materialized pages.</summary>
    ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct);
}
