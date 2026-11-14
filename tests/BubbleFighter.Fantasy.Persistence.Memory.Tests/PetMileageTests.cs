using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks atomic mileage conversion, ordinary rewards and rollback for memory persistence.</summary>
public sealed class PetMileageTests
{
    private const int Pet = 301001, OtherPet = 301002, Food = 100643, Meter = 103385, Reward = 200777;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Pet, OtherPet, Food, Meter, Reward]);
    private static readonly PetProgression Initial = new(0, 0, Now.AddHours(1), null, null, null);

    /// <summary>Final mileage rows receive equipment Articles without invalidating their own expected grant, and refusals allocate nothing.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RewardArticlesAndLatePetsCommitOnlyWithTheMutation(bool existingReward, bool stale)
    {
        var (table, store) = await Seed(existingReward);
        var before = await store.ReadPetInventoryAsync(1, Scope, default);
        var late = new InventorySlotRecord(1, InventoryBag.CharacterBag, 9, OtherPet, 1, 0) { Lifetime = InventoryLifetime.Permanent };
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [late], default);
        await store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag, [late with { Bag = InventoryBag.EquippedBag }], default);
        var command = Command(before) with { ArticleInitialization = new(1, Scope, [Reward], Now) };
        if (stale) command = command with { ExpectedSlot = command.ExpectedSlot with { SlotKey = 999 } };
        var result = await store.MutatePetWithResultAsync(command, default);
        var saved = await store.ReadPetInventoryAsync(1, Scope, default);
        if (stale)
        {
            Assert.Null(result);
            Assert.Equal(1UL, table.LastPetEntitlementId);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == OtherPet).PetIdentity);
            Assert.Equal(2, saved.CharacterBag.Single(row => row.ItemId == Food).Quantity);
            Assert.All(saved.CharacterBag.Where(row => row.ItemId == Reward), row => Assert.Null(row.ArticleIdentity));
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(3UL, table.LastPetEntitlementId);
            Assert.NotNull(result.Snapshot.CharacterBag.Single(row => row.ItemId == OtherPet).PetIdentity);
            var reward = result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward);
            Assert.NotNull(reward.ArticleIdentity);
            Assert.Equal(existingReward ? 6 : 2, reward.Quantity);
            Assert.All(result.Snapshot.EquippedBag.Where(row => row.ItemId == Reward), row => Assert.Equal(reward.ArticleIdentity, row.ArticleIdentity));
            Assert.Equal(result.Snapshot.CharacterBag, saved.CharacterBag);
            Assert.Null(await store.MutatePetWithResultAsync(command, default));
            Assert.Equal(3UL, table.LastPetEntitlementId);
        }
    }

    /// <summary>Mileage conversion commits food, progression and ordinary rewards together without overwriting stored flags.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionCommitsFoodGrowthAndOrdinaryRewardsWhilePreservingStoredFlags(bool existingReward)
    {
        var (table, store) = await Seed(existingReward);
        table.Find(1)!.LastPurchaseSlotKey = 5000;
        var before = await store.ReadPetInventoryAsync(1, Scope, default);
        var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(Command(before), default));
        Assert.Equal(new[] { Reward }, result.RewardItemIds);
        var pet = result.Snapshot.CharacterBag.Single(row => row.ItemId == Pet);
        Assert.Equal(before.CharacterBag.Single(row => row.ItemId == Pet).PetIdentity, pet.PetIdentity);
        Assert.Equal(1, pet.Pet!.Find(Scope.Key)!.Experience);
        Assert.All(result.Snapshot.CharacterBag.Concat(result.Snapshot.EquippedBag).Where(row => row.ItemId == Meter),
            row => { Assert.Equal(3, row.Quantity); Assert.Equal(0x80, row.Flags); });
        Assert.All(result.Snapshot.CharacterBag.Concat(result.Snapshot.EquippedBag).Where(row => row.ItemId == Food),
            row => Assert.Equal(1, row.Quantity));
        var reward = result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward);
        Assert.Equal(existingReward ? 6 : 2, reward.Quantity);
        Assert.Equal(existingReward ? 4 : 5001, reward.SlotKey);
        var raw = await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        Assert.Equal(0x81, raw.Single(row => row.ItemId == Meter).Flags);
        if (existingReward)
        {
            Assert.Equal(0x81, raw.Single(row => row.ItemId == Reward).Flags);
            Assert.Equal(0x80, reward.Flags);
            Assert.Equal(6, result.Snapshot.EquippedBag.Single(row => row.ItemId == Reward).Quantity);
        }
        else Assert.DoesNotContain(result.Snapshot.EquippedBag, row => row.ItemId == Reward);
        Assert.Empty(table.Find(1)!.Purchases);
        Assert.Equal(existingReward ? 5000 : 5001, table.Find(1)!.LastPurchaseSlotKey);
    }

    /// <summary>A stale shared mileage balance rejects another build or pet until the caller replans.</summary>
    [Fact]
    public async Task SharedBalanceConflictsAcrossDifferentPetsAndBuildsUntilReplanned()
    {
        var (_, store) = await Seed(existingReward: false, otherPet: true);
        var older = new InventoryLoadoutScope("cn-774", Scope.SupportedItemIds);
        await store.InitializePetsAsync(Initialization(older), default);
        var first = await store.ReadPetInventoryAsync(1, Scope, default);
        var second = await store.ReadPetInventoryAsync(1, older, default);
        PetMutationCommand Gain(PetInventorySnapshot snapshot, InventoryLoadoutScope scope, int pet) =>
            new(snapshot.CharacterBag.Single(row => row.ItemId == pet), scope, Initial with { Experience = 1 }, 0, false, Now)
            {
                Initialization = Initialization(scope),
                Mileage = new(Meter, snapshot.CharacterBag.Single(row => row.ItemId == Meter), 1, 100, 1000, null),
            };
        Assert.NotNull(await store.MutatePetWithResultAsync(Gain(first, Scope, Pet), default));
        Assert.Null(await store.MutatePetWithResultAsync(Gain(second, older, OtherPet), default));
        var retry = await store.ReadPetInventoryAsync(1, older, default);
        var committed = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(Gain(retry, older, OtherPet), default));
        Assert.Empty(committed.RewardItemIds);
        Assert.Equal(10, committed.Snapshot.CharacterBag.Single(row => row.ItemId == Meter).Quantity);
        Assert.Equal(0, committed.Snapshot.CharacterBag.Single(row => row.ItemId == OtherPet).Pet!.Find(Scope.Key)!.Experience);
        Assert.Equal(1, committed.Snapshot.CharacterBag.Single(row => row.ItemId == OtherPet).Pet!.Find(older.Key)!.Experience);
    }

    /// <summary>Mileage rewards accept the same renewal and upgrade plans as ordinary item grants.</summary>
    [Theory]
    [InlineData(InventoryGrantKind.Days)]
    [InlineData(InventoryGrantKind.Life)]
    [InlineData(InventoryGrantKind.Permanent)]
    public async Task ConversionAcceptsTheSharedOrdinaryGrantPlannersRenewalAndUpgradeResults(InventoryGrantKind kind)
    {
        var (_, store) = await Seed(existingReward: true);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(1, bag, 512, default);
            await store.ReplaceSlotsAsync(1, bag, rows.Select(row => row.ItemId != Reward ? row : row with
            {
                Quantity = kind == InventoryGrantKind.Life ? 0 : 1,
                Lifetime = kind == InventoryGrantKind.Life ? InventoryLifetime.Life : InventoryLifetime.Until(Now.AddDays(2)),
            }).ToArray(), default);
        }
        var before = await store.ReadPetInventoryAsync(1, Scope, default);
        var grants = InventoryGrantPlanner.Plan(1, [new(Reward, kind, kind == InventoryGrantKind.Permanent ? 1 : 3, 10, false)],
            before.CharacterBag, Now);
        var command = Command(before);
        command = command with { Mileage = command.Mileage! with { Conversion = new(2, 2, 5, false, 3, grants) } };
        var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(command, default));
        var expected = Assert.Single(grants);
        Assert.All(result.Snapshot.CharacterBag.Concat(result.Snapshot.EquippedBag).Where(row => row.ItemId == Reward), row =>
        {
            Assert.Equal(expected.Quantity, row.Quantity);
            Assert.Equal(expected.Lifetime, row.Lifetime);
            Assert.Equal(4, row.SlotKey);
        });
        Assert.Equal(new[] { Reward }, result.RewardItemIds);
    }

    /// <summary>Rejected conversions preserve food, progression, mileage, rewards and the inventory key watermark.</summary>
    [Theory]
    [InlineData("stale-balance")]
    [InlineData("stale-reward")]
    [InlineData("gain-overflow")]
    [InlineData("balance-limit")]
    [InlineData("cost-overflow")]
    [InlineData("unaffordable")]
    [InlineData("remainder")]
    [InlineData("expired-reward")]
    [InlineData("lifetime-reward")]
    [InlineData("key-overflow")]
    [InlineData("capacity")]
    public async Task RefusalRollsBackMileageFoodProgressionGrantsAndKeyWatermark(string failure)
    {
        var newReward = failure is "key-overflow" or "capacity";
        var (table, store) = await Seed(existingReward: !newReward);
        if (failure == "key-overflow") table.Find(1)!.LastPurchaseSlotKey = long.MaxValue;
        if (failure == "capacity")
            await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
                Enumerable.Range(4, InventoryLimits.MaxInventorySlotsPerBag - 3)
                    .Select(index => new InventorySlotRecord(1, InventoryBag.CharacterBag, index, 400000 + index, 1, 0)).ToArray(), default);
        var snapshot = await store.ReadPetInventoryAsync(1, Scope, default);
        var command = Command(snapshot);
        var effect = command.Mileage!;
        var conversion = effect.Conversion!;
        command = failure switch
        {
            "stale-balance" => command with { Mileage = effect with { ExpectedSlot = effect.ExpectedSlot! with { Quantity = 9 } } },
            "stale-reward" => command with { Mileage = effect with { Conversion = Conversion(conversion.Rewards[0] with
                { ExpectedSlot = conversion.Rewards[0].ExpectedSlot! with { Quantity = 5 } }) } },
            "gain-overflow" => command with { Mileage = effect with { Gain = int.MaxValue } },
            "balance-limit" => command with { Mileage = effect with { MaximumBalance = 10, Conversion = null } },
            "cost-overflow" => command with { Mileage = effect with { Conversion = conversion with
                { Count = int.MaxValue, MaximumCount = int.MaxValue, CostPerConversion = 2 } } },
            "unaffordable" => command with { Mileage = effect with { Conversion = conversion with { Count = 3, MaximumCount = 3 } } },
            "remainder" => command with { Mileage = effect with { Conversion = conversion with { BalanceAfterConversion = 2 } } },
            "expired-reward" => command with { Mileage = effect with { Conversion = Conversion(conversion.Rewards[0] with
                { Quantity = 1, Lifetime = InventoryLifetime.Until(Now) }) } },
            "lifetime-reward" => command with { Mileage = effect with { Conversion = Conversion(conversion.Rewards[0] with
                { Lifetime = InventoryLifetime.Life }) } },
            _ => command,
        };
        var rawBefore = await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        var mirrorBefore = await store.ListSlotsAsync(1, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        var lastKey = table.Find(1)!.LastPurchaseSlotKey;
        Assert.Null(await store.MutatePetWithResultAsync(command, default));
        Assert.Equal(rawBefore, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        Assert.Equal(mirrorBefore, await store.ListSlotsAsync(1, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        Assert.Equal(lastKey, table.Find(1)!.LastPurchaseSlotKey);
        Assert.Empty(table.Find(1)!.Purchases);
    }

    /// <summary>Explicit remainder discard is honored and a missing balance is initialized only by the supplied plan.</summary>
    [Fact]
    public async Task ExplicitDiscardAndMissingBalanceAreAppliedWithoutInventingConversionRules()
    {
        var (table, store) = await Seed(existingReward: false);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(1, bag, InventoryLimits.MaxInventorySlotsPerBag, default);
            await store.ReplaceSlotsAsync(1, bag, rows.Where(row => row.ItemId != Meter).ToArray(), default);
        }
        var snapshot = await store.ReadPetInventoryAsync(1, Scope, default);
        var command = Command(snapshot);
        command = command with { Mileage = command.Mileage! with
        {
            Gain = 13,
            Conversion = command.Mileage!.Conversion! with { DiscardRemainder = true, BalanceAfterConversion = 0 },
        } };
        var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(command, default));
        Assert.DoesNotContain(result.Snapshot.CharacterBag, row => row.ItemId == Meter);
        Assert.Equal(1000, result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward).SlotKey);
        Assert.Equal(1000, table.Find(1)!.LastPurchaseSlotKey);
    }

    /// <summary>Mileage effects reject conflicting grants, pet rewards and invalid vectors before mutation.</summary>
    [Fact]
    public async Task EffectValidationRejectsOverlapsPetRewardsAndUnboundedOrMutableVectors()
    {
        var (_, store) = await Seed(existingReward: true);
        var command = Command(await store.ReadPetInventoryAsync(1, Scope, default));
        var effect = command.Mileage!;
        var reward = effect.Conversion!.Rewards[0];
        Assert.Throws<ArgumentException>(() => (command with { Mileage = effect with { ItemId = Food } }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Mileage = effect with
            { Conversion = Conversion(reward with { ItemId = OtherPet, ExpectedSlot = null }) } }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Mileage = effect with
            { Conversion = Conversion(reward with { Mode = ShopGrantMode.AppendInstance }) } }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Mileage = effect with
            { Conversion = new(2, 2, 5, false, 3, [reward, reward]) } }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Initialization = null }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Mileage = effect with
            { Conversion = effect.Conversion! with { MaximumCount = 1 } } }).Validate());
        var mutable = new[] { reward };
        Assert.Throws<ArgumentException>(() => new PetMileageConversion(2, 2, 5, false, 3,
            Enumerable.Repeat(reward, InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray()));
        var copied = new PetMileageConversion(2, 2, 5, false, 3, mutable);
        mutable[0] = reward with { Quantity = 99 };
        Assert.Equal(reward, copied.Rewards[0]);
        var ids = new[] { Reward };
        var result = new PetMutationResult(new([], []), ids);
        ids[0] = Food;
        Assert.Equal(Reward, result.RewardItemIds[0]);
    }

    private static PetMileageConversion Conversion(ShopPurchaseGrant reward) => new(2, 2, 5, false, 3, [reward]);

    private static PetMutationCommand Command(PetInventorySnapshot snapshot)
    {
        var reward = snapshot.CharacterBag.SingleOrDefault(row => row.ItemId == Reward);
        return new(snapshot.CharacterBag.Single(row => row.ItemId == Pet), Scope, Initial with { Experience = 1 }, Food, true, Now)
        {
            Initialization = Initialization(Scope),
            Mileage = new(Meter, snapshot.CharacterBag.SingleOrDefault(row => row.ItemId == Meter), 5, 100, 1000,
                Conversion(new(Reward, (reward?.Quantity ?? 0) + 2)
                    { Mode = ShopGrantMode.MatchItem, ExpectedSlot = reward, Lifetime = InventoryLifetime.Permanent })),
        };
    }

    private static PetInitializationCommand Initialization(InventoryLoadoutScope scope) => new(1, scope, [Pet, OtherPet], Initial, Now);

    private static async Task<(MemoryPlayerTable Table, MemoryInventoryRepository Store)> Seed(bool existingReward, bool otherPet = false)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        List<InventorySlotRecord> rows = [new(1, InventoryBag.CharacterBag, 1, Pet, 1, 0x81) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, Food, 2, 0x80) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 3, Meter, 8, 0x81) { Lifetime = InventoryLifetime.Permanent }];
        if (existingReward) rows.Add(new(1, InventoryBag.CharacterBag, 4, Reward, 4, 0x81) { Lifetime = InventoryLifetime.Permanent });
        if (otherPet) rows.Add(new(1, InventoryBag.CharacterBag, 5, OtherPet, 1, 0) { Lifetime = InventoryLifetime.Permanent });
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await store.InitializePetsAsync(Initialization(Scope), default);
        await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], Meter, false, default);
        if (existingReward) await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], Reward, false, default);
        return (table, store);
    }
}
