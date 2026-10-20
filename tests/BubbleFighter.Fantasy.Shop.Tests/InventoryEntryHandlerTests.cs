using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Handlers;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the inventory-dialog entry answer: the bag
/// restatement first, then the equipped view through the shared push service — the shop
/// state is not part of it. The view step skips only when nothing wearable exists.
/// </summary>
public sealed class InventoryEntryHandlerTests
{
    private static InventorySlotRecord Row(int itemId, byte flags = 0) =>
        new(42, InventoryBag.CharacterBag, itemId, itemId, 1, flags)
        { Lifetime = InventoryLifetime.Permanent };

    [Fact]
    public async Task EntryRestatesTheStoredBagThenPublishesTheView()
    {
        var order = new List<string>();
        var store = new BagStore([Row(300001, InventorySlotRecord.WornFlagBit), Row(202001, InventorySlotRecord.WornFlagBit), Row(201003)]);
        var replaced = new FakeReplaced(order);
        var view = new FakeViewPush(order);
        var handler = new InventoryEntryHandler(store, replaced, view, NullLogger.Instance);

        await handler.HandleAsync(new Session(7, 42), new ShopInventoryRequested(), default);

        Assert.Equal(["bag", "view"], order);
        var restated = Assert.Single(replaced.Sent);
        Assert.Equal(3, restated.CharacterBag.Count);
        Assert.Empty(restated.EquippedBag);
        var published = Assert.Single(view.Pushed);
        Assert.Equal(42, published.UserNum);
        Assert.Equal(300001, published.CharacterId);
        Assert.Equal([300001, 202001], published.EquippedIds);
    }

    [Fact]
    public async Task EntryWithoutAMountedViewStillRestatesTheBag()
    {
        var store = new BagStore([Row(300001, InventorySlotRecord.WornFlagBit)]);
        var replaced = new FakeReplaced([]);
        var handler = new InventoryEntryHandler(store, replaced, null, NullLogger.Instance);

        await handler.HandleAsync(new Session(9, 42), new ShopInventoryRequested(), default);

        Assert.Single(replaced.Sent);
    }

    [Fact]
    public async Task EntryWithoutAPlayerFailsLoud()
    {
        var handler = new InventoryEntryHandler(new BagStore([]), new FakeReplaced([]), null, NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new Session(11, 0), new ShopInventoryRequested(), default).AsTask());
    }

    [Fact]
    public async Task EntryWithoutAWornCharacterSkipsOnlyTheView()
    {
        var store = new BagStore([Row(202001)]);
        var replaced = new FakeReplaced([]);
        var view = new FakeViewPush([]);
        var handler = new InventoryEntryHandler(store, replaced, view, NullLogger.Instance);

        await handler.HandleAsync(new Session(13, 42), new ShopInventoryRequested(), default);

        Assert.Single(replaced.Sent);
        Assert.Empty(view.Pushed);
    }

    [Fact]
    public async Task EntrySweepsExpiredItemsBeforeTheBagAndNoticesAfterTheView()
    {
        var order = new List<string>();
        var store = new BagStore([Row(300001, InventorySlotRecord.WornFlagBit)]);
        var expiry = new FakeExpiry(order, [204001]);
        var notice = new FakeNotice(order);
        var handler = new InventoryEntryHandler(store, new FakeReplaced(order), new FakeViewPush(order),
            NullLogger.Instance, expiry, notice);

        await handler.HandleAsync(new Session(15, 42), new ShopInventoryRequested(), default);

        Assert.Equal(["sweep", "bag", "view", "notice"], order);
        Assert.Equal([42L], expiry.Users);
        Assert.Equal([204001], Assert.Single(notice.Sent).ItemIds);
    }

    [Fact]
    public async Task EntryWithNothingExpiredSendsNoNotice()
    {
        var order = new List<string>();
        var notice = new FakeNotice(order);
        var handler = new InventoryEntryHandler(new BagStore([Row(300001, InventorySlotRecord.WornFlagBit)]),
            new FakeReplaced(order), null, NullLogger.Instance, new FakeExpiry(order, []), notice);

        await handler.HandleAsync(new Session(17, 42), new ShopInventoryRequested(), default);

        Assert.Equal(["sweep", "bag"], order);
        Assert.Empty(notice.Sent);
    }

    private sealed class FakeExpiry(List<string> order, int[] result) : IInventoryExpiry
    {
        internal List<long> Users { get; } = [];
        public ValueTask<IReadOnlyList<int>> SweepAsync(long userNum, CancellationToken ct)
        {
            order.Add("sweep");
            Users.Add(userNum);
            return ValueTask.FromResult<IReadOnlyList<int>>(result);
        }
    }

    private sealed class FakeNotice(List<string> order) : IMessageSender<ItemsExpired>
    {
        internal List<ItemsExpired> Sent { get; } = [];
        public string Protocol => "kr-812/game";
        public ValueTask SendAsync(ISession session, ItemsExpired message, CancellationToken ct)
        {
            order.Add("notice");
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BagStore(IReadOnlyList<InventorySlotRecord> rows) : IInventoryRepository
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(bag == InventoryBag.CharacterBag ? rows : []);
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReplaced(List<string> order) : IMessageSender<InventoryReplaced>
    {
        internal List<InventoryReplaced> Sent { get; } = [];
        public string Protocol => "kr-812/game";
        public ValueTask SendAsync(ISession session, InventoryReplaced message, CancellationToken ct)
        {
            order.Add("bag");
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeViewPush(List<string> order) : IEquippedViewPush
    {
        internal List<(int UserNum, int CharacterId, int[] EquippedIds)> Pushed { get; } = [];
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            order.Add("view");
            Pushed.Add((userNum, characterId, equippedIds.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long connectionId, long userNum) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(userNum);
        public long Id => 1;
        public IConnection Connection { get; } = new Conn(connectionId);
        public ClientProfile Profile { get; } = new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    private sealed class Conn(long id) : IConnection
    {
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
