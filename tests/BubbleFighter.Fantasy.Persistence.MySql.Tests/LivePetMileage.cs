using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Database mileage rewards share the pet transaction and allocate fresh inventory keys without purchase receipts.</summary>
    [Fact]
    public async Task MileageConversionSharesThePetCommitAndAllocatesBeyondActiveKeysWithoutPurchaseAudit()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory);
        var now = DateTimeOffset.UtcNow;
        InventoryLoadoutScope scope = new("kr-1256", [301001, 100643, 103385, 200777]);
        PetProgression initial = new(0, 0, now.AddHours(1), null, null, null);
        InventorySlotRecord[] rows = [new(user, InventoryBag.CharacterBag, 1, 301001, 1, 0x81) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, 100643, 2, 0x80) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 9000, 103385, 8, 0x81) { Lifetime = InventoryLifetime.Permanent }];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        PetInitializationCommand initialization = new(user, scope, [301001], initial, now);
        await store.InitializePetsAsync(initialization, default);
        await store.UpdateLoadoutSelectionSnapshotAsync(user, scope, [], 103385, false, default);
        var before = await store.ReadPetInventoryAsync(user, scope, default);
        PetMutationCommand command = new(before.CharacterBag.Single(row => row.ItemId == 301001), scope,
            initial with { Experience = 1 }, 100643, true, now)
        {
            Initialization = initialization,
            ArticleInitialization = new(user, scope, [200777], now),
            Mileage = new(103385, before.CharacterBag.Single(row => row.ItemId == 103385), 5, 100, 1000,
                new(2, 2, 5, false, 3, [new(200777, 2) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }])),
        };
        var invalid = command with { Mileage = command.Mileage! with
            { Conversion = command.Mileage!.Conversion! with { BalanceAfterConversion = 2 } } };
        var keyBefore = await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", user));
        Assert.Null(await store.MutatePetWithResultAsync(invalid, default));
        Assert.Equal(before.CharacterBag, (await store.ReadPetInventoryAsync(user, scope, default)).CharacterBag);
        Assert.Equal(keyBefore, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", user)));
        var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(command, default));
        Assert.Equal(new[] { 200777 }, result.RewardItemIds);
        Assert.NotNull(result.Snapshot.CharacterBag.Single(row => row.ItemId == 200777).ArticleIdentity);
        Assert.Equal(9001, result.Snapshot.CharacterBag.Single(row => row.ItemId == 200777).SlotKey);
        Assert.Equal(9001, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", user)));
        Assert.Equal(3, result.Snapshot.CharacterBag.Single(row => row.ItemId == 103385).Quantity);
        Assert.Equal(0x80, result.Snapshot.CharacterBag.Single(row => row.ItemId == 103385).Flags);
        Assert.Equal(0x81, (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == 103385).Flags);
        Assert.Equal(1, result.Snapshot.CharacterBag.Single(row => row.ItemId == 301001).Pet!.Find(scope.Key)!.Experience);
        Assert.All(result.Snapshot.CharacterBag.Concat(result.Snapshot.EquippedBag).Where(row => row.ItemId == 100643),
            row => Assert.Equal(1, row.Quantity));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM purchase_log WHERE user_num=@u", ("@u", user)));
        Assert.Null(await store.MutatePetWithResultAsync(command, default));
        var reopened = await new MySqlInventoryRepository(factory).ReadPetInventoryAsync(user, scope, default);
        Assert.Equal(result.Snapshot.CharacterBag, reopened.CharacterBag);
        Assert.Equal(result.Snapshot.EquippedBag, reopened.EquippedBag);
    }
}
