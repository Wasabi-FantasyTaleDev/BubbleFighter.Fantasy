using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Concurrent exact plans commit once, retain unrelated pet ownership, and preserve failure revisions after rereading.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquipmentEnhancementCommitsAllMaterialsAndTargetsExactlyOnce(bool success)
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var scope = new InventoryLoadoutScope("kr-1256", [300100, 300101, 190106, 190107, 301001]);
        var store = new MySqlInventoryRepository(factory);
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 10, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 11, 300101, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 12, 190106, 4, 0),
            new(user, InventoryBag.CharacterBag, 13, 190107, 2, 0),
            new(user, InventoryBag.CharacterBag, 14, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var pets = await store.InitializePetsAsync(new(user, scope, [301001], new(0, 0, now, null, null, null), now), default);
        var pet = pets.CharacterBag.Single(row => row.ItemId == 301001);
        var snapshot = await store.InitializeArticlesAsync(new(user, scope, [300100, 300101], now), default);
        Assert.Equal(3, snapshot.CharacterBag.Select(InventoryArticleIdentityResolver.Of).OfType<InventoryArticleIdentity>()
            .Select(identity => identity.ArticleKey).Distinct().Count());
        var command = new ItemEnhancementCommand(snapshot.CharacterBag.Where(row => row.ItemId is 300100 or 300101).ToArray(), scope,
            [new(snapshot.CharacterBag.Single(row => row.ItemId == 190106), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190107), 2)],
            ItemEnhancementKind.Clothing, success, now);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await store.TryEnhanceAsync(command, default))));
        var committed = Assert.Single(results, result => result is not null)!;
        var reopened = new MySqlInventoryRepository(factory);
        var saved = await reopened.ReadEnhancementInventoryAsync(user, scope, default);
        Assert.Equal(committed.CharacterBag, saved.CharacterBag);
        Assert.Equal(committed.EquippedBag, saved.EquippedBag);
        Assert.Equal(pet, saved.CharacterBag.Single(row => row.ItemId == 301001));
        foreach (var bag in new[] { saved.CharacterBag, saved.EquippedBag })
        {
            Assert.Equal(2, bag.Single(row => row.ItemId == 190106).Quantity);
            Assert.DoesNotContain(bag, row => row.ItemId == 190107);
            Assert.All(bag.Where(row => row.ItemId is 300100 or 300101), row => Assert.Equal(
                new ItemEnhancementState(success ? ItemEnhancementKind.Clothing : ItemEnhancementKind.None, 1), row.Enhancements!.Find(scope.Key)));
        }
        Assert.Null(await reopened.TryEnhanceAsync(command, default));
        var target = saved.CharacterBag.Single(row => row.ItemId == 300100);
        var selected = Assert.IsType<InventorySnapshot>(await reopened.SelectArticleAsync(new(user, scope, target, [], true, now), default));
        Assert.Equal(1, selected.CharacterBag.Single(row => row.ItemId == 300100).Flags & 1);
        Assert.Null(await reopened.SelectArticleAsync(new(user, scope, target, [], true, now), default));
    }
}
