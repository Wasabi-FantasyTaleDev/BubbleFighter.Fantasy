using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    private static PetProgressions PetStates(PetProgression progression) => PetProgressions.Empty.With("kr-812", progression);

    /// <summary>Concurrent database purchase and feeding retain metadata in both persisted bag copies.</summary>
    [Fact]
    public async Task PetFeedingAndConcurrentPurchaseRoundTripBothBagsWithoutLosingMetadata()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var initial = new PetProgression(0, 95, now.AddHours(1), null, null, null);
        var grown = new PetProgression(1, 0, now.AddHours(8), now, new(1, 4, 0, 0, 0), null);
        var pet = new InventorySlotRecord(user, InventoryBag.CharacterBag, 10, 301001, 1, 0x81)
            { Lifetime = InventoryLifetime.Permanent, Pet = PetStates(initial) };
        var food = new InventorySlotRecord(user, InventoryBag.CharacterBag, 11, 100643, 2, 0x40);
        var hidden = new InventorySlotRecord(user, InventoryBag.CharacterBag, 12, 999001, 3, 0x21);
        var inventory = new MySqlInventoryRepository(factory);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [pet, food, hidden], default);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.EquippedBag,
            [pet with { Bag = InventoryBag.EquippedBag }, food with { Bag = InventoryBag.EquippedBag }, hidden with { Bag = InventoryBag.EquippedBag }], default);
        var command = new PetMutationCommand(pet, new("kr-812", [301001, 100643, 200001]), grown, 100643, true, now);
        var purchase = new ShopPurchaseCommand(new(user, 100, 0), 42, 0, "lucci", 10, 1000, [new(200001, 7)]);
        var shop = new MySqlShopPurchaseStore(factory);
        await Task.WhenAll(Task.Run(async () => Assert.NotNull(await inventory.MutatePetAsync(command, default))),
            Task.Run(async () => Assert.NotNull(await shop.PurchaseAsync(purchase, default))));
        inventory = new MySqlInventoryRepository(factory);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = await inventory.ListSlotsAsync(user, bag, 512, default);
            Assert.Equal(pet with { Bag = bag, Pet = PetStates(grown) }, saved.Single(row => row.ItemId == 301001));
            Assert.Equal(1, saved.Single(row => row.ItemId == 100643).Quantity);
            Assert.Equal(hidden with { Bag = bag }, saved.Single(row => row.ItemId == 999001));
        }
        Assert.Equal(7, (await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == 200001).Quantity);
        Assert.Null(await inventory.MutatePetAsync(command, default));
        var renewal = purchase with { Grants = [new(301001, 2) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = pet, Lifetime = InventoryLifetime.Permanent }] };
        await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => shop.PurchaseAsync(renewal, default).AsTask());
        renewal = renewal with { Grants = [renewal.Grants[0] with { ExpectedSlot = pet with { Pet = PetStates(grown) } }] };
        Assert.Equal(PetStates(grown), Assert.Single((await shop.PurchaseAsync(renewal, default))!.Grants).Pet);
        Assert.Equal(PetStates(grown), (await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == 301001).Pet);
        var refreshed = (await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == 301001);
        var modern = initial with { Experience = 17, LastFedAt = now };
        Assert.NotNull(await inventory.MutatePetAsync(command with
        {
            ExpectedSlot = refreshed, Scope = new("kr-1256", command.Scope.SupportedItemIds), Progression = modern,
        }, default));
        var reopened = new MySqlInventoryRepository(factory);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = (await reopened.ListSlotsAsync(user, bag, 512, default)).Single(row => row.ItemId == 301001);
            Assert.Equal(grown, saved.Pet!.Find("kr-812"));
            Assert.Equal(modern, saved.Pet.Find("kr-1256"));
        }
    }

    /// <summary>Database feeding refusals, cancellation and duplicate plans preserve transaction atomicity.</summary>
    [Fact]
    public async Task PetFoodRefusalCancellationAndConcurrentDuplicatePlansKeepAtomicState()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        var now = DateTimeOffset.UtcNow;
        var initial = new PetProgression(0, 0, now, null, null, null);
        var pet = new InventorySlotRecord(user, InventoryBag.CharacterBag, 10, 301001, 1, 1) { Pet = PetStates(initial) };
        var food = new InventorySlotRecord(user, InventoryBag.CharacterBag, 11, 100643, 1, 0);
        var next = initial with { Experience = 5, FullnessExpiresAt = now.AddHours(8), LastFedAt = now };
        var command = new PetMutationCommand(pet, new("kr-812", [301001, 100643]), next, 100643, true, now);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [pet], default);
        Assert.Null(await inventory.MutatePetAsync(command, default));
        Assert.Equal(pet, Assert.Single(await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)));
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [pet, food], default);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.EquippedBag,
            [pet with { Bag = InventoryBag.EquippedBag }, food with { Bag = InventoryBag.EquippedBag }], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.MutatePetAsync(command, new CancellationToken(true)).AsTask());
        Assert.Equal([pet, food], await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await inventory.MutatePetAsync(command, default))));
        Assert.Single(results, result => result is not null);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(PetStates(next), Assert.Single(await inventory.ListSlotsAsync(user, bag, 512, default)).Pet);
    }
}
