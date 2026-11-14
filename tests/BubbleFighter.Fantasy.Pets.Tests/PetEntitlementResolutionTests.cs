using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>Checks Article lookup against existing ownership without preparing metadata before equipment policy.</summary>
public sealed class PetEntitlementResolutionTests
{
    private const int OtherPetId = 301002;

    /// <summary>A published identity remains usable while another newly granted pet stays uninitialized.</summary>
    [Fact]
    public async Task InitializedArticleResolvesWithoutPreparingAnotherOwnedPet()
    {
        var rig = new Rig();
        await rig.SeedAsync(1, PetTestData.PetId);
        var owned = Assert.Single((await rig.Writer.PrepareAsync(1, default)).CharacterBag);
        var identity = Assert.IsType<PetEntitlementIdentity>(owned.PetIdentity);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await rig.Store.AppendMissingItemsAsync(1, bag, [Slot(1, OtherPetId, bag)], default);
        var before = await rig.RowsAsync(1);

        var target = await rig.Resolver.ResolveAsync(1, identity.ArticleKey, default);

        Assert.Equal(new PetEquipmentTarget(owned.ItemId, identity), target);
        Assert.Equal(before, await rig.RowsAsync(1));
        Assert.Equal(1, rig.ReadOnlyStore.ReadCount);
        Assert.All(before.Where(row => row.ItemId == OtherPetId), row => Assert.Null(row.PetIdentity));
    }

    /// <summary>An unknown addressable key cannot allocate identities or create progression on unrelated ownership.</summary>
    [Fact]
    public async Task UnknownAddressableArticleLeavesUninitializedPetsAndAllocatorUntouched()
    {
        var rig = new Rig();
        await rig.SeedAsync(1, PetTestData.PetId, OtherPetId);
        var before = await rig.RowsAsync(1);

        Assert.Null(await rig.Resolver.ResolveAsync(1, PetArticleKeys.First + 100, default));

        Assert.Equal(before, await rig.RowsAsync(1));
        Assert.Equal(1, rig.ReadOnlyStore.ReadCount);
        var prepared = await rig.Writer.PrepareAsync(1, default);
        Assert.Equal(new[] { PetArticleKeys.First, PetArticleKeys.First + 1 },
            prepared.CharacterBag.Select(row => row.PetIdentity!.ArticleKey).Order());
    }

    /// <summary>A shared identity becomes resolvable only after the requested build has explicitly initialized its progression.</summary>
    [Fact]
    public async Task AnotherBuildsArticleRequiresExistingCurrentBuildProgression()
    {
        var legacy = new Rig("kr-812");
        await legacy.SeedAsync(1, PetTestData.PetId);
        var owned = Assert.Single((await legacy.Writer.PrepareAsync(1, default)).CharacterBag);
        var identity = Assert.IsType<PetEntitlementIdentity>(owned.PetIdentity);
        var originalProgression = Assert.IsType<PetProgressions>(owned.Pet);
        var modern = new Rig(store: legacy.Store);
        var before = await modern.RowsAsync(1);

        Assert.Null(await modern.Resolver.ResolveAsync(1, identity.ArticleKey, default));

        Assert.Equal(before, await modern.RowsAsync(1));
        Assert.Null(originalProgression.Find(modern.Scope.Key));
        var prepared = Assert.Single((await modern.Writer.PrepareAsync(1, default)).CharacterBag);
        var currentProgression = Assert.IsType<PetProgressions>(prepared.Pet);
        Assert.Equal(identity, prepared.PetIdentity);
        Assert.Equal(originalProgression.Find(legacy.Scope.Key), currentProgression.Find(legacy.Scope.Key));
        Assert.NotNull(currentProgression.Find(modern.Scope.Key));
        Assert.Equal(new PetEquipmentTarget(owned.ItemId, identity),
            await modern.Resolver.ResolveAsync(1, identity.ArticleKey, default));
    }

