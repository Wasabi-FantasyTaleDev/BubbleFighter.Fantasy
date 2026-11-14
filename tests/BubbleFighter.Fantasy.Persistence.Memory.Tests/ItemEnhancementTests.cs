using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks atomic equipment changes, exact ownership and shared Article allocation in memory.</summary>
public sealed class ItemEnhancementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 300101, 300102, 190106, 190107, 301001]);

    /// <summary>Items granted after request resolution initialize in the same commit, while a stale plan advances no allocator.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LateEquipmentAndPetPreparationCommitsOnlyWithTheEnclosingOperation(bool selection, bool refuse)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        var snapshot = await Seed(store);
        InventorySlotRecord[] late = [
            new(1, InventoryBag.CharacterBag, 9, 300102, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 10, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, late, default);
        await store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag, late.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var articles = new ArticleInitializationCommand(1, Scope, [300100, 300101, 300102], Now);
        var pets = new PetInitializationCommand(1, Scope, [301001], new(0, 0, Now.AddHours(1), null, null, null), Now);
        var targets = snapshot.CharacterBag.Where(row => row.ItemId is 300100 or 300101).ToArray();
        if (refuse) targets[0] = targets[0] with { SlotKey = 999 };
        var result = selection
            ? await store.SelectArticleAsync(new(1, Scope, targets[0], [], true, Now)
                { ArticleInitialization = articles, PetInitialization = pets }, default)
            : await store.TryEnhanceAsync(new(targets, Scope, Command(snapshot, true).MaterialDebits, ItemEnhancementKind.Clothing, true, Now)
                { ArticleInitialization = articles, PetInitialization = pets }, default);
        var saved = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        if (refuse)
        {
            Assert.Null(result);
            Assert.Equal(2UL, table.LastPetEntitlementId);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 300102).ArticleIdentity);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 301001).Pet);
            Assert.Equal(4, saved.CharacterBag.Single(row => row.ItemId == 190106).Quantity);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(4UL, table.LastPetEntitlementId);
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 300102).ArticleIdentity);
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 301001).Pet!.Find(Scope.Key));
            Assert.Equal(result.CharacterBag, saved.CharacterBag);
            Assert.Equal(result.EquippedBag, saved.EquippedBag);
        }
    }

    /// <summary>One exact failed plan may consume materials once, and its revision survives replenishment and JSON round trips.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDuplicatePlansCommitEveryTargetAndMaterialExactlyOnce(bool success)
    {
        var store = new MemoryInventoryRepository(new(1));
        var snapshot = await Seed(store);
        var command = Command(snapshot, success);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryEnhanceAsync(command, default))));
        var result = Assert.Single(results, row => row is not null)!;
        foreach (var bag in new[] { result.CharacterBag, result.EquippedBag })
        {
            Assert.Equal(2, bag.Single(row => row.ItemId == 190106).Quantity);
            Assert.DoesNotContain(bag, row => row.ItemId == 190107);
            foreach (var target in bag.Where(row => row.ItemId is 300100 or 300101))
            {
                Assert.Equal(new ItemEnhancementState(success ? ItemEnhancementKind.Clothing : ItemEnhancementKind.None, 1), target.Enhancements!.Find(Scope.Key));
                Assert.Equal(target.Enhancements, JsonSerializer.Deserialize<ItemEnhancements>(JsonSerializer.Serialize(target.Enhancements)));
            }
        }
        Assert.Null(await store.TryEnhanceAsync(command, default));
        Assert.Equal(4, snapshot.CharacterBag.Single(row => row.ItemId == 190106).Quantity);
        Assert.Null(snapshot.CharacterBag[0].Enhancements);
        if (!success)
        {
            var retry = new ItemEnhancementCommand(result.CharacterBag.Where(row => row.ItemId is 300100 or 300101).ToArray(), Scope,
                [new(result.CharacterBag.Single(row => row.ItemId == 190106), 2)], ItemEnhancementKind.Clothing, true, Now);
            var succeeded = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(retry, default));
            Assert.All(succeeded.CharacterBag.Where(row => row.ItemId is 300100 or 300101),
                row => Assert.Equal(new ItemEnhancementState(ItemEnhancementKind.Clothing, 2), row.Enhancements!.Find(Scope.Key)));
        }
    }

    /// <summary>Every target and debit is checked before any part of the mutation is saved.</summary>
    [Theory]
    [InlineData("missing_material")]
    [InlineData("equipped")]
    [InlineData("expired_material")]
    [InlineData("stale_identity")]
    public async Task InvalidPlanChangesNeitherTargetsNorOtherMaterials(string reason)
    {
        var store = new MemoryInventoryRepository(new(1));
        var original = await Seed(store, reason == "expired_material");
        var command = Command(original, true);
        if (reason == "missing_material")
            await store.ConsumeItemAsync(1, 190107, default);
        else if (reason == "equipped")
            Assert.NotNull(await store.SelectArticleAsync(new(1, Scope, original.CharacterBag[0], [], true, Now), default));
        else if (reason == "stale_identity")
            command = new([original.CharacterBag[0] with { ArticleIdentity = new(9999, 199999999) }, original.CharacterBag[1]],
                Scope, command.MaterialDebits, ItemEnhancementKind.Clothing, true, Now);
        var before = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.Null(await store.TryEnhanceAsync(command, default));
        var after = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
    }

    /// <summary>Pet and ordinary equipment allocate from one pool and retain their identity when viewed in another build.</summary>
    [Fact]
    public async Task ArticleAllocationSharesPetPoolAndBuildSelectionsRemainIndependent()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        var original = await Seed(store);
        var pet = new InventorySlotRecord(1, InventoryBag.CharacterBag, 8, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent };
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [pet], default);
        var pets = await store.InitializePetsAsync(new(1, Scope, [301001], new(0, 0, Now, null, null, null), Now), default);
        Assert.Equal(3, pets.CharacterBag.Select(InventoryArticleIdentityResolver.Of).OfType<InventoryArticleIdentity>()
            .Select(identity => identity.ArticleKey).Distinct().Count());
        Assert.Equal(3UL, table.LastPetEntitlementId);
        var target = original.CharacterBag[0];
        var selected = Assert.IsType<InventorySnapshot>(await store.SelectArticleAsync(new(1, Scope, target, [], true, Now), default));
        Assert.Equal(1, selected.CharacterBag[0].Flags & 1);
        Assert.Null(await store.SelectArticleAsync(new(1, Scope, target, [], true, Now), default));
        var other = new InventoryLoadoutScope("kr-other", Scope.SupportedItemIds);
        var separate = await store.InitializeArticlesAsync(new(1, other, [300100, 300101], Now), default);
        Assert.Equal(target.ArticleIdentity, separate.CharacterBag[0].ArticleIdentity);
        Assert.Equal(0, separate.CharacterBag[0].Flags & 1);
        Assert.Equal(1, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag[0].Flags & 1);
    }

    /// <summary>Managed identity and enhancement fields cannot be copied through ordinary external replace writes.</summary>
    [Fact]
    public async Task ExternalWritesCannotForgeOrEraseManagedMetadata()
    {
        var store = new MemoryInventoryRepository(new(1));
        var snapshot = await Seed(store);
        var rows = snapshot.CharacterBag.ToArray();
        rows[0] = rows[0] with { ArticleIdentity = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default).AsTask());
        rows[0] = snapshot.CharacterBag[0] with { Enhancements = ItemEnhancements.Empty.With(Scope.Key, new(ItemEnhancementKind.Weapon, 1)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default).AsTask());
        Assert.Equal(snapshot.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
    }

    /// <summary>Exhausted scope capacity and cancellation refuse the whole planned debit and metadata change.</summary>
    [Fact]
    public async Task CapacityAndCancellationLeaveTheOriginalSnapshotIntact()
    {
        var store = new MemoryInventoryRepository(new(1), maxLoadoutsPerPlayer: 1);
        var snapshot = await Seed(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryEnhanceAsync(Command(snapshot, true), new(true)).AsTask());
        var other = new InventoryLoadoutScope("kr-other", Scope.SupportedItemIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeArticlesAsync(new(1, other, [300100], Now), default).AsTask());
        Assert.Equal(snapshot.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
    }

    private static ItemEnhancementCommand Command(InventorySnapshot snapshot, bool success) => new(
        snapshot.CharacterBag.Where(row => row.ItemId is 300100 or 300101).ToArray(), Scope,
        [new(snapshot.CharacterBag.Single(row => row.ItemId == 190106), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190107), 2)],
        ItemEnhancementKind.Clothing, success, Now);

    private static async Task<InventorySnapshot> Seed(MemoryInventoryRepository store, bool expired = false)
    {
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 300101, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 3, 190106, 4, 0),
            new(1, InventoryBag.CharacterBag, 4, 190107, 2, 0)
                { Lifetime = expired ? InventoryLifetime.Until(Now.AddMinutes(-1)) : default },
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return await store.InitializeArticlesAsync(new(1, Scope, [300100, 300101], Now), default);
    }
}
