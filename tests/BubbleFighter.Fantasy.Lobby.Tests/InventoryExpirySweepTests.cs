using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// The expiry sweep over the in-process store: expired rows leave both bags and the worn state with them,
/// unexpired rows and other players are untouched, and an expired worn character is replaced by the default.
/// </summary>
public sealed class InventoryExpirySweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLifetime Past = InventoryLifetime.Until(Now.AddDays(-1));
    private static readonly InventoryLifetime Future = InventoryLifetime.Until(Now.AddDays(3));

    private static InventorySlotRecord Row(long user, InventoryBag bag, int itemId, InventoryLifetime lifetime, bool worn = false) =>
        new(user, bag, 100 + itemId, itemId, 1, worn ? InventorySlotRecord.WornFlagBit : (byte)0) { Lifetime = lifetime };

    private static async Task<(MemoryInventoryRepository Store, InventoryExpirySweep Sweep)> Setup(
        long user, params (int Id, InventoryLifetime Lifetime, bool Worn)[] rows)
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await store.ReplaceSlotsAsync(user, bag, rows.Select(r => Row(user, bag, r.Id, r.Lifetime, r.Worn)).ToArray(), default);
        return (store, new InventoryExpirySweep(store, null, StarterKit.Default, new FixedClock(Now)));
    }

    [Fact]
    public async Task ExpiredRowsAreRemovedFromBothBagsAndUnexpiredAndPermanentRowsStay()
    {
        var (store, sweep) = await Setup(7, (300001, InventoryLifetime.Permanent, true), (201001, Past, false),
            (202001, Future, true), (204001, default, false));

        var expired = await sweep.SweepAsync(7, default);

        Assert.Equal([201001], expired);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal([300001, 202001, 204001],
                (await store.ListSlotsAsync(7, bag, 512, default)).Select(r => r.ItemId));
    }

    [Fact]
    public async Task AWornExpiredWeaponIsUnequippedAndTheCharacterStaysWorn()
    {
        var (store, sweep) = await Setup(7, (300001, InventoryLifetime.Permanent, true), (201001, Past, true), (201003, Future, false));

        Assert.Equal([201001], await sweep.SweepAsync(7, default));

        var bag = await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default);
        Assert.DoesNotContain(bag, r => r.ItemId == 201001);
        Assert.Equal([300001], bag.Where(r => (r.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(r => r.ItemId));
    }

    [Fact]
    public async Task AnExpiredWornCharacterIsReplacedByTheOwnedDefaultCharacter()
    {
        var (store, sweep) = await Setup(7, (300001, InventoryLifetime.Permanent, false), (300005, Past, true), (202001, Future, true));

        Assert.Equal([300005], await sweep.SweepAsync(7, default));

        var bag = await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default);
        Assert.Equal([202001, 300001], bag.Where(r => (r.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(r => r.ItemId).Order());
        Assert.DoesNotContain(bag, r => r.ItemId == 300005);
    }

    [Fact]
    public async Task AnExpiredWornCharacterWithoutTheDefaultGrantsItPermanentlyInBothBags()
    {
        var (store, sweep) = await Setup(7, (300005, Past, true), (202001, Future, true));

        await sweep.SweepAsync(7, default);

        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(7, bag, 512, default);
            var granted = Assert.Single(rows, r => r.ItemId == 300001);
            Assert.Equal(InventoryLifetime.Permanent, granted.Lifetime);
        }
        var worn = (await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default))
            .Where(r => (r.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(r => r.ItemId).Order();
        Assert.Equal([202001, 300001], worn);
    }

    [Fact]
    public async Task OtherPlayersAreNeverTouchedAndASecondSweepFindsNothing()
    {
        var table = new MemoryPlayerTable(8);
        var store = new MemoryInventoryRepository(table);
        await store.ReplaceSlotsAsync(7, InventoryBag.CharacterBag, [Row(7, InventoryBag.CharacterBag, 201001, Past)], default);
        await store.ReplaceSlotsAsync(8, InventoryBag.CharacterBag, [Row(8, InventoryBag.CharacterBag, 201001, Past)], default);
        var sweep = new InventoryExpirySweep(store, null, StarterKit.Default, new FixedClock(Now));

        Assert.Equal([201001], await sweep.SweepAsync(7, default));
        Assert.Empty(await sweep.SweepAsync(7, default));

        // Player 7 had no character at all, so the sweep wears the default one; the expired weapon stays gone.
        Assert.Equal([300001], (await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default)).Select(r => r.ItemId));
        Assert.Equal([201001], (await store.ListSlotsAsync(8, InventoryBag.CharacterBag, 512, default)).Select(r => r.ItemId));
    }

    /// <summary>A sweep whose repair failed after the deletion committed is healed by the next sweep.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASweepAfterAFailedRepairStillWearsTheDefaultCharacter(bool refused)
    {
        var (store, _) = await Setup(7, (300005, Past, true), (202001, Future, true));
        var selection = new Selection(store, failFirst: !refused, refuseFirst: refused);
        var sweep = new InventoryExpirySweep(store, selection, StarterKit.Default, new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sweep.SweepAsync(7, default).AsTask());
        Assert.DoesNotContain(await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default),
            row => row.ItemId is >= 300001 and <= 300031 && (row.Flags & InventorySlotRecord.WornFlagBit) != 0);

        Assert.Empty(await sweep.SweepAsync(7, default));
        Assert.Contains(await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default),
            row => row.ItemId == 300001 && (row.Flags & InventorySlotRecord.WornFlagBit) != 0);
        Assert.Equal(2, selection.Applications);
        Assert.Empty(await sweep.SweepAsync(7, default));
        Assert.Equal(2, selection.Applications);
    }

    /// <summary>
    /// A player created at login without a creation stage owns nothing yet; the starter seeding that follows the sweep
    /// gives the bag, so the sweep must not try to wear a lone character under required weapon positions.
    /// </summary>
    [Fact]
    public async Task AFreshPlayerWithAnEmptyBagIsLeftForTheStarterSeeding()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));
        var selection = new Selection(store);
        var sweep = new InventoryExpirySweep(store, selection, StarterKit.Default, new FixedClock(Now));

        Assert.Empty(await sweep.SweepAsync(7, default));

        Assert.Equal(0, selection.Applications);
        Assert.Empty(await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default));
        Assert.Empty(await store.ListSlotsAsync(7, InventoryBag.EquippedBag, 512, default));
    }

    [Fact]
    public async Task ASweepThatRemovedTheLastCharacterStillWearsTheDefault()
    {
        var (store, sweep) = await Setup(7, (300005, Past, true));

        Assert.Equal([300005], await sweep.SweepAsync(7, default));

        Assert.Contains(await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default),
            row => row.ItemId == 300001 && (row.Flags & InventorySlotRecord.WornFlagBit) != 0);
    }

    [Fact]
    public async Task ADeadlineExactlyNowHasEnded()
    {
        var (_, sweep) = await Setup(7, (201001, InventoryLifetime.Until(Now), false));
        Assert.Equal([201001], await sweep.SweepAsync(7, default));
    }

    /// <summary>A targeted discard repairs the character without sweeping unrelated items or reseeding starter weapons.</summary>
    [Fact]
    public async Task TargetedRemovalRepairsTheCharacterWithoutRefillingTheStarterBag()
    {
        var (store, sweep) = await Setup(7, (300005, Past, true), (202001, Past, false), (202002, Future, true));
        IInventoryItemRemoval service = sweep;

        var removed = await service.RemoveItemAsync(7, 300005, default);

        Assert.NotNull(removed);
        Assert.Equal(2, removed.Count);
        Assert.All(removed, row => Assert.Equal(300005, row.ItemId));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(7, bag, 512, default);
            Assert.Equal([202001, 202002, 300001], rows.Select(row => row.ItemId).Order());
            Assert.Equal(InventoryLifetime.Permanent, Assert.Single(rows, row => row.ItemId == 300001).Lifetime);
        }
        var selected = await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default);
        Assert.Equal([202002, 300001], selected.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
            .Select(row => row.ItemId).Order());
        Assert.Empty((await service.RemoveItemAsync(7, 300005, default))!);
    }

    /// <summary>A rejected or missing identity does not trigger default grants or remove other expired items.</summary>
    [Fact]
    public async Task TargetedRefusalAndAbsenceLeaveBothBagsUnchanged()
    {
        var (store, sweep) = await Setup(7, (300005, Future, true), (202001, Past, false));
        var before = await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default);

        Assert.Null(await sweep.RemoveItemAsync(7, 300005, default));
        Assert.Empty((await sweep.RemoveItemAsync(7, 300006, default))!);

        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(before.Select(row => row with { Bag = bag }), await store.ListSlotsAsync(7, bag, 512, default));
    }

    /// <summary>The configured equipment policy restores both views instead of bypassing exclusive selections.</summary>
    [Fact]
    public async Task TargetedCharacterRemovalUsesTheEquipmentSelectionService()
    {
        var (store, _) = await Setup(7, (300005, Past, true));
        var selection = new Selection(store);
        var service = new InventoryExpirySweep(store, selection, StarterKit.Default, new FixedClock(Now));

        Assert.Equal(2, (await service.RemoveItemAsync(7, 300005, default))!.Count);

        Assert.Equal(1, selection.Applications);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var replacement = Assert.Single(await store.ListSlotsAsync(7, bag, 512, default));
            Assert.Equal(300001, replacement.ItemId);
            Assert.Equal(InventoryLifetime.Permanent, replacement.Lifetime);
            Assert.NotEqual(0, replacement.Flags & InventorySlotRecord.WornFlagBit);
        }
    }

    /// <summary>A retry after committed deletion can finish failed equipment repair without deleting or granting twice.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentCharacterRetryCompletesAPreviouslyFailedRepair(bool refused)
    {
        var (store, _) = await Setup(7, (300005, Past, true));
        var selection = new Selection(store, failFirst: !refused, refuseFirst: refused);
        var service = new InventoryExpirySweep(store, selection, StarterKit.Default, new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveItemAsync(7, 300005, default).AsTask());
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(7, bag, 512, default);
            Assert.Equal(300001, Assert.Single(rows).ItemId);
            Assert.DoesNotContain(rows, row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0);
        }

        Assert.Empty((await service.RemoveItemAsync(7, 300005, default))!);
        Assert.Equal(2, selection.Applications);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var repaired = Assert.Single(await store.ListSlotsAsync(7, bag, 512, default));
            Assert.Equal(300001, repaired.ItemId);
            Assert.NotEqual(0, repaired.Flags & InventorySlotRecord.WornFlagBit);
        }
        Assert.Empty((await service.RemoveItemAsync(7, 300005, default))!);
        Assert.Equal(2, selection.Applications);
    }

    /// <summary>A retry completes the second bag if its grant failed after the first bag committed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentCharacterRetryCompletesAPartiallyGrantedDefault(bool useSelection)
    {
        var (store, _) = await Setup(7, (300005, Past, true), (202001, Future, true));
        var interrupted = new InterruptedMirrorStore(store);
        var selection = useSelection ? new Selection(interrupted) : null;
        var service = new InventoryExpirySweep(interrupted, selection, StarterKit.Default, new FixedClock(Now));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveItemAsync(7, 300005, default).AsTask());
        Assert.Same(interrupted.Failure, failure);
        Assert.Contains(await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default), row => row.ItemId == 300001);
        Assert.DoesNotContain(await store.ListSlotsAsync(7, InventoryBag.EquippedBag, 512, default), row => row.ItemId == 300001);
        if (selection is not null) Assert.Equal(0, selection.Applications);

        Assert.Empty((await service.RemoveItemAsync(7, 300005, default))!);
        foreach (var kind in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await store.ListSlotsAsync(7, kind, 512, default);
            Assert.Equal([202001, 300001], rows.Select(row => row.ItemId).Order());
            Assert.Equal(InventoryLifetime.Permanent, Assert.Single(rows, row => row.ItemId == 300001).Lifetime);
            Assert.Equal(Future, Assert.Single(rows, row => row.ItemId == 202001).Lifetime);
        }
        var repaired = await store.ListSlotsAsync(7, InventoryBag.CharacterBag, 512, default);
        Assert.Contains(repaired, IsWornDefault);
        if (selection is not null)
        {
            Assert.Equal(1, selection.Applications);
            Assert.Contains(await store.ListSlotsAsync(7, InventoryBag.EquippedBag, 512, default), IsWornDefault);
        }
        var appends = interrupted.Appends;
        Assert.Empty((await service.RemoveItemAsync(7, 300005, default))!);
        Assert.Equal(appends, interrupted.Appends);
        if (selection is not null) Assert.Equal(1, selection.Applications);

        static bool IsWornDefault(InventorySlotRecord row) => row.ItemId == 300001 && (row.Flags & InventorySlotRecord.WornFlagBit) != 0;
    }

    private sealed class InterruptedMirrorStore(IInventoryRepository inner) : IInventoryRepository
    {
        internal InvalidOperationException Failure { get; } = new("The second default-character grant failed.");
        internal int Appends { get; private set; }
        private bool _failed;

        public ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(long userNum, InventoryBag bag,
            IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
        {
            Appends++;
            if (!_failed && bag == InventoryBag.EquippedBag)
            {
                _failed = true;
                throw Failure;
            }
            return inner.AppendMissingItemsAsync(userNum, bag, candidates, ct);
        }

        public ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId,
            DateTimeOffset now, CancellationToken ct) => inner.RemoveExpiredItemAsync(userNum, itemId, now, ct);
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag,
            int maxCount, CancellationToken ct) => inner.ListSlotsAsync(userNum, bag, maxCount, ct);
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots,
            CancellationToken ct) => inner.ReplaceSlotsAsync(userNum, bag, slots, ct);
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn,
            CancellationToken ct) => inner.SetSlotWornAsync(userNum, itemId, worn, ct);
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
            inner.UpdateWornSelectionAsync(userNum, groups, itemId, worn, ct);
    }

    private sealed class Selection(IInventoryRepository inventory, bool failFirst = false, bool refuseFirst = false) : IEquipmentSelection
    {
        public int Applications { get; private set; }

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);

        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct)
        {
            Applications++;
            if (failFirst && Applications == 1)
                throw new InvalidOperationException("The first equipment repair failed.");
            if (refuseFirst && Applications == 1)
                return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>(null);
            return inventory.UpdateWornSelectionAsync(userNum, [new(300001, 300031, 300001)], itemId, worn, ct);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
