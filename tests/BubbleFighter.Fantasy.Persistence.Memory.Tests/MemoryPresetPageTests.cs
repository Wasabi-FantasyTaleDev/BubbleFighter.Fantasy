using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>
/// Pins the in-memory preset page store beside the active-slot store it extends: first-seen players read
/// as unmaterialized (null, not an empty table), a set stores a validated five-page copy, and the guard
/// rejects partial tables because pages are positional - <c>kr-1256 sub_81B820</c> keys the client's page
/// map by list position, so a hole would shift every later page onto the wrong hotkey.
/// </summary>
public sealed class MemoryPresetPageTests
{
    private static readonly WeaponPresetPage[] Pages =
    [
        new(202001, 205001, 0), new(203001, 205001, 0), new(204001, 0, 0),
        new(202003, 205001, 7), new(204003, 0, 0),
    ];

    [Fact]
    public async Task ConcurrentInitializersReturnTheSameCommittedPages()
    {
        var state = new MemoryPlayerInventoryState(new MemoryPlayerTable(4));
        var first = WeaponPresetPages.SeedFromWorn(202001, 0);
        var second = WeaponPresetPages.SeedFromWorn(204001, 205001);
        using var gate = new Barrier(2);
        Task<WeaponPresetPage[]> Initialize(WeaponPresetPage[] seed) => Task.Run(async () =>
        {
            Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await state.GetOrCreateWeaponPresetPagesAsync(7, seed, default);
        });
        var tables = await Task.WhenAll(Initialize(first), Initialize(second));
        Assert.Equal(tables[0], tables[1]);
        Assert.Equal(tables[0], await state.GetWeaponPresetPagesAsync(7, default));
        tables[0][0] = new(999, 0, 0);
        Assert.Equal(tables[1], await state.GetOrCreateWeaponPresetPagesAsync(7, first, default));
    }

    [Fact]
    public async Task AFirstSeenPlayerHasNoPagesAndASetSurvivesReadback()
    {
        var state = new MemoryPlayerInventoryState(new MemoryPlayerTable(4));
        Assert.Null(await state.GetWeaponPresetPagesAsync(7, default));

        await state.SetWeaponPresetPagesAsync(7, Pages, default);

        Assert.Equal(Pages, await state.GetWeaponPresetPagesAsync(7, default));
        // Another player stays unmaterialized; pages never leak across the key.
        Assert.Null(await state.GetWeaponPresetPagesAsync(8, default));
    }

    [Fact]
    public async Task TheStoredTableIsACopyNotTheCallerArray()
    {
        var state = new MemoryPlayerInventoryState(new MemoryPlayerTable(4));
        var pages = (WeaponPresetPage[])Pages.Clone();

        await state.SetWeaponPresetPagesAsync(7, pages, default);
        pages[0] = new(207001, 0, 0);

        Assert.Equal(Pages[0], (await state.GetWeaponPresetPagesAsync(7, default))![0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task PartialOrOversizedTablesAreRejectedBeforeAnyStateChanges(int count)
    {
        var state = new MemoryPlayerInventoryState(new MemoryPlayerTable(4));
        await state.SetWeaponPresetPagesAsync(7, Pages, default);
        // The oversized row pads with a sixth page rather than slicing past the table's end.
        var rejected = count <= Pages.Length ? Pages[..count]
            : [.. Pages, .. Enumerable.Repeat(new WeaponPresetPage(202001, 0, 0), count - Pages.Length)];

        await Assert.ThrowsAsync<ArgumentException>(() => state.SetWeaponPresetPagesAsync(7, rejected, default).AsTask());

        Assert.Equal(Pages, await state.GetWeaponPresetPagesAsync(7, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            state.GetWeaponPresetPagesAsync(0, default).AsTask());
    }
}