    /// <summary>Unavailable ownership and missing progression are refused without repairing either bag.</summary>
    [Theory]
    [InlineData("expired")]
    [InlineData("empty")]
    [InlineData("missing-progression")]
    public async Task UnavailableArticleIsRefusedWithoutMutation(string reason)
    {
        var rig = new Rig();
        await rig.SeedAsync(1, PetTestData.PetId);
        var owned = Assert.Single((await rig.Writer.PrepareAsync(1, default)).CharacterBag);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var row = owned with { Bag = bag };
            row = reason switch
            {
                "expired" => row with { Lifetime = InventoryLifetime.Until(PetTestData.Now) },
                "empty" => row with { Quantity = 0 },
                _ => row with { Pet = null },
            };
            await rig.Store.ReplaceSlotsAsync(1, bag, [row], default);
        }
        var before = await rig.RowsAsync(1);

        Assert.Null(await rig.Resolver.ResolveAsync(1, owned.PetIdentity!.ArticleKey, default));

        Assert.Equal(before, await rig.RowsAsync(1));
    }

    /// <summary>An Article owned by another account does not authorize a target in the requesting account.</summary>
    [Fact]
    public async Task AnotherOwnersArticleCannotResolve()
    {
        var rig = new Rig();
        await rig.SeedAsync(1, PetTestData.PetId);
        await rig.SeedAsync(2, PetTestData.PetId);
        var owned = Assert.Single((await rig.Writer.PrepareAsync(1, default)).CharacterBag);
        await rig.Writer.PrepareAsync(2, default);
        var before = await rig.RowsAsync(2);

        Assert.Null(await rig.Resolver.ResolveAsync(2, owned.PetIdentity!.ArticleKey, default));

        Assert.Equal(before, await rig.RowsAsync(2));
    }

    /// <summary>Saved metadata cannot override the active build's supported items or native pet classification.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentCatalogMustSupportAndClassifyTheTargetAsPet(bool supported)
    {
        var original = new Rig();
        await original.SeedAsync(1, PetTestData.PetId);
        var owned = Assert.Single((await original.Writer.PrepareAsync(1, default)).CharacterBag);
        var catalog = Catalog();
        var items = catalog.Items.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (supported) items[PetTestData.PetId] = items[PetTestData.PetId] with { Function = 0 };
        else items.Remove(PetTestData.PetId);
        var current = new Rig(store: original.Store, catalog: new(items,
            catalog.Skills.ToDictionary(pair => pair.Key, pair => pair.Value),
            catalog.Buffs.ToDictionary(pair => pair.Key, pair => pair.Value), catalog.PetCategory));
        var before = await current.RowsAsync(1);

        Assert.Null(await current.Resolver.ResolveAsync(1, owned.PetIdentity!.ArticleKey, default));

        Assert.Equal(before, await current.RowsAsync(1));
    }

    /// <summary>Removing and regranting the same catalog item invalidates both the old Article and a previously resolved target.</summary>
    [Fact]
    public async Task RegrantInvalidatesArticleLookupAndTransactionalSelection()
    {
        var rig = new Rig();
        await rig.SeedAsync(1, PetTestData.PetId);
        var original = Assert.Single((await rig.Writer.PrepareAsync(1, default)).CharacterBag);
        var target = Assert.IsType<PetEquipmentTarget>(
            await rig.Resolver.ResolveAsync(1, original.PetIdentity!.ArticleKey, default));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await rig.Store.ReplaceSlotsAsync(1, bag, [], default);
        await rig.SeedAsync(1, PetTestData.PetId);
        var uninitialized = await rig.RowsAsync(1);

        Assert.Null(await rig.Resolver.ResolveAsync(1, target.Identity.ArticleKey, default));
        Assert.Null(await rig.Writer.SelectAsync(1, target, [], true, default));
        Assert.Equal(uninitialized, await rig.RowsAsync(1));

        var replacement = Assert.Single((await rig.Writer.PrepareAsync(1, default)).CharacterBag);
        var replacementIdentity = Assert.IsType<PetEntitlementIdentity>(replacement.PetIdentity);
        Assert.Equal(target.Identity.ArticleKey + 1, replacementIdentity.ArticleKey);
        var before = await rig.RowsAsync(1);
        Assert.Null(await rig.Resolver.ResolveAsync(1, target.Identity.ArticleKey, default));
        Assert.Null(await rig.Writer.SelectAsync(1, target, [], true, default));
        Assert.Equal(before, await rig.RowsAsync(1));
        var current = Assert.IsType<PetEquipmentTarget>(
            await rig.Resolver.ResolveAsync(1, replacementIdentity.ArticleKey, default));
        Assert.NotNull(await rig.Writer.SelectAsync(1, current, [], true, default));
    }

    /// <summary>References outside the native Article interval are refused before any persistence read.</summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(99999999UL)]
    [InlineData(9223372036854775808UL)]
    [InlineData(18446744073709551615UL)]
    public async Task UnaddressableArticleDoesNotReadPersistence(ulong articleKey)
    {
        var rig = new Rig();

        Assert.Null(await rig.Resolver.ResolveAsync(1, articleKey, default));

        Assert.Equal(0, rig.ReadOnlyStore.ReadCount);
    }

    private static InventorySlotRecord Slot(long userNum, int itemId, InventoryBag bag) =>
        new(userNum, bag, itemId, itemId, 1, 0) { Lifetime = InventoryLifetime.Permanent };

    private static PetCatalog Catalog()
    {
        var original = PetTestData.Catalog();
        var items = original.Items.ToDictionary(pair => pair.Key, pair => pair.Value);
        items.Add(OtherPetId, items[PetTestData.PetId] with { ItemId = OtherPetId });
        return new(items, original.Skills.ToDictionary(pair => pair.Key, pair => pair.Value),
            original.Buffs.ToDictionary(pair => pair.Key, pair => pair.Value), original.PetCategory);
    }

    private sealed class Rig
    {
        internal Rig(string build = "kr-1256", MemoryInventoryRepository? store = null, PetCatalog? catalog = null)
        {
            Store = store ?? new(new MemoryPlayerTable(4));
            catalog ??= Catalog();
            Scope = new(build, catalog.Items.Keys.Order().ToArray());
            var rules = new PetRules(catalog, PetTestData.Options(manual: build == "kr-1256", build: build),
                new PetTestData.MinimumRandom());
            var clock = new PetTestData.Clock();
            Writer = new(catalog, rules, Store, Store, Scope, clock);
            ReadOnlyStore = new(Store);
            Resolver = new(catalog, rules, ReadOnlyStore, Store, Scope, clock);
        }

        internal MemoryInventoryRepository Store { get; }
        internal InventoryLoadoutScope Scope { get; }
        internal PetEntitlements Writer { get; }
        internal PetEntitlements Resolver { get; }
        internal ResolutionStore ReadOnlyStore { get; }

        internal async Task SeedAsync(long userNum, params int[] itemIds)
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                await Store.ReplaceSlotsAsync(userNum, bag, itemIds.Select(item => Slot(userNum, item, bag)).ToArray(), default);
        }

        internal async Task<InventorySlotRecord[]> RowsAsync(long userNum) =>
            [.. await Store.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default),
             .. await Store.ListSlotsAsync(userNum, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, default)];
    }

    private sealed class ResolutionStore(IPetEntitlementStore inner) : IPetEntitlementStore
    {
        internal int ReadCount { get; private set; }

        /// <summary>Counts lookup reads while preserving the real store's projection and ownership validation.</summary>
        public ValueTask<PetInventorySnapshot> ReadPetInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
        {
            ReadCount++;
            return inner.ReadPetInventoryAsync(userNum, scope, ct);
        }

        /// <summary>Fails a lookup that attempts to prepare metadata before equipment policy.</summary>
        public ValueTask<PetInventorySnapshot> InitializePetsAsync(PetInitializationCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("Article resolution must not initialize pet metadata.");

        /// <summary>Fails a lookup that attempts to persist an equipment decision.</summary>
        public ValueTask<PetInventorySnapshot?> SelectPetAsync(PetEquipmentSelectionCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("Article resolution must not change equipment selection.");
    }
}
