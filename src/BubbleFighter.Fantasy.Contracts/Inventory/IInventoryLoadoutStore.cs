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

    /// <summary>Commits selection and optional pet initialization together, returning both bags from that transaction.</summary>
    ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization = null) =>
        throw new NotSupportedException("This inventory store does not provide complete selection snapshots.");

    /// <summary>Commits selection and metadata initialization together, requiring provider support for Article metadata or an exact ownership target.</summary>
    ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
        PetInitializationCommand? initialization, ArticleInitializationCommand? articleInitialization,
        InventorySelectionTarget? target = null) =>
        articleInitialization is null && target is null
            ? UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct, initialization)
            : throw new NotSupportedException("This inventory store does not support combined Article metadata or ownership-checked selection.");

    /// <summary>Updates this build's active slot or full page table; initialization preserves already materialized pages.</summary>
    ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct);
}
