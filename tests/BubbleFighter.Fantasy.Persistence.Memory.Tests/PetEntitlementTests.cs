using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks ownership allocation, retirement and complete transactional pet snapshots for memory persistence.</summary>
public sealed class PetEntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [301001, 301002, 100643]);
    private static readonly PetProgression Initial = new(0, 0, Now.AddHours(1), null, null, null);
    private static readonly InventoryWearGroup[] Groups = [new(301001, 301002, 301001)];

    /// <summary>Pins the inclusive native Article range and rejects unsigned references routed to ordinary lookup.</summary>
    [Theory]
    [InlineData(99999999UL, false)]
    [InlineData(100000000UL, true)]
    [InlineData(9223372036854775807UL, true)]
    [InlineData(9223372036854775808UL, false)]
    public void NativeSignedArticleRangeRejectsHighBitReferencesBeforeOrdinaryItemAliasing(ulong key, bool addressable)
    {
        // kr-1256 sub_806CE0 routes high-bit references through ordinary lookup; the server refuses them
        // as Articles so the low word cannot alias an unrelated owned item.
        Assert.Equal(addressable, PetArticleKeys.IsAddressable(key));
    }

    /// <summary>Successful selection or growth returns identities for pets granted after the original target snapshot.</summary>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("article")]
    [InlineData("mutation")]
    public async Task CompleteMutationInitializesOtherPetsGrantedAfterTheTargetWasRead(string operation)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, true);
        var expected = (await store.InitializePetsAsync(Command(1), default)).CharacterBag[0];
        InventorySlotRecord[] additions = [Pet(1) with { ItemId = 301002, SlotKey = 2, Flags = 0 },
            new(1, InventoryBag.CharacterBag, 3, 100643, 2, 0)];
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, additions, default);
        await store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag,
            additions.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var result = operation switch
        {
            "ordinary" => await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], 100643, true, default, Command(1)),
            "article" => await store.SelectPetAsync(new(1, Scope, [], new(301001, expected.PetIdentity!), false, Now)
                { Initialization = Command(1) }, default),
            _ => await store.MutatePetSnapshotAsync(new(expected, Scope, Initial with { Experience = 1 }, 100643, true, Now)
                { Initialization = Command(1) }, default),
        };
        Assert.NotNull(result);
        Assert.Equal(3, result.CharacterBag.Count);
        Assert.Equal(3, result.EquippedBag.Count);
        Assert.Equal(expected.PetIdentity, result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        foreach (var pet in result.CharacterBag.Where(row => row.ItemId is 301001 or 301002))
        {
            Assert.NotNull(pet.PetIdentity);
            Assert.NotNull(pet.Pet!.Find(Scope.Key));
            var mirror = result.EquippedBag.Single(row => row.ItemId == pet.ItemId);
            Assert.Equal(pet.PetIdentity, mirror.PetIdentity);
            Assert.Equal(pet.Pet, mirror.Pet);
            Assert.Equal(pet.Flags & 1, mirror.Flags & 1);
        }
        Assert.Equal(2UL, table.LastPetEntitlementId);
        if (operation == "ordinary")
        {
            Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
                row => Assert.Equal(1, row.Flags & 1));
            await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], 100643, false, default, Command(1));
            Assert.Equal(1, result.CharacterBag.Single(row => row.ItemId == 100643).Flags & 1);
        }
        if (operation == "mutation") Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
            row => Assert.Equal(1, row.Quantity));
    }

    /// <summary>Rejected selection or growth leaves newly observed pets and the shared allocator unchanged.</summary>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("article")]
    [InlineData("mutation")]
    public async Task RefusedMutationDiscardsOtherPetInitializationAndAllocatorChanges(string operation)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, true);
        var expected = (await store.InitializePetsAsync(Command(1), default)).CharacterBag[0];
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [Pet(1) with { ItemId = 301002, SlotKey = 2, Flags = 0 }], default);
        var before = await store.ReadPetInventoryAsync(1, Scope, default);
        var result = operation switch
        {
            "ordinary" => await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], 100643, true, default, Command(1)),
            "article" => await store.SelectPetAsync(new(1, Scope, [], new(301001, new(99, PetArticleKeys.First + 99)), true, Now)
                { Initialization = Command(1) }, default),
            _ => await store.MutatePetSnapshotAsync(new(expected with { Quantity = 2 }, Scope, Initial, 0, false, Now)
                { Initialization = Command(1) }, default),
        };
        Assert.Null(result);
        var after = await store.ReadPetInventoryAsync(1, Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
        Assert.Equal(1UL, table.LastPetEntitlementId);
        Assert.Equal(PetArticleKeys.First, table.LastPetArticleKey);
    }

    /// <summary>Preparing already initialized ownership neither locks the allocator nor rewrites the stored snapshot.</summary>
    [Fact]
    public async Task ExistingIdentityPreparationDoesNotAcquireAllocatorOrRewriteUnchangedState()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, true);
        var initialized = await store.InitializePetsAsync(Command(1), default);
        var entry = table.Find(1)!;
        var characterRows = entry.Bags[(int)InventoryBag.CharacterBag];
        var equippedRows = entry.Bags[(int)InventoryBag.EquippedBag];
        var loadout = entry.Loadouts[Scope.Key];
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (table.PetAllocatorGate)
            {
                held.SetResult();
                release.Wait();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? operation = null;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            operation = Task.Run(async () =>
            {
                var repeated = await store.InitializePetsAsync(Command(1), default);
                Assert.Equal(initialized.CharacterBag, repeated.CharacterBag);
                Assert.Same(characterRows, entry.Bags[(int)InventoryBag.CharacterBag]);
                Assert.Same(equippedRows, entry.Bags[(int)InventoryBag.EquippedBag]);
                Assert.Same(loadout, entry.Loadouts[Scope.Key]);
                var legacy = new InventoryLoadoutScope("kr-812", Scope.SupportedItemIds);
                var progressed = await store.InitializePetsAsync(Command(1) with { Scope = legacy }, default);
                Assert.Equal(initialized.CharacterBag[0].PetIdentity, progressed.CharacterBag[0].PetIdentity);
                Assert.Equal(Initial, progressed.CharacterBag[0].Pet!.Find(legacy.Key));
            });
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await holder;
            if (operation is not null) await operation;
        }
        Assert.Equal(1UL, table.LastPetEntitlementId);
        Assert.Equal(PetArticleKeys.First, table.LastPetArticleKey);
    }

    /// <summary>A large catalog allocates identities only for owned pets within the inventory limit.</summary>
    [Fact]
    public async Task InitializationCatalogCanExceedInventoryCapacityWithoutAllocatingUnownedItems()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, false);
        var catalog = Enumerable.Range(301001, 65536).ToArray();
        var command = new PetInitializationCommand(1, new("kr-1256", catalog), catalog, Initial, Now);
        var initialized = await store.InitializePetsAsync(command, default);
        Assert.Single(initialized.CharacterBag);
        Assert.Empty(initialized.EquippedBag);
        Assert.Equal(1UL, table.LastPetEntitlementId);
        Assert.Equal(PetArticleKeys.First, initialized.CharacterBag[0].PetIdentity!.ArticleKey);
    }

    /// <summary>Repository instances share one allocation per entitlement while retaining both bags and build progressions.</summary>
    [Fact]
    public async Task ConcurrentRepositoriesAllocateOncePerOwnerAndKeepBothMirrorsAndBuilds()
    {
        var table = new MemoryPlayerTable(2);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, true);
        await Seed(store, 2, true);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(async () =>
            await new MemoryInventoryRepository(table).InitializePetsAsync(Command(index % 2 + 1), default))));
        var identities = results.Select(snapshot => Assert.Single(snapshot.CharacterBag).PetIdentity!).Distinct().ToArray();
        Assert.Equal(2, identities.Length);
        Assert.Equal(PetArticleKeys.First, identities.Min(identity => identity.ArticleKey));
        Assert.Equal(100000000UL, identities.Min(identity => identity.ArticleKey));
        Assert.Equal(PetArticleKeys.First + 1, identities.Max(identity => identity.ArticleKey));
        Assert.All(results, snapshot => Assert.Equal(Assert.Single(snapshot.CharacterBag).PetIdentity,
            Assert.Single(snapshot.EquippedBag).PetIdentity));
        var old = (await store.ReadPetInventoryAsync(1, Scope, default)).CharacterBag[0];
        var legacy = new InventoryLoadoutScope("kr-812", Scope.SupportedItemIds);
        var other = await store.InitializePetsAsync(Command(1) with { Scope = legacy, InitialProgression = Initial with { Experience = 17 } }, default);
        Assert.Equal(old.PetIdentity, other.CharacterBag[0].PetIdentity);
        Assert.Equal(Initial, other.CharacterBag[0].Pet!.Find(Scope.Key));
        Assert.Equal(17, other.CharacterBag[0].Pet!.Find(legacy.Key)!.Experience);
        Assert.Equal(other.CharacterBag[0].Pet, other.EquippedBag[0].Pet);
        var repeated = await store.InitializePetsAsync(Command(1) with { InitialProgression = Initial with { Experience = 99 } }, default);
        Assert.Equal(Initial, repeated.CharacterBag[0].Pet!.Find(Scope.Key));
    }

    /// <summary>Removing and granting a pet retires its old Article even when a stale bag mirror is written back.</summary>
    [Fact]
    public async Task RegrantCannotRecoverAnOldArticleOrItsBuildSelectionFromAStaleMirror()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        await Seed(store, 1, true);
        await store.UpdateLoadoutSelectionAsync(1, Scope, Groups, null, false, default);
        var original = (await store.InitializePetsAsync(Command(1), default)).CharacterBag[0];
        Assert.True((original.Flags & 1) != 0);
        var oldTarget = new PetEquipmentTarget(original.ItemId, original.PetIdentity!);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [], default);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Pet(1)], default);
        var replacement = await store.InitializePetsAsync(Command(1), default);
        var current = replacement.CharacterBag[0];
        Assert.NotEqual(original.PetIdentity, current.PetIdentity);
        Assert.True(current.PetIdentity!.ArticleKey > original.PetIdentity!.ArticleKey);
        Assert.Equal(current.PetIdentity, replacement.EquippedBag[0].PetIdentity);
        Assert.Equal(0, current.Flags & 1);
        Assert.Null(await store.SelectPetAsync(new(1, Scope, Groups, oldTarget, true, Now), default));
        var selected = await store.SelectPetAsync(new(1, Scope, Groups, new(current.ItemId, current.PetIdentity), true, Now), default);
        Assert.NotNull(selected);
        Assert.All(selected.CharacterBag.Concat(selected.EquippedBag), row => Assert.True((row.Flags & 1) != 0));
        var loadout = await store.GetLoadoutAsync(1, Scope, default);
        Assert.NotNull(loadout.WornPetEntitlementIds);
        Assert.Equal([current.PetIdentity.EntitlementId], loadout.WornPetEntitlementIds);
        Assert.Null(await store.MutatePetSnapshotAsync(new(original, Scope, Initial with { Experience = 1 }, 0, false, Now), default));
    }

    /// <summary>Untrusted inventory writes cannot mint, duplicate or revive a pet entitlement.</summary>
    [Fact]
    public async Task ExternalWritesCannotManufactureCloneOrResurrectIdentity()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(2));
        await Seed(store, 1, false);
        var row = (await store.InitializePetsAsync(Command(1), default)).CharacterBag[0];
        Assert.Empty((await store.ReadPetInventoryAsync(1, Scope, default)).EquippedBag);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(2, InventoryBag.CharacterBag,
            [row with { UserNum = 2 }], default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [row with { ItemId = 301002, SlotKey = 2 }], default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            [row with { PetIdentity = null }], default).AsTask());
        Assert.Single(await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [Pet(1)], default));
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [], default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [row], default).AsTask());
    }

    /// <summary>Allocator exhaustion rolls back pending identities and never reuses the last committed addressable key.</summary>
    [Fact]
    public async Task ExhaustionRollsBackTheWholeInitializationAndDoesNotReuseACommittedLastKey()
    {
        var table = new MemoryPlayerTable(1) { LastPetArticleKey = PetArticleKeys.Last - 1 };
        var store = new MemoryInventoryRepository(table);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Pet(1), Pet(1) with { ItemId = 301002, SlotKey = 2 }], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializePetsAsync(Command(1), default).AsTask());
        Assert.Equal(0UL, table.LastPetEntitlementId);
        Assert.Equal(PetArticleKeys.Last - 1, table.LastPetArticleKey);
        Assert.All(await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default), row => Assert.Null(row.PetIdentity));
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Pet(1)], default);
        var last = await store.InitializePetsAsync(Command(1), default);
        Assert.Equal(PetArticleKeys.Last, last.CharacterBag[0].PetIdentity!.ArticleKey);
        Assert.Equal(9223372036854775807UL, last.CharacterBag[0].PetIdentity!.ArticleKey);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [], default);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Pet(1)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializePetsAsync(Command(1), default).AsTask());
        Assert.Equal(PetArticleKeys.Last, table.LastPetArticleKey);
    }

    /// <summary>Conflicting bag copies and expired pets fail before selection or progression can change.</summary>
    [Fact]
    public async Task ConflictingMirrorsAndExpiredTargetsCannotChangeSelectionOrProgression()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store, 1, true);
        var row = (await store.InitializePetsAsync(Command(1), default)).CharacterBag[0];
        table.Find(1)!.Bags[(int)InventoryBag.EquippedBag][0] = row with
        { Bag = InventoryBag.EquippedBag, PetIdentity = new(55, PetArticleKeys.First + 55) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializePetsAsync(Command(1), default).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.MutatePetSnapshotAsync(new(row, Scope,
            Initial with { Experience = 1 }, 0, false, Now), default).AsTask());
        table.Find(1)!.Bags[(int)InventoryBag.EquippedBag].Clear();
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [row with { Lifetime = InventoryLifetime.Until(Now) }], default);
        Assert.Null(await store.SelectPetAsync(new(1, Scope, Groups, new(row.ItemId, row.PetIdentity!), true, Now), default));
    }

    /// <summary>Contract snapshots detach mutable vectors while retaining all unsigned identity bits.</summary>
    [Fact]
    public void ContractsCopyVectorsAndPreserveFullUnsignedIdentities()
    {
        var identity = new PetEntitlementIdentity(ulong.MaxValue, ulong.MaxValue);
        identity.Validate();
        var row = Pet(1) with { PetIdentity = identity };
        var rows = new[] { row };
        var snapshot = new PetInventorySnapshot(rows, []);
        rows[0] = Pet(1);
        Assert.Equal(identity, snapshot.CharacterBag[0].PetIdentity);
        Assert.Equal(identity, JsonSerializer.Deserialize<InventorySlotRecord>(JsonSerializer.Serialize(row))!.PetIdentity);
        var ids = new[] { 301001 };
        var command = new PetInitializationCommand(1, Scope, ids, Initial, Now);
        ids[0] = 301002;
        Assert.Equal(301001, command.PetItemIds[0]);
        var selected = new[] { ulong.MaxValue };
        var copied = new InventoryLoadoutState([301001], 0, null) { WornPetEntitlementIds = selected }.Copy();
        selected[0] = 1;
        Assert.Equal(ulong.MaxValue, copied.WornPetEntitlementIds![0]);
        Assert.Throws<InvalidDataException>(() => new InventoryLoadoutState([], 0, null) { WornPetEntitlementIds = [0] }.Copy());
    }

    private static PetInitializationCommand Command(long user) => new(user, Scope, [301001, 301002], Initial, Now);
    private static InventorySlotRecord Pet(long user) => new(user, InventoryBag.CharacterBag, 1, 301001, 1, 0x81)
    { Lifetime = InventoryLifetime.Permanent };
    private static async Task Seed(MemoryInventoryRepository store, long user, bool mirror)
    {
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user)], default);
        if (mirror) await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [Pet(user) with { Bag = InventoryBag.EquippedBag }], default);
    }
}
