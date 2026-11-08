using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class BuildLoadoutTests
{
    private static readonly InventoryWearGroup[] Groups =
        [new(300001, 300031, 300001, true), new(202000, 204999, 202001, true)];
    private static readonly int[] Common = [300001, 300002, 202001, 202002, 100001];
    private static readonly InventoryLoadoutScope Korean = new("kr-1256", [.. Common, 300029, 204999]);

    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    public async Task KoreanRheaDoesNotEnterOlderBuildAndReturningPreservesBothSelections(string oldBuild)
    {
        var table = new MemoryPlayerTable(4);
        var store = new MemoryInventoryRepository(table);
        var older = new InventoryLoadoutScope(oldBuild, Common);
        var rows = await SeedAsync(store, 1);
        var korean = await store.UpdateLoadoutSelectionAsync(1, Korean, Groups, null, false, default);
        Assert.Contains(korean!, row => row.ItemId == 300029 && (row.Flags & 1) == 1);
        var old = await store.UpdateLoadoutSelectionAsync(1, older, Groups, null, false, default);
        Assert.DoesNotContain(old!, row => row.ItemId is 300029 or 204999);
        Assert.Contains(old!, row => row.ItemId == 300001 && (row.Flags & 1) == 1);
        Assert.Contains(old!, row => row.ItemId == 202001 && (row.Flags & 1) == 1);
        Assert.Null(await store.UpdateLoadoutSelectionAsync(1, older, Groups, 300029, true, default));
        await store.UpdateLoadoutSelectionAsync(1, older, Groups, 300002, true, default);
        await store.UpdateLoadoutSelectionAsync(1, older, Groups, 202002, true, default);
        korean = await store.UpdateLoadoutSelectionAsync(1, Korean, Groups, null, false, default);
        Assert.Contains(korean!, row => row.ItemId == 300029 && (row.Flags & 1) == 1);
        Assert.Contains(korean!, row => row.ItemId == 204999 && (row.Flags & 1) == 1);
        old = await store.UpdateLoadoutSelectionAsync(1, older, Groups, null, false, default);
        Assert.Contains(old!, row => row.ItemId == 300002 && (row.Flags & 1) == 1);
        Assert.Contains(old!, row => row.ItemId == 202002 && (row.Flags & 1) == 1);
        Assert.Equal(rows, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
    }

    [Fact]
    public async Task EveryBuildKeepsItsPresetPagesAndActiveSlotWithoutMutatingLegacyState()
    {
        var table = new MemoryPlayerTable(4);
        var store = new MemoryInventoryRepository(table);
        await SeedAsync(store, 1);
        var legacy = new MemoryPlayerInventoryState(table);
        await legacy.SetActiveWeaponPresetSlotAsync(1, 4, default);
        var legacyPages = WeaponPresetPages.SeedFromWorn(204999, 0);
        legacyPages[0] = legacyPages[0] with { Unknown8 = 77 };
        await legacy.SetWeaponPresetPagesAsync(1, legacyPages, default);
        string[] builds = ["cn-279", "cn-324", "cn-404", "cn-406", "cn-408", "cn-706", "cn-717",
            "id-400", "id-1809", "kr-812", "kr-1256", "cn-774", "tw-454"];
        for (var index = 0; index < builds.Length; index++)
        {
            var scope = new InventoryLoadoutScope(builds[index], Common);
            var migrated = await store.GetLoadoutAsync(1, scope, default);
            Assert.All(migrated.PresetPages!, page => Assert.Equal(0, page.Unknown0));
            Assert.Equal(77, migrated.PresetPages![0].Unknown8);
            var pages = WeaponPresetPages.SeedFromWorn(index % 2 == 0 ? 202001 : 202002, 0);
            await store.UpdateLoadoutPresetsAsync(1, scope, (byte)(index % 5), pages, false, default);
            pages[0] = new(999, 999, 999);
        }
        for (var index = 0; index < builds.Length; index++)
        {
            var scope = new InventoryLoadoutScope(builds[index], Common);
            var state = await store.UpdateLoadoutPresetsAsync(1, scope, null, WeaponPresetPages.SeedFromWorn(100001, 0), true, default);
            Assert.Equal(index % 5, state.ActivePresetSlot);
            Assert.All(state.PresetPages!, page => Assert.Equal(index % 2 == 0 ? 202001 : 202002, page.Unknown0));
            state.PresetPages![0] = new(0, 0, 0);
            Assert.NotEqual(0, (await store.GetLoadoutAsync(1, scope, default)).PresetPages![0].Unknown0);
        }
        Assert.Equal(4, await legacy.GetActiveWeaponPresetSlotAsync(1, default));
        Assert.Equal(legacyPages, await legacy.GetWeaponPresetPagesAsync(1, default));
        Assert.Equal(0, (await store.GetLoadoutAsync(2, Korean, default)).ActivePresetSlot);
    }

    [Fact]
    public async Task ConcurrentSharedAppendSurvivesSelectionsAndFailedCapacityDoesNotChangeExistingState()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4), 1);
        await SeedAsync(store, 1);
        var addition = new InventorySlotRecord(1, InventoryBag.CharacterBag, 99, 100009, 3, 128);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
        {
            if (index == 0) await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [addition], default);
            else await store.UpdateLoadoutSelectionAsync(1, Korean, Groups, index % 2 == 0 ? 300029 : 300001, true, default);
        })));
        Assert.Contains(addition, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        var before = await store.GetLoadoutAsync(1, Korean, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetLoadoutAsync(1, new("cn-279", Common), default).AsTask());
        var after = await store.GetLoadoutAsync(1, Korean, default);
        Assert.Equal(before.WornItemIds, after.WornItemIds);
        Assert.NotNull(await store.UpdateLoadoutSelectionAsync(1, Korean, Groups, 300029, true, default));
    }

    private static async Task<InventorySlotRecord[]> SeedAsync(IInventoryRepository store, long user)
    {
        var rows = Korean.SupportedItemIds.Select((id, index) => new InventorySlotRecord(user, InventoryBag.CharacterBag,
            index + 1, id, 3, (byte)(id is 300029 or 204999 ? 129 : 128))
            { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(2)) }).ToArray();
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return rows;
    }
}
