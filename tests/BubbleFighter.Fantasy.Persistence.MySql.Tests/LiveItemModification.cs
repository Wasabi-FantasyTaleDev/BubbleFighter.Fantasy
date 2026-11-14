using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Concurrent exact modification plans couple currency and both bags, preserving deletion history after rereading.</summary>
    [Fact]
    public async Task EquipmentModificationPersistsWalletMaterialsAndEmptyDeletionRevision()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var scope = new InventoryLoadoutScope("kr-1256", [300100, 190200, 190201]);
        var store = new MySqlInventoryRepository(factory);
        var wallets = new MySqlPlayerWalletRepository(factory);
        await wallets.GetOrCreateAsync(new(user, 1000, 50), default);
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 10, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 11, 190200, 6, 0),
            new(user, InventoryBag.CharacterBag, 12, 190201, 1, 0),
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var snapshot = await store.InitializeArticlesAsync(new(user, scope, [300100], now), default);
        var apply = new ItemModificationCommand(snapshot.CharacterBag.Single(row => row.ItemId == 300100), scope,
            [new(snapshot.CharacterBag.Single(row => row.ItemId == 190200), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190201), 1)],
            new(101, 160, -40, 0, 0), null, 100, now);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await store.TryModifyAsync(apply, default))));
        var applied = Assert.Single(results, result => result is not null)!;
        Assert.Equal(new PlayerWallet(user, 900, 50), applied.Wallet);
        Assert.Equal(applied.Wallet, await wallets.FindByUserNumAsync(user, default));
        var reopened = new MySqlInventoryRepository(factory);
        var saved = await reopened.ReadEnhancementInventoryAsync(user, scope, default);
        Assert.Equal(applied.Inventory.CharacterBag, saved.CharacterBag);
        Assert.Equal(applied.Inventory.EquippedBag, saved.EquippedBag);
        foreach (var bag in new[] { saved.CharacterBag, saved.EquippedBag })
        {
            Assert.Equal(4, bag.Single(row => row.ItemId == 190200).Quantity);
            Assert.DoesNotContain(bag, row => row.ItemId == 190201);
            Assert.Equal(new ItemModificationState(1, [new(101, 160, -40, 0, 0)]), bag.Single(row => row.ItemId == 300100).Modifications!.Find(scope.Key));
        }
        var deletion = new ItemModificationCommand(saved.CharacterBag.Single(row => row.ItemId == 300100), scope, [], null, 0, 25, now);
        var deleted = Assert.IsType<ItemModificationResult>(await reopened.TryModifyAsync(deletion, default));
        Assert.Equal(new PlayerWallet(user, 875, 50), await wallets.FindByUserNumAsync(user, default));
        saved = await new MySqlInventoryRepository(factory).ReadEnhancementInventoryAsync(user, scope, default);
        Assert.Equal(deleted.Inventory.CharacterBag, saved.CharacterBag);
        Assert.Equal(new ItemModificationState(2, []), saved.CharacterBag.Single(row => row.ItemId == 300100).Modifications!.Find(scope.Key));
        Assert.Null(await reopened.TryModifyAsync(apply, default));
        Assert.Null(await reopened.TryModifyAsync(deletion, default));
    }
}
