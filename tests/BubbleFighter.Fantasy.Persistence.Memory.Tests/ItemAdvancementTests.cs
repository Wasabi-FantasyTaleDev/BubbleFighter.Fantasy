using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks that a laboratory grade-up commits with its card debit on both bags and survives later enhancement.</summary>
public sealed class ItemAdvancementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300301, 300302, 190247]);

    /// <summary>The selector lands on the first member only, the card is debited, a repeat is stale and enhancement keeps the grade-up.</summary>
    [Fact]
    public async Task GradeUpCommitsAtomicallyAndSurvivesEnhancement()
    {
        var store = new MemoryInventoryRepository(new(1));
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300301, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 300302, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 3, 190247, 4, 0),
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var initial = await store.InitializeArticlesAsync(new(1, Scope, [300301, 300302], Now), default);
        InventorySlotRecord[] targets = [.. new[] { 300301, 300302 }.Select(id => initial.CharacterBag.Single(row => row.ItemId == id))];
        var command = new ItemEnhancementCommand(targets, Scope, [new(initial.CharacterBag.Single(row => row.ItemId == 190247), 3)],
            ItemEnhancementKind.None, false, Now) { Advancement = 157 };
        var result = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(command, default));
        foreach (var bag in new[] { result.CharacterBag, result.EquippedBag })
        {
            Assert.Equal(1, bag.Single(row => row.ItemId == 190247).Quantity);
            Assert.Equal((ItemEnhancementKind.None, 157), (bag.Single(row => row.ItemId == 300301).Enhancements!.Find(Scope.Key)!.Kind,
                bag.Single(row => row.ItemId == 300301).Enhancements!.Find(Scope.Key)!.Advancement));
            Assert.Null(bag.Single(row => row.ItemId == 300302).Enhancements);
        }
        Assert.Equal(result.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
        // The plan was made from the earlier rows, so replaying it is stale and spends nothing more.
        Assert.Null(await store.TryEnhanceAsync(command, default));

        var carrier = result.CharacterBag.Single(row => row.ItemId == 300301);
        var enhanced = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(new([carrier], Scope, [], ItemEnhancementKind.Clothing, true, Now), default));
        var state = enhanced.CharacterBag.Single(row => row.ItemId == 300301).Enhancements!.Find(Scope.Key)!;
        Assert.Equal((ItemEnhancementKind.Clothing, 157, 2L), (state.Kind, state.Advancement, state.Revision));

        // The grade-up mode requires no applied enhancement, and the same selector cannot be installed twice.
        Assert.Throws<ArgumentException>(() => (command with { EnhancementKind = ItemEnhancementKind.Clothing }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { ApplyEnhancement = true }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (command with { Advancement = -1 }).Validate());
        var again = new ItemEnhancementCommand([enhanced.CharacterBag.Single(row => row.ItemId == 300301)], Scope,
            [new(enhanced.CharacterBag.Single(row => row.ItemId == 190247), 1)], ItemEnhancementKind.None, false, Now) { Advancement = 157 };
        Assert.Null(await store.TryEnhanceAsync(again, default));
    }
}
