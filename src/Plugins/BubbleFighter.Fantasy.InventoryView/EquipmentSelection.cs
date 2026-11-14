using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Resolves the native character and weapon positions independently of UI category selection.</summary>
internal sealed class EquipmentSelection(IInventoryRepository inventory,
    IReadOnlyList<InventoryWearGroup>? wearableLocations = null, EquipmentWearGroups? contributions = null,
    IPetEntitlements? pets = null, IItemEnhancementStore? articles = null, InventoryLoadoutScope? scope = null, IEquipmentArticleResolver? articleResolver = null, IInventoryLoadoutStore? loadouts = null) : IEquipmentSelection
{
    // 0x808F10 / 0x81B9C0: primary and special preset positions; ShopItem2 identifies
    // 202/203/204 as rifle/sniper/shotgun, 201 as sidearm, 205 as special, 207 as melee.
    private static readonly InventoryWearGroup[] Groups =
    [
        new(300001, 300031, 300001, RequireEquipped: true),
        new(202000, 204999, 202001, RequireEquipped: true),
        new(201000, 201999, 201001, RequireEquipped: true),
        new(205000, 205999, 205001),
        new(207000, 207999, 207001, RequireEquipped: true),
    ];

    private readonly InventoryWearGroup[] _groups = [.. Groups, .. wearableLocations ?? []];

    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
        await inventory.UpdateWornSelectionAsync(userNum, await GroupsAsync(ct).ConfigureAwait(false),
            null, false, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Equipment normalization returned no inventory.");

    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
        await inventory.UpdateWornSelectionAsync(userNum, await GroupsAsync(ct).ConfigureAwait(false),
            itemId, worn, ct).ConfigureAwait(false);

    /// <summary>Commits a resolved pet Article with the native equipment positions and protocol-owned group contributions.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyPetAsync(long userNum, PetEquipmentTarget target, bool worn, CancellationToken ct) =>
        pets is null ? throw new InvalidOperationException("Article selection requires a pet entitlement provider.")
            : await pets.SelectAsync(userNum, target, await GroupsAsync(ct).ConfigureAwait(false), worn, ct, articleResolver?.CreateInitialization(userNum, DateTimeOffset.UtcNow)).ConfigureAwait(false);

    /// <summary>Applies the native equipment groups while retaining the exact resolved Article precondition.</summary>
    public async ValueTask<InventorySnapshot?> ApplyArticleAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        if (articles is null || scope is null || articleResolver is null)
            throw new InvalidOperationException("Article selection requires its build-scoped store and native metadata catalog.");
        var now = DateTimeOffset.UtcNow;
        return await articles.SelectArticleAsync(new(userNum, scope, expectedTarget, await GroupsAsync(ct).ConfigureAwait(false), worn, now)
            { ArticleInitialization = articleResolver.CreateInitialization(userNum, now), PetInitialization = pets?.CreateInitialization(userNum, now) }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Retains an ordinary ownership row through the same atomic selection used by metadata-backed inventory publication.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplyOwnedSnapshotAsync(long userNum, InventorySlotRecord expectedTarget, bool worn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (loadouts is null || scope is null)
            throw new InvalidOperationException("Owned selection requires the build-scoped inventory store.");
        var groups = await GroupsAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return await loadouts.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, expectedTarget.ItemId, worn, ct,
            pets?.CreateInitialization(userNum, now), articleResolver?.CreateInitialization(userNum, now), new(expectedTarget, now))
            .ConfigureAwait(false);
    }

    /// <summary>Commits an item selection and its pet metadata together, returning both bags for publication.</summary>
    public async ValueTask<PetInventorySnapshot?> ApplySnapshotAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        var initialization = articleResolver?.CreateInitialization(userNum, DateTimeOffset.UtcNow);
        var groups = await GroupsAsync(ct).ConfigureAwait(false);
        if (pets is not null) return await pets.SelectItemAsync(userNum, itemId, groups, worn, ct, initialization).ConfigureAwait(false);
        if (loadouts is null || scope is null)
            throw new InvalidOperationException("Complete metadata selection requires a build-scoped initialization provider.");
        return await loadouts.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct,
            initialization: null, articleInitialization: initialization).ConfigureAwait(false);
    }

    private ValueTask<IReadOnlyList<InventoryWearGroup>> GroupsAsync(CancellationToken ct) =>
        contributions?.ResolveAsync(_groups, ct) ?? ValueTask.FromResult<IReadOnlyList<InventoryWearGroup>>(_groups);
}
