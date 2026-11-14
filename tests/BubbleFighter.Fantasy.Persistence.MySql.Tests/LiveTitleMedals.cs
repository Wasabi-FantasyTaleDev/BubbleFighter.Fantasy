using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Concurrent title acquisition spends all medals once and shares inventory and Article watermarks with other grants.</summary>
    [Fact]
    public async Task TitleClaimCommitsBalancesOwnershipAndPublicationMetadataTogether()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var scope = new InventoryLoadoutScope("kr-1256", [400001, 300100, 301001]);
        var store = new MySqlInventoryRepository(factory);
        Assert.Empty(await store.ReadAsync(long.MaxValue, scope.Key, default));
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 10, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 11, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var credit = new TitleMedalGrantCommand(user, scope.Key, "test", "initial", [new(30007, 30), new(30008, 25)]);
        Assert.True((await store.GrantAsync(credit, default)).Applied);
        var command = new TitleClaimCommand(user, scope, 400001, 1200001, [new(30007, 20), new(30008, 20)],
            new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, now)
        {
            ArticleInitialization = new(user, scope, [300100, 400001], now),
            PetInitialization = new(user, scope, [301001], new(0, 0, now, null, null, null), now),
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await store.TryClaimAsync(command, default))));
        var committed = Assert.Single(results, result => result is not null)!;
        Assert.Equal(12, committed.GrantedItem.SlotKey);
        Assert.NotNull(committed.GrantedItem.ArticleIdentity);
        Assert.Equal(10, committed.Medals.Single(row => row.MedalId == 30007).Count);
        Assert.Equal(5, committed.Medals.Single(row => row.MedalId == 30008).Count);
        foreach (var bag in new[] { committed.Inventory.CharacterBag, committed.Inventory.EquippedBag })
        {
            Assert.NotNull(bag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.NotNull(bag.Single(row => row.ItemId == 301001).PetIdentity);
        }
        var reopened = new MySqlInventoryRepository(factory);
        Assert.False((await reopened.GrantAsync(credit, default)).Applied);
        var saved = await reopened.ReadEnhancementInventoryAsync(user, scope, default);
        var prepared = await reopened.PrepareInventorySnapshotAsync(new(user, scope)
        {
            ArticleInitialization = command.ArticleInitialization,
            PetInitialization = command.PetInitialization,
        }, default);
        Assert.Equal(saved.CharacterBag, prepared.CharacterBag);
        Assert.Equal(saved.EquippedBag, prepared.EquippedBag);
        Assert.Equal(committed.Inventory.CharacterBag, saved.CharacterBag);
        Assert.Equal(committed.Inventory.EquippedBag, saved.EquippedBag);
        Assert.Equal(committed.Medals, await reopened.ReadAsync(user, scope.Key, default));
        Assert.Null(await reopened.TryClaimAsync(command with { ExpectedTitle = committed.GrantedItem }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.GrantAsync(credit with { Source = "changed" }, default).AsTask());
    }
}
