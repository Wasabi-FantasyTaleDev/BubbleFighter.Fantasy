using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Pets;

/// <summary>Resolves native pet identities and initializes one build's progression through its feature-owned catalog.</summary>
public interface IPetEntitlements
{
    /// <summary>Gets the build whose native catalog and saved progression this provider uses.</summary>
    string BuildKey { get; }

    /// <summary>Tests whether the build's native catalog assigns this item to pet ownership.</summary>
    bool IsPetItem(int itemId);

    /// <summary>Supplies immutable native initialization inputs for use inside another inventory mutation's transaction.</summary>
    PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now);

    /// <summary>Materializes missing pet identities and progression, returning both bags from the same committed transaction.</summary>
    ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Resolves a live owned Article from existing metadata for this build without initializing identities, progression or selection.
    /// Missing metadata returns null; selection must recheck the returned identity in its own transaction.
    /// </summary>
    ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct);

    /// <summary>Revalidates the resolved entitlement and commits selection with the current build's equipment conflict groups.</summary>
    ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct);

    /// <summary>Commits an ordinary item selection and pet metadata together, returning a complete snapshot for publication.</summary>
    ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct);
    /// <summary>Commits exact pet ownership and optional equipment Article initialization in one transaction.</summary>
    ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct, ArticleInitializationCommand? articleInitialization) =>
        articleInitialization is null ? SelectAsync(userNum, target, groups, worn, ct)
            : throw new NotSupportedException("This pet provider does not support combined equipment metadata initialization.");

    /// <summary>Commits an ordinary selection together with pet and optional equipment Article metadata.</summary>
    ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct, ArticleInitializationCommand? articleInitialization) =>
        articleInitialization is null ? SelectItemAsync(userNum, itemId, groups, worn, ct)
            : throw new NotSupportedException("This pet provider does not support combined equipment metadata initialization.");
}
