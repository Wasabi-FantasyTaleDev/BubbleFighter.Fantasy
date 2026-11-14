using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks mileage over real starter markers while retaining fresh allocation and exact item preconditions.</summary>
public sealed class PetMileageMarkerTests
{
    private const int Pet = 301001, Food = 100643, Meter = 103385, Reward = 200777;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>New balance and reward keys exceed both raw bags and the durable watermark despite repeated starter markers.</summary>
    [Theory]
    [InlineData("character")]
    [InlineData("mirror")]
    [InlineData("history")]
    public async Task StarterMarkersAllowMileageWithoutReusingAnyAllocationFloor(string highestSource)
    {
        var setup = await SeedAsync(highestSource, includeBalance: false);
        var rawBefore = await RawAsync(setup.Store);
        Assert.All(rawBefore.GroupBy(row => row.Bag), bag => Assert.Contains(
            bag.Where(row => StarterKit.Default.ItemIds.Contains(row.ItemId)).GroupBy(row => row.SlotKey), group => group.Count() > 1));
        var snapshot = await setup.Store.ReadPetInventoryAsync(1, setup.Initialization.Scope, default);
        Assert.DoesNotContain(snapshot.CharacterBag.Concat(snapshot.EquippedBag), row => row.ItemId is 800001 or 800002);
        var first = Assert.IsType<PetMutationResult>(await setup.Store.MutatePetWithResultAsync(
            Command(snapshot, setup.Initialization, Reward, gain: 2), default));
        var meter = Assert.Single(first.Snapshot.CharacterBag, row => row.ItemId == Meter);
        var reward = Assert.Single(first.Snapshot.CharacterBag, row => row.ItemId == Reward);
        Assert.Equal(setup.Highest + 1, meter.SlotKey);
        Assert.Equal(setup.Highest + 2, reward.SlotKey);
        Assert.DoesNotContain(meter.SlotKey, rawBefore.Select(row => row.SlotKey));
        Assert.DoesNotContain(reward.SlotKey, rawBefore.Select(row => row.SlotKey));
        Assert.Equal(setup.Highest + 2, setup.Table.Find(1)!.LastPurchaseSlotKey);
        Assert.Equal(new[] { Reward }, first.RewardItemIds);
        Assert.All(first.Snapshot.CharacterBag.Concat(first.Snapshot.EquippedBag).Where(row => row.ItemId == Food),
            row => Assert.Equal(2, row.Quantity));

        Assert.Equal(0, await setup.Store.ConsumeItemAsync(1, Reward, default));
        var current = await setup.Store.ReadPetInventoryAsync(1, setup.Initialization.Scope, default);
        var second = Assert.IsType<PetMutationResult>(await setup.Store.MutatePetWithResultAsync(
            Command(current, setup.Initialization, Reward, gain: 2), default));
        Assert.Equal(setup.Highest + 3, Assert.Single(second.Snapshot.CharacterBag, row => row.ItemId == Reward).SlotKey);
        Assert.Equal(meter.SlotKey, Assert.Single(second.Snapshot.CharacterBag, row => row.ItemId == Meter).SlotKey);
        Assert.Equal(setup.Highest + 3, setup.Table.Find(1)!.LastPurchaseSlotKey);
        Assert.Empty(setup.Table.Find(1)!.Purchases);
        Assert.Equal(rawBefore.Where(row => StarterKit.Default.ItemIds.Contains(row.ItemId)),
            (await RawAsync(setup.Store)).Where(row => StarterKit.Default.ItemIds.Contains(row.ItemId)));
    }

    /// <summary>A conditional reward updates only its catalog item when many starter items share that item's stored marker.</summary>
    [Fact]
    public async Task RewardUpdatesTheMatchingStarterItemWithoutChangingOtherRowsWithItsMarker()
    {
        const int starterReward = 201003;
        var setup = await SeedAsync("history", includeBalance: true);
        var rawBefore = await RawAsync(setup.Store);
        var snapshot = await setup.Store.ReadPetInventoryAsync(1, setup.Initialization.Scope, default);
        var before = Assert.Single(snapshot.CharacterBag, row => row.ItemId == starterReward);
        var result = Assert.IsType<PetMutationResult>(await setup.Store.MutatePetWithResultAsync(
            Command(snapshot, setup.Initialization, starterReward, gain: 1), default));
        var rawAfter = await RawAsync(setup.Store);
        Assert.All(rawAfter.Where(row => row.ItemId == starterReward), row =>
        {
            var original = Assert.Single(rawBefore, candidate => candidate.Bag == row.Bag && candidate.ItemId == starterReward);
            Assert.Equal(original with { Quantity = before.Quantity + 1 }, row);
        });
        Assert.Equal(rawBefore.Where(row => StarterKit.Default.ItemIds.Contains(row.ItemId) && row.ItemId != starterReward),
            rawAfter.Where(row => StarterKit.Default.ItemIds.Contains(row.ItemId) && row.ItemId != starterReward));
        Assert.Equal(setup.Highest, setup.Table.Find(1)!.LastPurchaseSlotKey);
        Assert.Equal(new[] { starterReward }, result.RewardItemIds);
    }

