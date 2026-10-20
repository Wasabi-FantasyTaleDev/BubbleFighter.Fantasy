using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Persistence.MySql;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class RepositoryGuardTests
{
    [Fact]
    public async Task FighterRejectsInvalidLiveType()
    {
        var repo = new MySqlFighterRepository(new UnusedFactory());
        var record = new FighterRecord(1, 0, 0, 1, 0, "Dao");

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.UpsertAsync(record, CancellationToken.None).AsTask());
        Assert.Contains("1..31", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryListRejectsUnboundedMaxCount()
    {
        var repo = new MySqlInventoryRepository(new UnusedFactory());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag + 1, CancellationToken.None).AsTask());
        Assert.Contains("maxCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventorySetSlotWornRejectsInvalidIdsBeforeOpeningAConnection()
    {
        var repo = new MySqlInventoryRepository(new UnusedFactory());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.SetSlotWornAsync(1, 0, worn: true, CancellationToken.None).AsTask());
        Assert.Contains("item id", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.SetSlotWornAsync(1, -5, worn: true, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.SetSlotWornAsync(0, 200001, worn: true, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task InventoryReplaceRejectsOversizedSnapshot()
    {
        var repo = new MySqlInventoryRepository(new UnusedFactory());
        var slots = Enumerable.Range(0, InventoryLimits.MaxInventorySlotsPerBag + 1)
            .Select(i => new InventorySlotRecord(1, InventoryBag.CharacterBag, i, 0, 0, 0))
            .ToList();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, slots, CancellationToken.None).AsTask());
        Assert.Contains("512", ex.Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task MoneyRejectsZeroUserNum()
    {
        var repo = new MySqlPlayerWalletRepository(new UnusedFactory());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.FindByUserNumAsync(0, CancellationToken.None).AsTask());
    }

    private sealed class UnusedFactory : IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Guards must fail before opening a connection.");
    }
}
