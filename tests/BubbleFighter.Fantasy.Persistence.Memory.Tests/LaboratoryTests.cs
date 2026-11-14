using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class LaboratoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-812", [11, 101, 102, 103, 104]);

    [Fact]
    public async Task ConcurrentRecipePlansConsumeOnceAndPreserveFractionalExpiryAndMetadata()
    {
        var store = new MemoryInventoryRepository(new(1));
        var rows = Rows(1);
        rows[1] = rows[1] with { Lifetime = InventoryLifetime.Permanent };
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, [rows[1] with { Bag = InventoryBag.EquippedBag }], default);
        var initialized = await store.InitializeArticlesAsync(new(1, Scope, [101], Now), default);
        rows = initialized.CharacterBag.ToArray();
        rows[1] = rows[1] with { Lifetime = InventoryLifetime.Until(Now.AddDays(1.25)) };
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, [rows[1] with { Bag = InventoryBag.EquippedBag }], default);
        var identity = rows[1].ArticleIdentity;
        var command = Command(rows);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryLaboratoryAsync(command, default))));
        var result = Assert.Single(attempts, result => result is not null)!;
        var survivor = Assert.Single(result.GrantedItems);
        Assert.Equal(identity, survivor.ArticleIdentity);
        Assert.Equal(rows[1].SlotKey, survivor.SlotKey);
        Assert.Equal(Now.AddDays(6.75).ToUnixTimeMilliseconds(), survivor.Lifetime.ExpiresAtUnixMilliseconds);
        Assert.Equal(1, result.Inventory.CharacterBag.Single(row => row.ItemId == 11).Quantity);
        Assert.DoesNotContain(result.Inventory.CharacterBag, row => row.ItemId is 102 or 103);
        Assert.Equal(rows[4], result.Inventory.CharacterBag.Single(row => row.ItemId == 104));
        Assert.Equal(survivor with { Bag = InventoryBag.EquippedBag }, Assert.Single(result.Inventory.EquippedBag));
    }

    [Fact]
    public async Task RewardCapacityRefusalDoesNotConsumeAnyInput()
    {
        var store = new MemoryInventoryRepository(new(1));
        var rows = Rows(1);
        rows[4] = rows[4] with { Quantity = 99 };
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        var command = new InventoryLaboratoryCommand(1, Scope,
            [new(rows[0], 1, false), new(rows[1], 0, true)],
            [new(new(104, InventoryGrantKind.Count, 2, 100), rows[4])], Now);
        Assert.Null(await store.TryLaboratoryAsync(command, default));
        Assert.Equal(rows, (await store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("worn")]
    [InlineData("expired")]
    [InlineData("reward_changed")]
    public async Task EveryExpectedRowIsCheckedBeforeAnyDebitIsSaved(string reason)
    {
        var store = new MemoryInventoryRepository(new(1));
        var rows = Rows(1);
        var command = Command(rows);
        if (reason == "stale") rows[2] = rows[2] with { SlotKey = 888 };
        if (reason == "worn") rows[2] = rows[2] with { Flags = 1 };
        if (reason == "expired") rows[2] = rows[2] with { Lifetime = InventoryLifetime.Until(Now.AddSeconds(-1)) };
        if (reason == "reward_changed") rows[1] = rows[1] with { Quantity = 100 };
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        Assert.Null(await store.TryLaboratoryAsync(command, default));
        Assert.Equal(rows, (await store.ReadLaboratoryInventoryAsync(1, Scope, default)).CharacterBag);
    }

    private static InventorySlotRecord[] Rows(long user) =>
    [
        new(user, InventoryBag.CharacterBag, 1, 11, 2, 0) { Lifetime = InventoryLifetime.Permanent },
        new(user, InventoryBag.CharacterBag, 2, 101, 2, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(1.25)) },
        new(user, InventoryBag.CharacterBag, 3, 102, 3, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(2.25)) },
        new(user, InventoryBag.CharacterBag, 4, 103, 4, 0) { Lifetime = InventoryLifetime.Until(Now.AddDays(3.25)) },
        new(user, InventoryBag.CharacterBag, 5, 104, 1, 0) { Lifetime = InventoryLifetime.Permanent },
    ];

    private static InventoryLaboratoryCommand Command(IReadOnlyList<InventorySlotRecord> rows) => new(1, Scope,
        [new(rows[0], 1, false), .. rows.Skip(1).Take(3).Select(row => new InventoryLaboratoryDebit(row, 0, true))],
        [new(new(101, InventoryGrantKind.Days, 6.75, 729), rows[1])], Now);
}
