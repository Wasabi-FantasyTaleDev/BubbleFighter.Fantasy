using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>
/// Ports of the MySQL store guard suite: every argument check the store-backed
/// callers rely on must reject before any in-memory state is touched, so a
/// malformed call fails identically whichever persistence row is mounted.
/// </summary>
public sealed class MemoryRepositoryGuardTests
{
    [Fact]
    public async Task FighterRejectsInvalidLiveType()
    {
        var repo = new MemoryFighterRepository(NewTable());
        var record = new FighterRecord(1, 0, 0, 1, 0, "Dao");

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.UpsertAsync(record, CancellationToken.None).AsTask());
        Assert.Contains("1..31", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryListRejectsUnboundedMaxCount()
    {
        var repo = new MemoryInventoryRepository(NewTable());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag + 1, CancellationToken.None).AsTask());
        Assert.Contains("maxCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryReplaceRejectsOversizedSnapshot()
    {
        var repo = new MemoryInventoryRepository(NewTable());
        var slots = Enumerable.Range(0, InventoryLimits.MaxInventorySlotsPerBag + 1)
            .Select(i => new InventorySlotRecord(1, InventoryBag.CharacterBag, i, 0, 0, 0))
            .ToList();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, slots, CancellationToken.None).AsTask());
        Assert.Contains("512", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryReplaceRejectsForeignSlot()
    {
        var repo = new MemoryInventoryRepository(NewTable());
        var slots = new List<InventorySlotRecord> { new(2, InventoryBag.CharacterBag, 1, 0, 0, 0) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, slots, CancellationToken.None).AsTask());
        Assert.Contains("does not match replace target 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryReplaceRejectsWrongBag()
    {
        var repo = new MemoryInventoryRepository(NewTable());
        var slots = new List<InventorySlotRecord> { new(1, InventoryBag.EquippedBag, 1, 0, 0, 0) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, slots, CancellationToken.None).AsTask());
        Assert.Contains("does not match replace target", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryListRejectsUnknownBag()
    {
        var repo = new MemoryInventoryRepository(NewTable());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ListSlotsAsync(1, (InventoryBag)2, 1, CancellationToken.None).AsTask());
    }





    [Fact]
    public async Task MoneyRejectsZeroUserNum()
    {
        var repo = new MemoryPlayerWalletRepository(NewTable());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.FindByUserNumAsync(0, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.UpsertAsync(new PlayerWallet(0, 0, 0), CancellationToken.None).AsTask());
    }

    [Fact]
    public void TableRejectsZeroCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryPlayerTable(0));
    }

    private static MemoryPlayerTable NewTable() => new(16);
}
