using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Checks login preparation against durable and memory stores with the production required-position selector.</summary>
public sealed class LoginInventoryOrderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyInventorySeedsEveryRequiredPositionBeforeNormalization(bool sqlite)
    {
        using var fixture = await Fixture.CreateAsync(sqlite);
        var (expired, snapshot) = await LoginAsync(fixture.Store, fixture.User);
        Assert.Empty(expired);
        AssertComplete(snapshot);
        var second = await LoginAsync(fixture.Store, fixture.User);
        Assert.Empty(second.Expired);
        Assert.Equal(snapshot.CharacterBag, second.Snapshot.CharacterBag);
        Assert.Equal(snapshot.EquippedBag, second.Snapshot.EquippedBag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialInventoryDropsExpiredRowsBeforeSeedingAndPreservesOwnedWeapons(bool sqlite)
    {
        using var fixture = await Fixture.CreateAsync(sqlite);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await fixture.Store.ReplaceSlotsAsync(fixture.User, bag,
            [
                new(fixture.User, bag, 11, 300005, 1, 1) { Lifetime = InventoryLifetime.Until(Now) },
                new(fixture.User, bag, 12, 203003, 7, 0x81) { Lifetime = InventoryLifetime.Until(Now.AddDays(2)) },
                new(fixture.User, bag, 13, 202001, 1, 1) { Lifetime = InventoryLifetime.Until(Now.AddDays(-1)) },
            ], default);

        var (expired, snapshot) = await LoginAsync(fixture.Store, fixture.User);
        Assert.Equal([202001, 300005], expired.Order());
        Assert.DoesNotContain(snapshot.CharacterBag, row => row.ItemId == 300005 || InventoryExpiry.IsExpired(row, Now));
        var kept = Assert.Single(snapshot.CharacterBag, row => row.ItemId == 203003);
        Assert.Equal((12L, 7, (byte)0x81, InventoryLifetime.Until(Now.AddDays(2))),
            (kept.SlotKey, kept.Quantity, kept.Flags, kept.Lifetime));
        Assert.Equal(InventoryLifetime.Permanent, Assert.Single(snapshot.CharacterBag, row => row.ItemId == 202001).Lifetime);
        Assert.Equal([201001, 203003, 207001, 300001],
            snapshot.CharacterBag.Where(row => (row.Flags & 1) != 0).Select(row => row.ItemId).Order());
        Assert.Equal(snapshot.CharacterBag, snapshot.EquippedBag);
        var second = await LoginAsync(fixture.Store, fixture.User);
        Assert.Empty(second.Expired);
        Assert.Equal(snapshot.CharacterBag, second.Snapshot.CharacterBag);
        Assert.Equal(snapshot.EquippedBag, second.Snapshot.EquippedBag);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryFinishesAnInterruptedMirrorGrantBeforeSelectingEquipment(bool sqlite, bool partiallyPopulated)
    {
        using var fixture = await Fixture.CreateAsync(sqlite);
        if (partiallyPopulated)
            await fixture.Store.ReplaceSlotsAsync(fixture.User, InventoryBag.CharacterBag,
            [
                new(fixture.User, InventoryBag.CharacterBag, 44, 201001, 8, 0),
                new(fixture.User, InventoryBag.CharacterBag, 45, 300005, 1, 1) { Lifetime = InventoryLifetime.Until(Now) },
            ], default);
        var interrupted = new InterruptedMirrorStore(fixture.Store);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LoginAsync(interrupted, fixture.User));
        Assert.Same(interrupted.Failure, failure);
        Assert.Empty(await fixture.Store.ListSlotsAsync(fixture.User, InventoryBag.EquippedBag, 512, default));
        Assert.DoesNotContain(await fixture.Store.ListSlotsAsync(fixture.User, InventoryBag.CharacterBag, 512, default),
            row => row.ItemId == 300005);

        var (expired, snapshot) = await LoginAsync(interrupted, fixture.User);
        Assert.Empty(expired);
        AssertComplete(snapshot, special: !partiallyPopulated);
        if (partiallyPopulated)
        {
            var kept = Assert.Single(snapshot.CharacterBag, row => row.ItemId == 201001);
            Assert.Equal((44L, 8), (kept.SlotKey, kept.Quantity));
        }
        var repeated = (await LoginAsync(interrupted, fixture.User)).Snapshot;
        Assert.Equal(snapshot.CharacterBag, repeated.CharacterBag);
        Assert.Equal(snapshot.EquippedBag, repeated.EquippedBag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingSeedingDoesNotHideMissingRequiredWeapons(bool sqlite)
    {
        using var fixture = await Fixture.CreateAsync(sqlite);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LoginAsync(fixture.Store, fixture.User, seed: false));
        Assert.Contains("Required equipment position lacks its initial item", failure.Message);
        Assert.Empty(await fixture.Store.ListSlotsAsync(fixture.User, InventoryBag.CharacterBag, 512, default));
    }

    private static async Task<(IReadOnlyList<int> Expired, InventorySnapshot Snapshot)> LoginAsync(
        IInventoryRepository store, long user, bool seed = true)
    {
        var selection = new EquipmentSelection(store);
        var resolver = new LoginInventoryResolver(store, seed, selection, clock: new FixedClock());
        var sweep = new InventoryExpirySweep(store, selection, StarterKit.Default, new FixedClock());
        var expired = await sweep.SweepForLoginAsync(user, resolver, default);
        return (expired, await resolver.ResolveAsync(user, default));
    }

    private static void AssertComplete(InventorySnapshot snapshot, bool special = true)
    {
        Assert.Equal(StarterKit.Default.ItemIds.Count, snapshot.CharacterBag.Count);
        Assert.Equal(snapshot.CharacterBag, snapshot.EquippedBag);
        Assert.Equal(snapshot.CharacterBag.Count, snapshot.CharacterBag.Select(row => row.ItemId).Distinct().Count());
        Assert.Equal(StarterKit.Default.WornItemIds.Where(id => special || id != 205001).Order(),
            snapshot.CharacterBag.Where(row => (row.Flags & 1) != 0).Select(row => row.ItemId).Order());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InterruptedMirrorStore(IInventoryRepository inner) : IInventoryRepository
    {
        private bool _failed;
        internal InvalidOperationException Failure { get; } = new("Interrupted starter mirror grant.");
        private void BeforeWrite(InventoryBag bag)
        {
            if (_failed || bag != InventoryBag.EquippedBag) return;
            _failed = true;
            throw Failure;
        }

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long user, InventoryBag bag, int maxCount, CancellationToken ct) =>
            inner.ListSlotsAsync(user, bag, maxCount, ct);
        public ValueTask ReplaceSlotsAsync(long user, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            BeforeWrite(bag);
            return inner.ReplaceSlotsAsync(user, bag, slots, ct);
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(long user, InventoryBag bag,
            IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
        {
            BeforeWrite(bag);
            return inner.AppendMissingItemsAsync(user, bag, candidates, ct);
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long user, DateTimeOffset now, CancellationToken ct) =>
            inner.RemoveExpiredAsync(user, now, ct);
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long user, int item, bool worn, CancellationToken ct) =>
            inner.SetSlotWornAsync(user, item, worn, ct);
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long user,
            IReadOnlyList<InventoryWearGroup> groups, int? item, bool worn, CancellationToken ct) =>
            inner.UpdateWornSelectionAsync(user, groups, item, worn, ct);
    }

    private sealed class Fixture(PluginContext? context, string? directory, IInventoryRepository store, long user) : IDisposable
    {
        internal IInventoryRepository Store => store;
        internal long User => user;

        internal static async Task<Fixture> CreateAsync(bool sqlite)
        {
            if (!sqlite) return new(null, null, new MemoryInventoryRepository(new MemoryPlayerTable(8)), 7);
            var directory = Path.Combine(Path.GetTempPath(), "bff-login-order-" + Guid.NewGuid().ToString("N"));
            var services = new ServiceRegistry();
            var context = new PluginContext(new PluginRow
            {
                Id = "sqlite", Plugin = "persistence.sqlite", Source = "test",
                Config = new Dictionary<string, object?> { ["database_path"] = Path.Combine(directory, "save.db") },
            }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
            try
            {
                await new SqlitePlugin().ActivateAsync(context, default);
                var user = (await services.Get<IPlayerRepository>().CreateAsync("fresh-login", "nickname", default)).UserNum;
                return new(context, directory, services.Get<IInventoryRepository>(), user);
            }
            catch
            {
                context.Unwind();
                throw;
            }
        }

        public void Dispose()
        {
            context?.Unwind();
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
