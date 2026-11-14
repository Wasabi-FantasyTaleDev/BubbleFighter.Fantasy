using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises Article resolution through equipment policy, persistence and the real room policy host.</summary>
public sealed class PetArticleEquipmentTests
{
    /// <summary>Policies see only resolved item ids, and an entitlement removed during policy evaluation cannot be equipped.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ArticleSelectionExposesOnlyItemIdsAndRechecksOwnershipAfterPolicy(int refusal)
    {
        const int petItem = 301001;
        var selectedItem = refusal == 3 ? 203073 : petItem;
        var succeeds = refusal is 0 or 3;
        var rooms = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4));
        var catalog = new[] { 300001, 202001, 201001, 205001, 207001, 203073, petItem };
        var scope = new InventoryLoadoutScope("kr-1256", catalog);
        var rows = catalog.Select((item, index) => new InventorySlotRecord(11, InventoryBag.CharacterBag,
            index + 1, item, 1, item is petItem or 203073 ? (byte)0 : (byte)1) { Lifetime = InventoryLifetime.Permanent }).ToArray();
        await store.ReplaceSlotsAsync(11, InventoryBag.CharacterBag, rows, default);
        var pets = new Entitlements(store, scope, petItem);
        var original = (await pets.PrepareAsync(11, default)).CharacterBag.Single(row => row.ItemId == petItem).PetIdentity!;
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test", Config = config },
                services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        var root = Context("root");
        try
        {
            root.Effect(services.Register<IInventoryRepository>(store));
            root.Effect(services.Register<IPetEntitlements>(pets));
            var localViews = new RoomFlowTests.Sink<LocalEquipmentViewUpdated>();
            root.Effect(services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(localViews));
            root.Effect(services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(new RoomFlowTests.Sink<RemoteEquipmentViewUpdated>()));
            await new PacketRouterPlugin().ActivateAsync(Context("router"), default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(Context("tables"), default);
            root.Effect(services.Get<IOpcodeTableRegistry>().Register(new OpcodeTable(new("test", "kr", 1256, 0, 0, "test"), [])));
            await new InventoryViewPlugin().ActivateAsync(Context("inventoryview", new()
            {
                ["enabled"] = true, ["publish_equipped_view_push"] = true, ["protocol"] = "test/game",
            }), default);
            var policyHost = new RoomPolicyHost(rooms.Registry, "test/game", "kr-1256");
            rooms.Flow.PolicyHost = policyHost;
            var observer = new AppearancePolicy();
            root.Effect(policyHost.Register(observer));
            await rooms.CreateAsync(host);
            root.Effect(events.OnAsync<EquipmentSelectionCommitted>(rooms.Flow.OnEquipmentSelectionCommittedAsync));
            root.Effect(events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (request, next, ct) =>
            {
                if (request.Slot.ItemId != petItem) return await next(ct);
                var progress = request.Slot.Pet!.Find(scope.Key)!;
                return new([new(petItem, 7, [(uint)progress.Grade, (uint)progress.Experience, 0, 0], 0, "")], null);
            }));
            if (refusal == 3)
            {
                root.Effect(events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
                    ValueTask.FromResult(request.Slot.ItemId == 203073
                        ? new InventoryItemPresentation([new(203073, 1, [(uint)request.Slot.Quantity, 0, 0, 0], 0, "")], null)
                        : InventoryItemPresentation.Empty)));
                root.Effect(events.OnAsync<EquipmentSelectionCommitted>(async (_, ct) =>
                {
                    var current = await store.ListSlotsAsync(11, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);
                    await store.ReplaceSlotsAsync(11, InventoryBag.CharacterBag,
                        current.Select(row => row.ItemId == 203073 ? row with { Quantity = 9 } : row).ToArray(), ct);
                }));
            }
            var policyItems = new List<int>();
            root.Effect(events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(async (request, next, ct) =>
            {
                policyItems.Add(request.ItemId);
                if (refusal == 1) return new(false, request.ItemId, request.Worn, "restricted item");
                if (refusal == 2)
                {
                    var current = await store.ListSlotsAsync(11, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);
                    await store.ReplaceSlotsAsync(11, InventoryBag.CharacterBag, current.Where(row => row.ItemId != petItem).ToArray(), ct);
                    await store.AppendMissingItemsAsync(11, InventoryBag.CharacterBag, [rows[^1]], ct);
                    await pets.PrepareAsync(11, ct);
                }
                return await next(ct);
            }));
            var replies = new RoomFlowTests.Sink<EquipmentChangeReplied>();
            var inventories = new RoomFlowTests.Sink<InventoryReplaced>();
            using var tracked = new EquippedItemScope(8, 32);
            var handler = new EquipmentHandlers(tracked, store, new RejectUnexpectedPush(), NullLogger.Instance, replies,
                services.Get<IEquipmentSelection>(), services.Get<IEquippedViewPush>(), events, pets, inventories);
            var request = refusal == 3 ? new EquipmentChangeRequested(selectedItem, selectedItem, true)
                : new EquipmentChangeRequested(checked((long)original.ArticleKey), null, true);
            rooms.SeatUpdated.Sent.Clear();
            await handler.HandleAsync(host, request, default);
            Assert.Equal(new[] { selectedItem }, policyItems);
            Assert.Equal(succeeds, Assert.Single(replies.To(host)).Succeeded);
            var room = policyHost.Find(host.Id)!;
            if (succeeds)
            {
                Assert.Contains(selectedItem, Assert.Single(room.Seats).EquippedItemIds);
                Assert.Contains(selectedItem, Assert.Single(Assert.Single(observer.Changes).Seats).EquippedItemIds);
                var published = Assert.Single(inventories.To(host));
                Assert.True(published.IsCompleteSnapshot);
                Assert.Equal(original, published.CharacterBag.Single(row => row.ItemId == petItem).PetIdentity);
                var firstSeat = Assert.Single(rooms.SeatUpdated.To(host)).Seat;
                Assert.Equal(selectedItem, Assert.Single(firstSeat.ItemAttributes).ItemId);
                Assert.Contains(selectedItem, firstSeat.EquippedItemIds);
                if (refusal == 3)
                {
                    Assert.Equal(1u, Assert.Single(firstSeat.ItemAttributes).Values[0]);
                    Assert.Equal(1, published.CharacterBag.Single(row => row.ItemId == selectedItem).Quantity);
                    Assert.Equal(1u, Assert.Single(Assert.Single(localViews.To(host)).ItemAttributes).Values[0]);
                    Assert.Equal(9, (await store.ListSlotsAsync(11, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default))
                        .Single(row => row.ItemId == selectedItem).Quantity);
                }
            }
            else
            {
                Assert.DoesNotContain(petItem, Assert.Single(room.Seats).EquippedItemIds);
                Assert.Empty(observer.Changes);
                Assert.Empty(inventories.Sent);
                if (refusal == 2)
                    Assert.NotEqual(original, (await pets.PrepareAsync(11, default)).CharacterBag.Single(row => row.ItemId == petItem).PetIdentity);
            }
            Assert.All(room.Seats.SelectMany(seat => seat.EquippedItemIds), item => Assert.True((ulong)item < PetArticleKeys.First));
        }
        finally
        {
            await rooms.Registry.StopAllAsync();
            for (var index = contexts.Count - 1; index >= 0; index--) contexts[index].Unwind();
        }
    }

    private sealed class Entitlements(MemoryInventoryRepository store, InventoryLoadoutScope scope, int petItem) : IPetEntitlements
    {
        /// <summary>Gets the canonical build whose inventory the fixture exposes.</summary>
        public string BuildKey => scope.Key;
        /// <summary>Identifies the single pet item seeded by this fixture.</summary>
        public bool IsPetItem(int itemId) => itemId == petItem;
        /// <summary>Builds the fixture pet's initial grade and skills for an inventory transaction.</summary>
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
            new(userNum, scope, [petItem], new(0, 0, DateTimeOffset.UnixEpoch, null, null, null), now);
        /// <summary>Initializes the fixture pet's durable identity and progression in the shared memory store.</summary>
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) => store.InitializePetsAsync(
            new(userNum, scope, [petItem], new(0, 0, DateTimeOffset.UnixEpoch, null, null, null), DateTimeOffset.UtcNow), ct);
        /// <summary>Finds the requested Article in the fixture's prepared inventory for equipment selection.</summary>
        public async ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct)
        {
            var row = (await PrepareAsync(userNum, ct)).CharacterBag.SingleOrDefault(row => row.PetIdentity?.ArticleKey == articleKey);
            return row is null ? null : new(row.ItemId, row.PetIdentity!);
        }
        /// <summary>Revalidates the Article target and commits its equipment selection through the memory store.</summary>
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            store.SelectPetAsync(new(userNum, scope, groups, target, worn, DateTimeOffset.UtcNow), ct);
        /// <summary>Commits an ordinary item selection together with the fixture's initialized pet metadata.</summary>
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            store.UpdateLoadoutSelectionSnapshotAsync(userNum, scope, groups, itemId, worn, ct, CreateInitialization(userNum, DateTimeOffset.UtcNow));
    }

    private sealed class AppearancePolicy : IRoomPolicy
    {
        internal List<RoomPolicyRoom> Changes { get; } = [];
        /// <summary>Allows fixture room starts so the policy observes equipment publication without adding a hold.</summary>
        public RoomStartDecision EvaluateStart(RoomPolicyRoom room, long requesterSessionId) => RoomStartDecision.Proceed;
        /// <summary>Completes a held-start notification without adding fixture effects.</summary>
        public ValueTask OnStartHeldAsync(RoomPolicyRoom room, long requesterSessionId, CancellationToken ct) => ValueTask.CompletedTask;
        /// <summary>Accepts membership notifications without recording them as appearance changes.</summary>
        public ValueTask OnMemberEnteredAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct) => ValueTask.CompletedTask;
        /// <summary>Records the exact room snapshot delivered after a committed equipment selection.</summary>
        public ValueTask OnAppearanceChangedAsync(RoomPolicyRoom room, long sessionId, CancellationToken ct)
        { Changes.Add(room); return ValueTask.CompletedTask; }
        /// <summary>Keeps recorded appearance snapshots available when the fixture room closes.</summary>
        public void OnRoomClosed(Guid instance) { }
    }

    private sealed class RejectUnexpectedPush : IInventoryPushService
    {
        /// <summary>Rejects a generic inventory push because this path must publish its committed typed snapshot.</summary>
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects a purchase push during an equipment-only fixture operation.</summary>
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) => throw new InvalidOperationException();
        /// <summary>Rejects a slot delta because equipment publication must carry the complete committed snapshot.</summary>
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) => throw new InvalidOperationException();
    }
}
