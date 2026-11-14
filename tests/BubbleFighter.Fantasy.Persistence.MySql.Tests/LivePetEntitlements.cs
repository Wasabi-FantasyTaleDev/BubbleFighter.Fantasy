using BubbleFighter.Fantasy.Contracts.Inventory;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Database transactions retain late pet initialization only when the enclosing mutation succeeds.</summary>
    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("article", false)]
    [InlineData("mutation", false)]
    [InlineData("ordinary", true)]
    [InlineData("article", true)]
    [InlineData("mutation", true)]
    public async Task SelectionAndMutationCommitLatePetInitializationOnlyWhenAccepted(string operation, bool refuse)
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory);
        var now = DateTimeOffset.UtcNow;
        InventoryLoadoutScope scope = new("kr-1256", [301001, 301002, 100643]);
        PetProgression initial = new(0, 0, now.AddHours(1), null, null, null);
        PetInitializationCommand initialization = new(user, scope, [301001, 301002], initial, now);
        InventorySlotRecord original = new(user, InventoryBag.CharacterBag, 1, 301001, 1, 1)
            { Lifetime = InventoryLifetime.Permanent };
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [original], default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [original with { Bag = InventoryBag.EquippedBag }], default);
        var expected = (await store.InitializePetsAsync(initialization, default)).CharacterBag[0];
        InventorySlotRecord[] additions = refuse
            ? [original with { ItemId = 301002, SlotKey = 2, Flags = 0 }]
            : [original with { ItemId = 301002, SlotKey = 2, Flags = 0 }, new(user, InventoryBag.CharacterBag, 3, 100643, 2, 0)];
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, additions, default);
        await store.AppendMissingItemsAsync(user, InventoryBag.EquippedBag,
            additions.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var before = await store.ReadPetInventoryAsync(user, scope, default);
        var counters = await ScalarAsync<string>("SELECT CONCAT(CAST(entitlement_id AS CHAR),',',CAST(article_key AS CHAR)) FROM pet_identity_allocator WHERE singleton=1");
        var result = operation switch
        {
            "ordinary" => await store.UpdateLoadoutSelectionSnapshotAsync(user, scope, [], 100643, true, default, initialization),
            "article" => await store.SelectPetAsync(new(user, scope, [], new(301001, refuse
                ? expected.PetIdentity! with { ArticleKey = expected.PetIdentity!.ArticleKey + 1 } : expected.PetIdentity!), false, now)
                { Initialization = initialization }, default),
            _ => await store.MutatePetSnapshotAsync(new(refuse ? expected with { Quantity = 2 } : expected,
                scope, initial with { Experience = 1 }, refuse ? 0 : 100643, true, now) { Initialization = initialization }, default),
        };
        if (refuse)
        {
            Assert.Null(result);
            var after = await store.ReadPetInventoryAsync(user, scope, default);
            Assert.Equal(before.CharacterBag, after.CharacterBag);
            Assert.Equal(before.EquippedBag, after.EquippedBag);
            Assert.Equal(counters, await ScalarAsync<string>("SELECT CONCAT(CAST(entitlement_id AS CHAR),',',CAST(article_key AS CHAR)) FROM pet_identity_allocator WHERE singleton=1"));
            return;
        }
        Assert.NotNull(result);
        Assert.Equal(3, result.CharacterBag.Count);
        Assert.Equal(3, result.EquippedBag.Count);
        Assert.Equal(expected.PetIdentity, result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        foreach (var pet in result.CharacterBag.Where(row => row.ItemId is 301001 or 301002))
        {
            Assert.NotNull(pet.PetIdentity);
            Assert.NotNull(pet.Pet!.Find(scope.Key));
            var mirror = result.EquippedBag.Single(row => row.ItemId == pet.ItemId);
            Assert.Equal(pet.PetIdentity, mirror.PetIdentity);
            Assert.Equal(pet.Pet, mirror.Pet);
            Assert.Equal(pet.Flags & 1, mirror.Flags & 1);
        }
        if (operation == "ordinary")
        {
            Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
                row => Assert.Equal(1, row.Flags & 1));
            await store.UpdateLoadoutSelectionSnapshotAsync(user, scope, [], 100643, false, default, initialization);
            Assert.Equal(1, result.CharacterBag.Single(row => row.ItemId == 100643).Flags & 1);
        }
        if (operation == "mutation") Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
            row => Assert.Equal(1, row.Quantity));
    }

    /// <summary>Preparing existing database identities completes while another connection holds the allocation row lock.</summary>
    [Fact]
    public async Task ExistingPetPreparationDoesNotWaitForGlobalAllocatorLock()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory);
        var now = DateTimeOffset.UtcNow;
        InventoryLoadoutScope scope = new("kr-1256", [301001]);
        PetProgression initial = new(0, 0, now.AddHours(1), null, null, null);
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag,
            [new(user, InventoryBag.CharacterBag, 1, 301001, 1, 1) { Lifetime = InventoryLifetime.Permanent }], default);
        PetInitializationCommand command = new(user, scope, [301001], initial, now);
        var initialized = await store.InitializePetsAsync(command, default);
        await using var holder = await RawAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var hold = new MySqlCommand("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1 FOR UPDATE", holder, transaction))
            Assert.NotNull(await hold.ExecuteScalarAsync());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var repeated = await store.InitializePetsAsync(command, cancellation.Token);
        Assert.Equal(initialized.CharacterBag, repeated.CharacterBag);
        var legacy = new InventoryLoadoutScope("kr-812", scope.SupportedItemIds);
        var progressed = await store.InitializePetsAsync(command with { Scope = legacy }, cancellation.Token);
        Assert.Equal(initialized.CharacterBag[0].PetIdentity, progressed.CharacterBag[0].PetIdentity);
        Assert.Equal(initial, progressed.CharacterBag[0].Pet!.Find(legacy.Key));
    }

    /// <summary>Independent database connections preserve shared ownership and reject retired Article references after regrant.</summary>
    [Fact]
    public async Task PetArticleInitializationAcrossConnectionsKeepsGlobalOwnershipAndRejectsStaleRegrant()
    {
        if (await FactoryAsync() is not { } factory) return;
        var first = await NewPlayerAsync();
        var second = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        InventoryLoadoutScope scope = new("kr-1256", [301001]);
        PetProgression initial = new(0, 0, now.AddHours(1), null, null, null);
        InventorySlotRecord Pet(long user) => new(user, InventoryBag.CharacterBag, 1, 301001, 1, 0x81)
            { Lifetime = InventoryLifetime.Permanent };
        PetInitializationCommand Initialize(long user) => new(user, scope, [301001], initial, now);
        var store = new MySqlInventoryRepository(factory);
        foreach (var user in new[] { first, second })
        {
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user)], default);
            await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [Pet(user) with { Bag = InventoryBag.EquippedBag }], default);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
            await new MySqlInventoryRepository(factory).InitializePetsAsync(Initialize(index % 2 == 0 ? first : second), default))));
        var identities = results.Select(result => result.CharacterBag[0].PetIdentity!).Distinct().ToArray();
        Assert.Equal(2, identities.Length);
        Assert.Equal(2, identities.Select(identity => identity.EntitlementId).Distinct().Count());
        Assert.Equal(2, identities.Select(identity => identity.ArticleKey).Distinct().Count());
        Assert.All(results, result => Assert.Equal(result.CharacterBag[0].PetIdentity, result.EquippedBag[0].PetIdentity));
        store = new MySqlInventoryRepository(factory);
        var old = (await store.ReadPetInventoryAsync(first, scope, default)).CharacterBag[0];
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(second, InventoryBag.CharacterBag,
            [old with { UserNum = second }], default).AsTask());
        await store.ReplaceSlotsAsync(first, InventoryBag.CharacterBag, [], default);
        await store.ReplaceSlotsAsync(first, InventoryBag.CharacterBag, [Pet(first)], default);
        var replacement = await store.InitializePetsAsync(Initialize(first), default);
        Assert.True(replacement.CharacterBag[0].PetIdentity!.ArticleKey > identities.Max(identity => identity.ArticleKey));
        Assert.Equal(replacement.CharacterBag[0].PetIdentity, replacement.EquippedBag[0].PetIdentity);
        Assert.Equal(0, replacement.CharacterBag[0].Flags & 1);
        InventoryWearGroup[] groups = [new(301001, 301001, 301001)];
        Assert.Null(await store.SelectPetAsync(new(first, scope, groups, new(301001, old.PetIdentity!), true, now), default));
        var target = new PetEquipmentTarget(301001, replacement.CharacterBag[0].PetIdentity!);
        var selected = await store.SelectPetAsync(new(first, scope, groups, target, true, now), default);
        Assert.NotNull(selected);
        Assert.True((selected.CharacterBag[0].Flags & 1) != 0);
        var mutated = await store.MutatePetSnapshotAsync(new(selected.CharacterBag[0], scope,
            initial with { Experience = 5 }, 0, true, now), default);
        Assert.NotNull(mutated);
        Assert.Equal(mutated.CharacterBag[0].Pet, mutated.EquippedBag[0].Pet);
        Assert.Equal(target.Identity, mutated.CharacterBag[0].PetIdentity);
    }
}
