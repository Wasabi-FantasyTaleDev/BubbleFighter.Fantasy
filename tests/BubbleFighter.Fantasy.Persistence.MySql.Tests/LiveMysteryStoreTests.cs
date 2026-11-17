using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    [Fact]
    public async Task MysteryPurchaseSerializesAndSurvivesRepositoryReopen()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var scope = new InventoryLoadoutScope("kr-1256", [100385, .. Enumerable.Range(101, 6)]);
        const string revision = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var board = new MysteryStoreBoard(revision, 100385, 2, false,
            Enumerable.Range(0, 6).Select(i => new MysteryStoreOffer(2000001 + i, 3, 0, "",
                [new(101 + i, InventoryGrantKind.Count, 2, 99, false)])).ToArray());
        var repository = new MySqlInventoryRepository(factory);
        await repository.ReplaceSlotsAsync(user, InventoryBag.CharacterBag,
            [new(user, InventoryBag.CharacterBag, 1, 100385, 10, 0) { Lifetime = InventoryLifetime.Permanent }], default);
        IMysteryStore store = repository;
        var opened = await store.EnsureAsync(new(user, scope, board, 600), default);
        var purchase = new MysteryStorePurchaseCommand(new(user, scope), revision, opened.Selection!.Token, 2000001, 3);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => await store.PurchaseAsync(purchase, default)));
        Assert.Single(results, result => result.Outcome == MysteryStoreOutcome.Purchased);
        Assert.Single(results, result => result.Outcome == MysteryStoreOutcome.AlreadyPurchased);
        store = new MySqlInventoryRepository(factory);
        var replay = await store.PurchaseAsync(purchase, default);
        Assert.Equal(MysteryStoreOutcome.AlreadyPurchased, replay.Outcome);
        Assert.Equal(7, replay.Inventory!.CharacterBag.Single(row => row.ItemId == 100385).Quantity);
        Assert.Equal(2, replay.Inventory.CharacterBag.Single(row => row.ItemId == 101).Quantity);
        Assert.Equal(1u, replay.Selection!.PurchasedMask);
    }
}
