using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>The daily life rule: the shared arithmetic and the in-process store's per-player application of it.</summary>
public sealed class InventoryLifeTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    private static InventorySlotRecord Row(long user, InventoryBag bag, int itemId, int quantity, InventoryLifetime lifetime) =>
        new(user, bag, itemId, itemId, quantity, 0) { Lifetime = lifetime };

    private static async Task<MemoryInventoryRepository> StoreWith(long user, params (int Id, int Quantity, InventoryLifetime Lifetime)[] rows)
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await store.ReplaceSlotsAsync(user, bag, rows.Select(r => Row(user, bag, r.Id, r.Quantity, r.Lifetime)).ToArray(), default);
        return store;
    }

    private static async Task<IReadOnlyList<int>> Quantities(MemoryInventoryRepository store, long user, InventoryBag bag) =>
        (await store.ListSlotsAsync(user, bag, 512, default)).Select(row => row.Quantity).ToList();

    [Fact]
    public void DaysMissedCountsWholeUtcDaysAndNeverChargesAFirstLoginOrAClockThatWentBack()
    {
        Assert.Equal(0, InventoryLife.DaysMissed(null, Day));
        Assert.Equal(0, InventoryLife.DaysMissed(Day, Day));
        Assert.Equal(1, InventoryLife.DaysMissed(Day.AddDays(-1), Day));
        Assert.Equal(5, InventoryLife.DaysMissed(new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 5)));
        Assert.Equal(365, InventoryLife.DaysMissed(new DateOnly(2025, 10, 12), Day));
        Assert.Equal(0, InventoryLife.DaysMissed(Day.AddDays(3), Day));
    }

    [Fact]
    public void ReduceFloorsAtZeroAndTheStoredDayNeverMovesBack()
    {
        Assert.Equal(7, InventoryLife.Reduce(10, 3));
        Assert.Equal(0, InventoryLife.Reduce(3, 3));
        Assert.Equal(0, InventoryLife.Reduce(3, 100));
        Assert.Equal(0, InventoryLife.Reduce(0, 1));
        Assert.Equal(0, InventoryLife.Reduce(int.MinValue, int.MaxValue));
        Assert.Equal(1, InventoryLife.Reduce(int.MaxValue, int.MaxValue - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryLife.Reduce(5, -1));
        Assert.Equal(Day, InventoryLife.NextLoginDay(null, Day));
        Assert.Equal(Day, InventoryLife.NextLoginDay(Day.AddDays(-4), Day));
        Assert.Equal(Day.AddDays(2), InventoryLife.NextLoginDay(Day.AddDays(2), Day));
    }

    [Fact]
    public void ALifeRowHasEndedExactlyWhenItsLifeIsZeroOrLess()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, 0, InventoryLifetime.Life), now));
        Assert.True(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, -1, InventoryLifetime.Life), now));
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, 1, InventoryLifetime.Life), now));
        // A count row at zero is not an ended row; only the life kind has this meaning.
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, 0, InventoryLifetime.Permanent), now));
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, 0, default), now));
    }

    [Fact]
    public async Task TheFirstLoginEverChargesNothingAndOnlyStoresTheDay()
    {
        var store = await StoreWith(7, (201350, 15, InventoryLifetime.Life));

        Assert.Equal(0, await store.AdvanceLifeAsync(7, Day, default));
        Assert.Equal([15], await Quantities(store, 7, InventoryBag.CharacterBag));
        // The stored day is what the next login measures from.
        Assert.Equal(2, await store.AdvanceLifeAsync(7, Day.AddDays(2), default));
        Assert.Equal([13], await Quantities(store, 7, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task ASecondLoginTheSameDayChargesNothing()
    {
        var store = await StoreWith(7, (201350, 15, InventoryLifetime.Life));
        await store.AdvanceLifeAsync(7, Day, default);

        Assert.Equal(1, await store.AdvanceLifeAsync(7, Day.AddDays(1), default));
        Assert.Equal(0, await store.AdvanceLifeAsync(7, Day.AddDays(1), default));
        Assert.Equal([14], await Quantities(store, 7, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task SeveralMissedDaysLowerEveryLifeRowInBothBags()
    {
        var store = await StoreWith(7, (201350, 15, InventoryLifetime.Life), (202386, 100, InventoryLifetime.Life));
        await store.AdvanceLifeAsync(7, Day, default);

        Assert.Equal(3, await store.AdvanceLifeAsync(7, Day.AddDays(3), default));

        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal([12, 97], await Quantities(store, 7, bag));
    }

    [Fact]
    public async Task LifeFloorsAtZeroAndTheEndedRowIsThenRemovedByTheExpiryRemoval()
    {
        var store = await StoreWith(7, (201350, 2, InventoryLifetime.Life), (202386, 9, InventoryLifetime.Life));
        await store.AdvanceLifeAsync(7, Day, default);

        Assert.Equal(5, await store.AdvanceLifeAsync(7, Day.AddDays(5), default));
        Assert.Equal([0, 4], await Quantities(store, 7, InventoryBag.CharacterBag));

        var removed = await store.RemoveExpiredAsync(7, DateTimeOffset.UtcNow, default);

        Assert.Equal([(InventoryBag.CharacterBag, 201350), (InventoryBag.EquippedBag, 201350)], removed.Select(r => (r.Bag, r.ItemId)));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal([202386], (await store.ListSlotsAsync(7, bag, 512, default)).Select(r => r.ItemId));
    }

    [Fact]
    public async Task OnlyLifeRowsOfTheNamedPlayerAreChargedAndOtherKindsKeepTheirQuantity()
    {
        var table = new MemoryPlayerTable(8);
        var store = new MemoryInventoryRepository(table);
        foreach (var user in new long[] { 7, 8 })
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [
                Row(user, InventoryBag.CharacterBag, 201350, 10, InventoryLifetime.Life),
                Row(user, InventoryBag.CharacterBag, 100104, 10, InventoryLifetime.Permanent),
                Row(user, InventoryBag.CharacterBag, 204001, 10, InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(30))),
                Row(user, InventoryBag.CharacterBag, 300001, 10, default)], default);
        await store.AdvanceLifeAsync(7, Day, default);
        await store.AdvanceLifeAsync(8, Day, default);

        await store.AdvanceLifeAsync(7, Day.AddDays(4), default);

        Assert.Equal([6, 10, 10, 10], await Quantities(store, 7, InventoryBag.CharacterBag));
        Assert.Equal([10, 10, 10, 10], await Quantities(store, 8, InventoryBag.CharacterBag));
        // Player 8's stored day is still the first one: its next login is charged from there.
        Assert.Equal(6, await store.AdvanceLifeAsync(8, Day.AddDays(6), default));
        Assert.Equal([4, 10, 10, 10], await Quantities(store, 8, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task AClockThatWentBackChargesNothingAndDoesNotChargeTheSameDaysTwice()
    {
        var store = await StoreWith(7, (201350, 30, InventoryLifetime.Life));
        await store.AdvanceLifeAsync(7, Day.AddDays(10), default);

        Assert.Equal(0, await store.AdvanceLifeAsync(7, Day, default));
        Assert.Equal(1, await store.AdvanceLifeAsync(7, Day.AddDays(11), default));
        Assert.Equal([29], await Quantities(store, 7, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task AUsePathNeverConsumesALifeItem()
    {
        var store = await StoreWith(7, (201350, 15, InventoryLifetime.Life));

        Assert.Null(await store.ConsumeItemAsync(7, 201350, default));
        Assert.Equal([15], await Quantities(store, 7, InventoryBag.CharacterBag));
    }

    [Fact]
    public async Task ARejectedUserNumberIsAnError()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AdvanceLifeAsync(0, Day, default).AsTask());
    }
}
