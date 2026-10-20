using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks that the memory identity store shares atomic state with the memory bags.</summary>
public sealed class MemoryPlayerRepositoryTests
{
    /// <summary>Native character creation seeds the shared bags and tutorial state.</summary>
    [Fact]
    public async Task CharacterCreationSeedsSharedInventoryAndTutorial()
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);

        var created = await players.CreateCharacterAsync("alice", "Alice", StarterKit.Default, default);

        Assert.NotNull(created);
        Assert.True(await players.HasCreatedCharacterAsync(created.UserNum, default));
        Assert.True(await players.NeedsTutorialAsync(created.UserNum, default));
        Assert.NotEmpty(await inventory.ListSlotsAsync(created.UserNum, InventoryBag.CharacterBag, 256, default));
        await players.CompleteTutorialAsync(created.UserNum, default);
        Assert.False(await players.NeedsTutorialAsync(created.UserNum, default));
    }

    /// <summary>A rename atomically consumes one shared rename card and updates the fighter.</summary>
    [Fact]
    public async Task RenameConsumesCardAndUpdatesSharedFighter()
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var fighters = new MemoryFighterRepository(table);
        var created = await players.CreateCharacterAsync("alice", "Alice", StarterKit.Default, default);
        Assert.NotNull(created);
        await inventory.AppendMissingItemsAsync(created.UserNum, InventoryBag.CharacterBag,
            [new InventorySlotRecord(created.UserNum, InventoryBag.CharacterBag, 99, 100104, 1, 0)], default);
        await inventory.AppendMissingItemsAsync(created.UserNum, InventoryBag.EquippedBag,
            [new InventorySlotRecord(created.UserNum, InventoryBag.EquippedBag, 100, 100104, 1, 0)], default);

        Assert.Equal(0, await players.RenameWithItemAsync(created.UserNum, "NewAlice", 100104, default));
        Assert.Equal("NewAlice", (await players.FindByUsernameAsync("alice", default))!.Nickname);
        Assert.Equal("NewAlice", (await fighters.ListByUserNumAsync(created.UserNum, default)).Single().Nickname);
        Assert.DoesNotContain(await inventory.ListSlotsAsync(created.UserNum, InventoryBag.CharacterBag, 256, default), row => row.ItemId == 100104);
        Assert.DoesNotContain(await inventory.ListSlotsAsync(created.UserNum, InventoryBag.EquippedBag, 256, default), row => row.ItemId == 100104);
        Assert.Equal(1, await players.RenameWithItemAsync(created.UserNum, "Again", 100104, default));
    }

    /// <summary>Duplicate names are rejected before the rename card is consumed.</summary>
    [Fact]
    public async Task RenameRejectsTakenNicknameWithoutConsumingCard()
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var first = await players.CreateCharacterAsync("one", "One", StarterKit.Default, default);
        var second = await players.CreateCharacterAsync("two", "Two", StarterKit.Default, default);
        Assert.NotNull(first);
        Assert.NotNull(second);
        await inventory.AppendMissingItemsAsync(first.UserNum, InventoryBag.CharacterBag,
            [new InventorySlotRecord(first.UserNum, InventoryBag.CharacterBag, 99, 100104, 1, 0)], default);

        Assert.Equal(9, await players.RenameWithItemAsync(first.UserNum, "Two", 100104, default));
        Assert.Contains(await inventory.ListSlotsAsync(first.UserNum, InventoryBag.CharacterBag, 256, default), row => row.ItemId == 100104);
    }
}
