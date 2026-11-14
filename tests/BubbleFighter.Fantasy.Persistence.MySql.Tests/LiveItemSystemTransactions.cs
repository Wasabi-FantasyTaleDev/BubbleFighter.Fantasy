using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>A missing wallet rolls back staged medals and progress before a concurrent retry commits one complete round.</summary>
    [Fact]
    public async Task RoundMedalsRollBackWithWalletFailureAndSurviveConcurrentReplay()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var progress = new MySqlPlayerProgressRepository(factory);
        var inventory = new MySqlInventoryRepository(factory);
        var match = "medal-" + Guid.NewGuid().ToString("N");
        var command = new RoundSettlementCommand(match, user, 2, stored => stored with { Exp = stored.Exp + 7 })
        { TitleMedals = new(user, "kr-1256", "round", "round:" + match, [new(30001, 3)]) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.SettleRoundAsync(command, default).AsTask());
        Assert.Empty(await inventory.ReadAsync(user, "kr-1256", default));
        Assert.Equal(0, (await progress.FindAsync(user, default))!.Exp);

        var wallets = new MySqlPlayerWalletRepository(factory);
        await wallets.GetOrCreateAsync(new(user, 10, 0), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () => await progress.SettleRoundAsync(command, default))));
        Assert.Single(results, result => result.Outcome == RoundSettlementOutcome.Settled);
        Assert.Equal(7, (await progress.FindAsync(user, default))!.Exp);
        Assert.Equal(new PlayerWallet(user, 12, 0), await wallets.FindByUserNumAsync(user, default));
        var reopened = new MySqlPlayerProgressRepository(factory);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled, (await reopened.SettleRoundAsync(command, default)).Outcome);
        Assert.Equal(new[] { new TitleMedalCount(30001, 3) },
            await new MySqlInventoryRepository(factory).ReadAsync(user, "kr-1256", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SettleRoundAsync(command with
        { TitleMedals = new(user, "kr-1256", "round", "round:" + match, [new(30001, 4)]) }, default).AsTask());
        Assert.Equal(12, (await wallets.FindByUserNumAsync(user, default))!.Lucci);
    }

    /// <summary>A laboratory transaction consumes the exact source once and preserves its replacement's allocated identity across rereading.</summary>
    [Fact]
    public async Task LaboratoryConsumesBothBagsAndPublishesOneReplacementAcrossConcurrentRequests()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var scope = new InventoryLoadoutScope("kr-812", [11, 101, 104]);
        var store = new MySqlInventoryRepository(factory);
        InventorySlotRecord[] rows =
        [
            new(user, InventoryBag.CharacterBag, 1, 11, 2, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 20, 101, int.MaxValue, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [rows[1] with { Bag = InventoryBag.EquippedBag }], default);
        var before = await store.InitializeArticlesAsync(new(user, scope, [101], now), default);
        var command = new InventoryLaboratoryCommand(user, scope,
            [new(before.CharacterBag.Single(row => row.ItemId == 11), 1, false),
                new(before.CharacterBag.Single(row => row.ItemId == 101), 0, true)],
            [new(new(104, InventoryGrantKind.Permanent, 1, 1), null)], now)
        { ArticleInitialization = new(user, scope, [101, 104], now) };
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () => await store.TryLaboratoryAsync(command, default))));
        var committed = Assert.Single(results, result => result is not null)!;
        var reward = Assert.Single(committed.GrantedItems);
        Assert.Equal(104, reward.ItemId);
        Assert.Equal(21, reward.SlotKey);
        Assert.NotNull(reward.ArticleIdentity);
        Assert.NotEqual(before.CharacterBag.Single(row => row.ItemId == 101).ArticleIdentity, reward.ArticleIdentity);
        Assert.Equal(1, committed.Inventory.CharacterBag.Single(row => row.ItemId == 11).Quantity);
        Assert.DoesNotContain(committed.Inventory.CharacterBag, row => row.ItemId == 101);
        Assert.DoesNotContain(committed.Inventory.EquippedBag, row => row.ItemId == 101);
        var reopened = new MySqlInventoryRepository(factory);
        var saved = await reopened.ReadLaboratoryInventoryAsync(user, scope, default);
        Assert.Equal(committed.Inventory.CharacterBag, saved.CharacterBag);
        Assert.Equal(committed.Inventory.EquippedBag, saved.EquippedBag);
        Assert.Null(await reopened.TryLaboratoryAsync(command, default));
    }

    /// <summary>Removing and reacquiring a title never reissues its durable item and medal bonuses or accepts a changed receipt.</summary>
    [Fact]
    public async Task TitleAcquisitionBonusesRemainOnceOnlyAfterOwnershipIsRemoved()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var now = DateTimeOffset.UtcNow;
        var scope = new InventoryLoadoutScope("kr-1256", [400001, 105]);
        var store = new MySqlInventoryRepository(factory);
        await store.GrantAsync(new(user, scope.Key, "test", "initial", [new(30007, 30)]), default);
        var command = new TitleClaimCommand(user, scope, 400001, 1200001, [new(30007, 5)],
            new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, now)
        {
            BonusGrants = [new(105, InventoryGrantKind.Count, 2, 100, false)],
            BonusMedals = [new(30008, 7)],
            ArticleInitialization = new(user, scope, [400001], now),
        };
        var first = Assert.IsType<TitleClaimResult>(await store.TryClaimAsync(command, default));
        Assert.Equal(2, first.Inventory.CharacterBag.Single(row => row.ItemId == 105).Quantity);
        Assert.Equal(7, first.Medals.Single(row => row.MedalId == 30008).Count);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = await store.ListSlotsAsync(user, bag, InventoryLimits.MaxInventorySlotsPerBag, default);
            await store.ReplaceSlotsAsync(user, bag, saved.Where(row => row.ItemId != 400001).ToArray(), default);
        }
        var reopened = new MySqlInventoryRepository(factory);
        var second = Assert.IsType<TitleClaimResult>(await reopened.TryClaimAsync(command, default));
        Assert.Equal(2, second.Inventory.CharacterBag.Single(row => row.ItemId == 105).Quantity);
        Assert.Equal(20, second.Medals.Single(row => row.MedalId == 30007).Count);
        Assert.Equal(7, second.Medals.Single(row => row.MedalId == 30008).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.TryClaimAsync(command with
        { BonusMedals = [new(30008, 8)] }, default).AsTask());
        Assert.Equal(second.Medals, await reopened.ReadAsync(user, scope.Key, default));
    }
}
