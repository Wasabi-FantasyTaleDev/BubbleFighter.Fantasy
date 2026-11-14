using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets;

internal sealed class PetEntitlements(PetCatalog catalog, PetRules rules,
    IPetEntitlementStore store, IInventoryLoadoutStore loadouts, InventoryLoadoutScope scope, TimeProvider time) : IPetEntitlements
{
    private readonly IReadOnlyList<int> _petItems = Array.AsReadOnly(catalog.Items.Values
        .Where(item => item.Function == PetCatalog.PetFunction).Select(item => item.ItemId).Order().ToArray());

    /// <summary>Gets the canonical build key used for equipment selection and saved pet progression.</summary>
    public string BuildKey => scope.Key;

    /// <summary>Identifies pet ownership by the native catalog's pet function.</summary>
    public bool IsPetItem(int itemId) => catalog.Items.TryGetValue(itemId, out var item) && item.Function == PetCatalog.PetFunction;

    /// <summary>Combines the build's pet item catalog and initial progression rules into transaction-owned initialization inputs.</summary>
    public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
        new(userNum, scope, _petItems, rules.Initial(now), now);

    /// <summary>Initializes missing owned pet identities and this build's progression using the current provider time.</summary>
    public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return store.InitializePetsAsync(CreateInitialization(userNum, now), ct);
    }

    /// <summary>Resolves a native Article from existing live ownership and build progression without initializing metadata, rejecting ambiguous identities.</summary>
    public async ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct)
    {
        // kr-1256 sub_806CE0 reserves the lower identities for ordinary item lookup.
        if (!PetArticleKeys.IsAddressable(articleKey)) return null;
        var snapshot = await store.ReadPetInventoryAsync(userNum, scope, ct).ConfigureAwait(false);
        var matches = snapshot.CharacterBag.Where(row => row.PetIdentity?.ArticleKey == articleKey).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("An Article key identifies more than one owned pet.");
        if (matches.Length == 0) return null;
        var row = matches[0];
        if (!scope.Supports(row.ItemId) || !IsPetItem(row.ItemId) || row.Pet?.Find(scope.Key) is null
            || row.Quantity <= 0 || InventoryExpiry.IsExpired(row, time.GetUtcNow())) return null;
        return new(row.ItemId, row.PetIdentity!);
    }

    /// <summary>Revalidates a catalog pet's resolved identity and commits its worn state together with any required metadata initialization.</summary>
    public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
        SelectAsync(userNum, target, groups, worn, ct, null);

    /// <summary>Stages both native metadata catalogs with the exact pet selection.</summary>
    public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct, ArticleInitializationCommand? articleInitialization)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!catalog.Items.TryGetValue(target.ItemId, out var item) || item.Function != PetCatalog.PetFunction)
            return ValueTask.FromResult<PetInventorySnapshot?>(null);
        var now = time.GetUtcNow();
        return store.SelectPetAsync(new(userNum, scope, groups, target, worn, now)
            { Initialization = CreateInitialization(userNum, now), ArticleInitialization = articleInitialization }, ct);
    }

    /// <summary>Commits an ordinary item selection with the same build's pet initialization and returns the complete inventory snapshot.</summary>
    public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
        SelectItemAsync(userNum, itemId, groups, worn, ct, null);

    /// <summary>Initializes both native metadata catalogs before publishing the selected inventory.</summary>
    public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
        IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct, ArticleInitializationCommand? articleInitialization) =>
        loadouts.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct,
            CreateInitialization(userNum, time.GetUtcNow()), articleInitialization);
}
