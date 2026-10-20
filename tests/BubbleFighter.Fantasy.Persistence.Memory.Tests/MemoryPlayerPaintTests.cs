using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks that color writes and spray consumption share one player lock.</summary>
public sealed class MemoryPlayerPaintTests
{
    /// <summary>Paint changes only the selected stack and its equipped mirror, retaining unrelated state.</summary>
    [Fact]
    public async Task PaintConsumesOneStackAndRetainsOtherItemsAndIdentity()
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var player = await players.CreateAsync("paint", "Painter", default);
        var original = new InventorySlotRecord(player.UserNum, InventoryBag.CharacterBag, 91, 100180, 3, 0x80)
            { Lifetime = InventoryLifetime.Permanent };
        var selected = original with { SlotKey = 92, Quantity = 2 };
        var other = original with { SlotKey = 93, ItemId = 100064, Quantity = 9 };
        await inventory.ReplaceSlotsAsync(player.UserNum, InventoryBag.CharacterBag, [original, selected, other], default);
        await inventory.ReplaceSlotsAsync(player.UserNum, InventoryBag.EquippedBag,
            [selected with { Bag = InventoryBag.EquippedBag }], default);

        Assert.True(await players.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default));
        Assert.Equal(player with { ColorIndex = 14 }, await players.FindByUsernameAsync("paint", default));
        Assert.Equal(new[] { original, selected with { Quantity = 1 }, other },
            await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default));
        Assert.Equal(selected with { Bag = InventoryBag.EquippedBag, Quantity = 1 },
            Assert.Single(await inventory.ListSlotsAsync(player.UserNum, InventoryBag.EquippedBag, 32, default)));
        Assert.False(await players.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default));
        Assert.Equal(1, (await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default))[1].Quantity);
    }

    /// <summary>Unavailable, expired and empty sprays cannot alter color or inventory.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task RejectedPaintPreservesInventory(int quantity, bool expired)
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var player = await players.CreateAsync("paint", "Painter", default);
        var spray = new InventorySlotRecord(player.UserNum, InventoryBag.CharacterBag, 91, 100180, quantity, 0x80)
            { Lifetime = expired ? InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) : InventoryLifetime.Permanent };
        await inventory.ReplaceSlotsAsync(player.UserNum, InventoryBag.CharacterBag, [spray], default);
        Assert.False(await players.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default));
        Assert.False(await players.ApplyPaintWithItemAsync(player.UserNum, 100064, 1, default));
        Assert.Equal(player, await players.FindByUsernameAsync("paint", default));
        Assert.Equal(spray, Assert.Single(await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default)));
    }

    /// <summary>Simultaneous requests cannot reuse the last owned spray.</summary>
    [Fact]
    public async Task ConcurrentRequestsConsumeTheLastSprayOnce()
    {
        var table = new MemoryPlayerTable(4);
        var players = new MemoryPlayerRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var player = await players.CreateAsync("paint", "Painter", default);
        await inventory.ReplaceSlotsAsync(player.UserNum, InventoryBag.CharacterBag,
            [new(player.UserNum, InventoryBag.CharacterBag, 91, 100180, 1, 0)], default);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            await players.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default))));
        Assert.Single(results, result => result);
        Assert.Empty(await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default));
        Assert.Equal((ushort)14, (await players.FindByUsernameAsync("paint", default))!.ColorIndex);
    }

    /// <summary>Cancellation and invalid arguments fail before changing durable state.</summary>
    [Fact]
    public async Task InvalidAndCancelledRequestsLeaveColorUnchanged()
    {
        var players = new MemoryPlayerRepository(new MemoryPlayerTable(4));
        var player = await players.CreateAsync("paint", "Painter", default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => players.ApplyPaintWithItemAsync(player.UserNum, 100180, 0, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => players.ApplyPaintWithItemAsync(player.UserNum, 0, 14, default).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => players.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, new(true)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => players.ApplyPaintWithItemAsync(99, 100180, 14, default).AsTask());
        Assert.Equal(player, await players.FindByUsernameAsync("paint", default));
    }
}
