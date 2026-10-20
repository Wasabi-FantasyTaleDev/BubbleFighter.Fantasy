using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Fighters.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Fighters.Tests;

/// <summary>
/// Coverage for the store-backed roster: the unlocked set is the character bag
/// and the equipped id is the fighters table, so a restart keeps what the
/// player owns and wears. Both stores are in-memory fakes, so no database is
/// needed.
/// </summary>
[Trait("Category", "LocalClientData")]
public sealed class StoreBackedFighterRosterTests
{
    private static FighterCatalog Catalog() => FighterCatalogLoader.Load(
        LocalClientData.Bytes("kr-1256", "Config/FighterConfig.xml"), "Config/FighterConfig.xml", 134_217_728);

    [Fact]
    public async Task UnlockAddsToTheBagAndEquipsWhenNoSlotExists()
    {
        var inventory = new FakeInventory();
        var fighters = new FakeFighters();
        var roster = new StoreBackedFighterRoster(inventory, fighters, Catalog());

        await roster.UnlockAsync(9, 300001, CancellationToken.None);

        Assert.Equal(new[] { 300001 }, await roster.ListUnlockedAsync(9, CancellationToken.None));
        Assert.Equal(300001, await roster.GetEquippedAsync(9, CancellationToken.None));
        Assert.Single(await inventory.ListSlotsAsync(9, InventoryBag.CharacterBag, 512, CancellationToken.None));
    }

    [Fact]
    public async Task UnlockIsIdempotentForTheSameItem()
    {
        var inventory = new FakeInventory();
        var roster = new StoreBackedFighterRoster(inventory, new FakeFighters(), Catalog());

        await roster.UnlockAsync(9, 300001, CancellationToken.None);
        await roster.UnlockAsync(9, 300001, CancellationToken.None);

        Assert.Equal(new[] { 300001 }, await roster.ListUnlockedAsync(9, CancellationToken.None));
    }

    [Fact]
    public async Task EquipKeepsTheOnlyUnlockedItem()
    {
        var inventory = new FakeInventory();
        var fighters = new FakeFighters();
        var roster = new StoreBackedFighterRoster(inventory, fighters, Catalog());
        await roster.UnlockAsync(9, 300001, CancellationToken.None);

        await roster.EquipAsync(9, 300001, CancellationToken.None);

        Assert.Equal(300001, await roster.GetEquippedAsync(9, CancellationToken.None));
    }

    [Fact]
    public async Task EquipWithoutUnlockFailsLoud()
    {
        var roster = new StoreBackedFighterRoster(new FakeInventory(), new FakeFighters(), Catalog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roster.EquipAsync(9, 300001, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task UnknownItemFailsLoud()
    {
        var roster = new StoreBackedFighterRoster(new FakeInventory(), new FakeFighters(), Catalog());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roster.UnlockAsync(9, 999999, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task UnknownUserSeedsTheCatalogDefault()
    {
        var inventory = new FakeInventory();
        var fighters = new FakeFighters();
        var roster = new StoreBackedFighterRoster(inventory, fighters, Catalog());
        var expected = Catalog().Default.ItemId;

        Assert.Empty(await roster.ListUnlockedAsync(4, CancellationToken.None));
        Assert.Equal(expected, await roster.GetEquippedAsync(4, CancellationToken.None));
        Assert.Single(await fighters.ListByUserNumAsync(4, CancellationToken.None));
    }

    [Fact]
    public async Task BagItemsAreTheUnlockedSet()
    {
        var inventory = new FakeInventory();
        var fighters = new FakeFighters();
        var catalog = Catalog();
        // A bag row the shop would have written: the item id sits in Unknown1.
        await inventory.ReplaceSlotsAsync(
            9,
            InventoryBag.CharacterBag,
            [
                new InventorySlotRecord(9, InventoryBag.CharacterBag, 11, 300001, 0, 0),
                new InventorySlotRecord(9, InventoryBag.CharacterBag, 12, 201001, 0, 0),
            ],
            CancellationToken.None);
        var roster = new StoreBackedFighterRoster(inventory, fighters, catalog);

        // Only the 300000-range ids are characters; 201001 is a weapon and is
        // not part of the roster.
        Assert.Equal(new[] { 300001 }, await roster.ListUnlockedAsync(9, CancellationToken.None));
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        private readonly Dictionary<(long, InventoryBag), List<InventorySlotRecord>> _slots = new();

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
            long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_slots.TryGetValue((userNum, bag), out var rows))
                return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([]);
            var count = Math.Min(maxCount, rows.Count);
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(rows.GetRange(0, count));
        }

        public ValueTask ReplaceSlotsAsync(
            long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _slots[(userNum, bag)] = [.. slots];
            return ValueTask.CompletedTask;
        }

        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_slots.TryGetValue((userNum, InventoryBag.CharacterBag), out var rows))
                return ValueTask.FromResult<InventorySlotRecord?>(null);
            InventorySlotRecord? last = null;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].ItemId != itemId)
                    continue;
                var flags = worn
                    ? (byte)(rows[i].Flags | InventorySlotRecord.WornFlagBit)
                    : (byte)(rows[i].Flags & ~InventorySlotRecord.WornFlagBit);
                rows[i] = rows[i] with { Flags = flags };
                last = rows[i];
            }

            return ValueTask.FromResult(last);
        }






    }

    private sealed class FakeFighters : IFighterRepository
    {
        private readonly List<FighterRecord> _rows = [];

        public ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<FighterRecord>>(
                _rows.FindAll(r => r.UserNum == userNum));
        }

        public ValueTask UpsertAsync(FighterRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = _rows.FindIndex(r => r.UserNum == record.UserNum && r.Slot == record.Slot);
            if (index >= 0)
                _rows[index] = record;
            else
                _rows.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
