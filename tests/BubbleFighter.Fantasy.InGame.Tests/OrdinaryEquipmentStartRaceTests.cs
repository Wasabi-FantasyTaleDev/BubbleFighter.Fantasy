using System.IO;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Ordinary equipment persistence that is still open when the host presses start holds the start until it settles.</summary>
public sealed class OrdinaryEquipmentStartRaceTests
{
    private const int OldPrimary = 202001;
    private const int NewPrimary = 203073;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly int[] InitialItems = [300001, OldPrimary, 201001, 205001, 207001];

    /// <summary>
    /// A start pressed while a weapon change is still being written waits for it and then starts once with the new
    /// weapon; without a start in between the waiting seat is refreshed as before.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStartDuringAnEquipmentWriteWaitsAndStartsWithTheCommittedWeapon(bool startWhilePersistenceWaits)
    {
        var rooms = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(2));
        var blocked = new PendingSelectionStore(store, guest.Id, NewPrimary);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        var replies = new RoomFlowTests.Sink<EquipmentChangeReplied>();
        var localViews = new RoomFlowTests.Sink<LocalEquipmentViewUpdated>();
        var push = new SelectionPush();
        var policyChecks = 0;
        EquipmentSelectionCommitted? committed = null;
        Task equipment = Task.CompletedTask;
        using var timeout = new CancellationTokenSource(Limit);
        using var equipped = new EquippedItemScope(2, InventoryLimits.MaxEquippedSlots);
        var ct = timeout.Token;

        PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow
            {
                Id = id, Plugin = id, Source = "test", Config = config ?? [],
            }, services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }

        try
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var rows = InitialItems.Append(NewPrimary).Select((item, index) =>
                    new InventorySlotRecord(guest.Id, bag, index + 1, item, 1,
                        item == NewPrimary ? (byte)0 : InventorySlotRecord.WornFlagBit)
                    { Lifetime = InventoryLifetime.Permanent }).ToArray();
                await store.ReplaceSlotsAsync(guest.Id, bag, rows, ct);
            }

            var root = Context("root");
            root.Effect(services.Register<IInventoryRepository>(blocked));
            root.Effect(services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(localViews));
            root.Effect(services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(
                new RoomFlowTests.Sink<RemoteEquipmentViewUpdated>()));
            await new PacketRouterPlugin().ActivateAsync(root, ct);
            await new OpcodeTableRegistryPlugin().ActivateAsync(root, ct);
            root.Effect(services.Get<IOpcodeTableRegistry>().Register(
                new OpcodeTable(new("test", "kr", 1256, 0, 0, "test"), [])));
            root.Effect(events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>((request, next, token) =>
            {
                Assert.Equal(guest.Id, request.UserNum);
                Assert.Equal(NewPrimary, request.ItemId);
                Interlocked.Increment(ref policyChecks);
                return next(token);
            }));
            root.Effect(events.OnAsync<EquipmentSelectionCommitted>((change, token) =>
            {
                committed = change;
                return rooms.Flow.OnEquipmentSelectionCommittedAsync(change, token);
            }));
            root.Effect(events.OnAsync<EquipmentSelectionStarting>((change, _) =>
            {
                rooms.Flow.OnEquipmentSelectionStarting(change);
                return ValueTask.CompletedTask;
            }));
            root.Effect(events.OnAsync<EquipmentSelectionSettled>(rooms.Flow.OnEquipmentSelectionSettledAsync));
            await new InventoryViewPlugin().ActivateAsync(Context("inventoryview", new()
            {
                ["enabled"] = true,
                ["publish_equipped_view_push"] = true,
                ["protocol"] = "test/game",
            }), ct);
            var handler = new EquipmentHandlers(equipped, blocked, push, NullLogger.Instance, replies,
                services.Get<IEquipmentSelection>(), services.Get<IEquippedViewPush>(), events);

            var roomId = await rooms.CreateAsync(host);
            await rooms.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), ct);
            await rooms.Flow.OnEquipmentSelectionCommittedAsync(new(guest, checked((int)guest.Id), 300001, InitialItems), ct);
            await rooms.Flow.HandleReadyAsync(guest, new(true), ct);
            rooms.SeatUpdated.Sent.Clear();
            var room = Assert.IsType<PlayRoom>(rooms.Registry.Find(roomId));
            Assert.Equal(RoomPhase.Waiting, room.Phase);

            equipment = handler.HandleAsync(guest, new(NewPrimary, NewPrimary, true), ct).AsTask();
            await blocked.Entered.WaitAsync(ct);
            Assert.Equal(1, Volatile.Read(ref policyChecks));
            Assert.False(equipment.IsCompleted);
            Assert.Equal(InitialItems.Order(), await WornAsync(store, guest.Id, InventoryBag.CharacterBag, ct));
            Assert.Empty(replies.Sent);
            Assert.Empty(localViews.Sent);

            if (startWhilePersistenceWaits)
            {
                // The open write holds the start: the room keeps waiting and nobody is sent the old weapon.
                await rooms.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), ct);
                Assert.Equal(RoomPhase.Waiting, room.Phase);
                Assert.False(equipment.IsCompleted);
                Assert.Empty(rooms.Starting.Sent);
            }

            blocked.Release();
            await equipment.WaitAsync(ct);

            var persisted = await WornAsync(store, guest.Id, InventoryBag.CharacterBag, ct);
            Assert.Contains(NewPrimary, persisted);
            Assert.DoesNotContain(OldPrimary, persisted);
            Assert.Equal(persisted, await WornAsync(store, guest.Id, InventoryBag.EquippedBag, ct));
            Assert.True(Assert.Single(replies.To(guest)).Succeeded);
            Assert.Equal(persisted, Assert.Single(localViews.To(guest)).EquippedItemIds.Order());
            Assert.Equal(persisted, Worn(Assert.IsAssignableFrom<IReadOnlyList<InventorySlotRecord>>(push.Last)));
            Assert.Equal(persisted, Assert.IsType<EquipmentSelectionCommitted>(committed).EquippedItemIds.Order());

            var currentSeat = Assert.IsType<RoomSeat>(room.FindByGame(guest.Id));
            if (startWhilePersistenceWaits)
            {
                // The seat was refreshed while the room still waited, then the held start resumed exactly once.
                Assert.Equal(RoomPhase.Starting, room.Phase);
                Assert.Equal(persisted, currentSeat.EquippedItemIds!.Order());
                Assert.Equal(persisted, Assert.Single(rooms.SeatUpdated.To(host)).Seat.EquippedItemIds!.Order());
                var startedSeat = Assert.Single(Assert.Single(rooms.Starting.To(host)).Room.Seats,
                    seat => seat.UserNum == guest.Id);
                Assert.Equal(persisted, startedSeat.EquippedItemIds!.Order());
                Assert.Null(room.HeldStart);
                Assert.Empty(room.PendingSelections);
            }
            else
            {
                Assert.Equal(RoomPhase.Waiting, room.Phase);
                Assert.Equal(persisted, currentSeat.EquippedItemIds!.Order());
                Assert.Equal(persisted, Assert.Single(rooms.SeatUpdated.To(host)).Seat.EquippedItemIds!.Order());
                await rooms.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), ct);
                var startedSeat = Assert.Single(Assert.Single(rooms.Starting.To(host)).Room.Seats,
                    seat => seat.UserNum == guest.Id);
                Assert.Equal(persisted, startedSeat.EquippedItemIds!.Order());
            }
        }
        finally
        {
            blocked.Release();
            try { await equipment.WaitAsync(Limit); }
            finally
            {
                try { await rooms.Registry.StopAllAsync(); }
                finally
                {
                    try { await rooms.Flow.StopScenesAsync(); }
                    finally
                    {
                        for (var index = contexts.Count - 1; index >= 0; index--)
                            await contexts[index].UnwindAsync();
                    }
                }
            }
        }
    }

    /// <summary>
    /// A Starting subscriber that fails, or a cancellation observed between subscribers, after the room has marked the
    /// seat still settles that mark: the failure surfaces, nothing is written, and the room's next start proceeds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailureAfterTheRoomMarkedTheSeatStillSettlesIt(bool cancelInsteadOfThrow)
    {
        var rooms = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var store = new CountingStore(new MemoryInventoryRepository(new MemoryPlayerTable(2)));
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        var replies = new RoomFlowTests.Sink<EquipmentChangeReplied>();
        var settled = 0;
        var laterSubscriberRan = false;
        PlayRoom? room = null;
        using var timeout = new CancellationTokenSource(Limit);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var equipped = new EquippedItemScope(2, InventoryLimits.MaxEquippedSlots);
        var ct = timeout.Token;

        PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow
            {
                Id = id, Plugin = id, Source = "test", Config = config ?? [],
            }, services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }

        try
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                await store.ReplaceSlotsAsync(guest.Id, bag, InitialItems.Append(NewPrimary).Select((item, index) =>
                    new InventorySlotRecord(guest.Id, bag, index + 1, item, 1,
                        item == NewPrimary ? (byte)0 : InventorySlotRecord.WornFlagBit)
                    { Lifetime = InventoryLifetime.Permanent }).ToArray(), ct);

            var root = Context("root");
            root.Effect(services.Register<IInventoryRepository>(store));
            root.Effect(services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(new RoomFlowTests.Sink<LocalEquipmentViewUpdated>()));
            root.Effect(services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(new RoomFlowTests.Sink<RemoteEquipmentViewUpdated>()));
            await new PacketRouterPlugin().ActivateAsync(root, ct);
            await new OpcodeTableRegistryPlugin().ActivateAsync(root, ct);
            root.Effect(services.Get<IOpcodeTableRegistry>().Register(new OpcodeTable(new("test", "kr", 1256, 0, 0, "test"), [])));
            // The room subscriber runs first and installs the mark; the failing subscriber runs after it.
            root.Effect(events.OnAsync<EquipmentSelectionStarting>((change, _) =>
            {
                rooms.Flow.OnEquipmentSelectionStarting(change);
                Assert.Equal(1, room!.PendingSelections.GetValueOrDefault(guest.Id));
                return ValueTask.CompletedTask;
            }, priority: 10));
            if (cancelInsteadOfThrow)
            {
                root.Effect(events.OnAsync<EquipmentSelectionStarting>((_, _) =>
                {
                    request.Cancel();
                    return ValueTask.CompletedTask;
                }, priority: 5));
                root.Effect(events.OnAsync<EquipmentSelectionStarting>((_, _) =>
                {
                    laterSubscriberRan = true;
                    return ValueTask.CompletedTask;
                }, priority: 0));
            }
            else
            {
                root.Effect(events.OnAsync<EquipmentSelectionStarting>(async (_, _) =>
                {
                    await Task.Yield();
                    throw new IOException("A later Starting subscriber failed.");
                }, priority: 0));
            }
            root.Effect(events.OnAsync<EquipmentSelectionSettled>((change, token) =>
            {
                Interlocked.Increment(ref settled);
                return rooms.Flow.OnEquipmentSelectionSettledAsync(change, token);
            }));
            await new InventoryViewPlugin().ActivateAsync(Context("inventoryview", new()
            {
                ["enabled"] = true,
                ["publish_equipped_view_push"] = true,
                ["protocol"] = "test/game",
            }), ct);
            var handler = new EquipmentHandlers(equipped, store, new SelectionPush(), NullLogger.Instance, replies,
                services.Get<IEquipmentSelection>(), services.Get<IEquippedViewPush>(), events);

            var roomId = await rooms.CreateAsync(host);
            await rooms.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), ct);
            await rooms.Flow.OnEquipmentSelectionCommittedAsync(new(guest, checked((int)guest.Id), 300001, InitialItems), ct);
            await rooms.Flow.HandleReadyAsync(guest, new(true), ct);
            room = Assert.IsType<PlayRoom>(rooms.Registry.Find(roomId));

            var equipment = handler.HandleAsync(guest, new(NewPrimary, NewPrimary, true), request.Token).AsTask();
            if (cancelInsteadOfThrow)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => equipment);
                Assert.False(laterSubscriberRan);
            }
            else await Assert.ThrowsAsync<IOException>(() => equipment);

            Assert.Equal(0, Volatile.Read(ref store.SelectionWrites));
            Assert.Equal(1, Volatile.Read(ref settled));
            Assert.Empty(room.PendingSelections);
            Assert.Null(room.HeldStart);
            Assert.Empty(replies.Sent);

            // No hold is left behind: the next start proceeds at once.
            await rooms.Flow.HandleStartAsync(host, new("rel_Village07_Easy02"), ct);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Single(rooms.Starting.To(host));
        }
        finally
        {
            try { await rooms.Registry.StopAllAsync(); }
            finally
            {
                try { await rooms.Flow.StopScenesAsync(); }
                finally
                {
                    for (var index = contexts.Count - 1; index >= 0; index--)
                        await contexts[index].UnwindAsync();
                }
            }
        }
    }

    private static int[] Worn(IReadOnlyList<InventorySlotRecord> rows) =>
        rows.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
            .Select(row => row.ItemId).Order().ToArray();

    private static async Task<int[]> WornAsync(IInventoryRepository store, long userNum, InventoryBag bag,
        CancellationToken ct) => Worn(await store.ListSlotsAsync(userNum, bag, InventoryLimits.MaxInventorySlotsPerBag, ct));

    private sealed class PendingSelectionStore(IInventoryRepository inner, long owner, int target) : IInventoryRepository
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        /// <summary>Pauses only the target ordinary selection immediately before delegating its actual atomic persistence operation.</summary>
        public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
        {
            if (userNum == owner && itemId == target)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return await inner.UpdateWornSelectionAsync(userNum, groups, itemId, worn, ct);
        }

        /// <summary>Reads the real persisted bag without changing the controlled selection wait.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag,
            int maxCount, CancellationToken ct) => inner.ListSlotsAsync(userNum, bag, maxCount, ct);

        /// <summary>Delegates fixture seeding without delaying initialization.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots,
            CancellationToken ct) => inner.ReplaceSlotsAsync(userNum, bag, slots, ct);

        /// <summary>Rejects a direct worn-flag fallback so the test must traverse atomic equipment selection.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("The regression requires the atomic equipment selection path.");
    }

    private sealed class CountingStore(IInventoryRepository inner) : IInventoryRepository
    {
        internal int SelectionWrites;

        /// <summary>Counts atomic selection writes so a request refused before storage can be told apart.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
        {
            Interlocked.Increment(ref SelectionWrites);
            return inner.UpdateWornSelectionAsync(userNum, groups, itemId, worn, ct);
        }

        /// <summary>Reads the real persisted bag.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag,
            int maxCount, CancellationToken ct) => inner.ListSlotsAsync(userNum, bag, maxCount, ct);

        /// <summary>Delegates fixture seeding.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots,
            CancellationToken ct) => inner.ReplaceSlotsAsync(userNum, bag, slots, ct);

        /// <summary>Rejects the direct worn-flag fallback.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("The regression requires the atomic equipment selection path.");
    }

    private sealed class SelectionPush : IInventoryPushService
    {
        internal IReadOnlyList<InventorySlotRecord>? Last { get; private set; }

        /// <summary>Copies the completed bag publication without rereading or changing persistence.</summary>
        public ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Last = slots.ToArray();
            return ValueTask.CompletedTask;
        }

        /// <summary>Rejects full-inventory publication because this request must publish its committed selection.</summary>
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected full inventory publication.");

        /// <summary>Rejects purchase publication because the fixture performs only ordinary equipment selection.</summary>
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants,
            CancellationToken ct) => throw new InvalidOperationException("Unexpected purchase publication.");

        /// <summary>Rejects a single-slot fallback that would omit displaced equipment.</summary>
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected single-slot publication.");
    }
}