    /// <summary>Allowing repeated native markers does not relax the exact single-item reward precondition.</summary>
    [Fact]
    public async Task DuplicateRewardItemsStillRefuseTheEntireMileageMutation()
    {
        const int starterReward = 201003;
        var setup = await SeedAsync("history", includeBalance: true);
        var snapshot = await setup.Store.ReadPetInventoryAsync(1, setup.Initialization.Scope, default);
        var command = Command(snapshot, setup.Initialization, starterReward, gain: 1);
        var bag = await setup.Store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
        var original = Assert.Single(bag, row => row.ItemId == starterReward);
        await setup.Store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            [.. bag, original with { SlotKey = setup.Highest + 1 }], default);
        var rawBefore = await RawAsync(setup.Store);
        Assert.Null(await setup.Store.MutatePetWithResultAsync(command, default));
        Assert.Equal(rawBefore, await RawAsync(setup.Store));
        Assert.Equal(setup.Highest, setup.Table.Find(1)!.LastPurchaseSlotKey);
        Assert.Empty(setup.Table.Find(1)!.Purchases);
    }

    private static PetMutationCommand Command(PetInventorySnapshot snapshot, PetInitializationCommand initialization,
        int reward, int gain)
    {
        var pet = snapshot.CharacterBag.Single(row => row.ItemId == Pet);
        var progress = pet.Pet!.Find(initialization.Scope.Key)!;
        var meter = snapshot.CharacterBag.SingleOrDefault(row => row.ItemId == Meter);
        var grants = InventoryGrantPlanner.Plan(1, [new(reward, InventoryGrantKind.Count, 1, 200000, false)], snapshot.CharacterBag, Now);
        return new(pet, initialization.Scope, progress with { Experience = progress.Experience + 1 }, Food, true, Now)
        {
            Initialization = initialization,
            Mileage = new(Meter, meter, gain, 100, 1,
                new(1, 1, 1, false, (meter?.Quantity ?? 0) + gain - 1, grants)),
        };
    }

    private static async Task<(MemoryPlayerTable Table, MemoryInventoryRepository Store,
        PetInitializationCommand Initialization, long Highest)> SeedAsync(string highestSource, bool includeBalance)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        var character = StarterKit.Default.Slots(1, InventoryBag.CharacterBag, Now).ToList();
        var equipped = StarterKit.Default.Slots(1, InventoryBag.EquippedBag, Now).ToList();
        var marker = Math.Max(character.Max(row => row.SlotKey), equipped.Max(row => row.SlotKey));
        var characterKey = marker + (highestSource == "character" ? 300 : 100);
        var mirrorKey = marker + (highestSource == "mirror" ? 300 : 200);
        var historyKey = marker + (highestSource == "history" ? 300 : 50);
        InventorySlotRecord[] additions = [new(1, InventoryBag.CharacterBag, 1, Pet, 1, 1) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, Food, 3, 0) { Lifetime = InventoryLifetime.Permanent }];
        character.AddRange(additions);
        equipped.AddRange(additions.Select(row => row with { Bag = InventoryBag.EquippedBag }));
        if (includeBalance)
        {
            InventorySlotRecord meter = new(1, InventoryBag.CharacterBag, 3, Meter, 0, 0) { Lifetime = InventoryLifetime.Permanent };
            character.Add(meter);
            equipped.Add(meter with { Bag = InventoryBag.EquippedBag });
        }
        character.Add(new(1, InventoryBag.CharacterBag, characterKey, 800001, 1, 0));
        equipped.Add(new(1, InventoryBag.EquippedBag, mirrorKey, 800002, 1, 0));
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, character, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, equipped, default);
        InventoryLoadoutScope scope = new("kr-1256", [.. StarterKit.Default.ItemIds, Pet, Food, Meter, Reward]);
        PetInitializationCommand initialization = new(1, scope, [Pet], new(0, 0, Now.AddHours(1), null, null, null), Now);
        await store.InitializePetsAsync(initialization, default);
        table.Find(1)!.LastPurchaseSlotKey = historyKey;
        return (table, store, initialization, Math.Max(historyKey, Math.Max(characterKey, mirrorKey)));
    }

    private static async Task<InventorySlotRecord[]> RawAsync(MemoryInventoryRepository store) =>
        [.. await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default),
            .. await store.ListSlotsAsync(1, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, default)];
}
