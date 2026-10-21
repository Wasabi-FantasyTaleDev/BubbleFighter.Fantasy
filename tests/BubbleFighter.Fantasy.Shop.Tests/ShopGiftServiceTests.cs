using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Pins free gifts: off-sale stocks under item terms, Lucci-only sets, refusals and the purchase-style publish.</summary>
public sealed class ShopGiftServiceTests
{
    private const long UserNum = 42;

    // kr-812 Stock 1001622 (count item 205002, 200 shots, off sale) and StockSet 2000373 (5000 Lucci, no stocks).
    private static readonly ShopStockRow CountStock = new(1001622, 205002, false, 1, 200, 0, 0, 0, 0);

    private static (ShopGiftService Gifts, MemoryInventoryRepository Inventory, RecordingStateSender State) Create()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var store = new MemoryShopPurchaseStore(table);
        var catalog = new ShopCatalog("test.xml", [], [])
        {
            Gifts = new Dictionary<int, ShopGift>
            {
                [CountStock.StockId] = new(CountStock.StockId, 0, 0, [CountStock]),
                [2000373] = new(2000373, 2000373, 5000, []),
                [1999999] = new(1999999, 0, 0, [new ShopStockRow(1999999, 999999, false, 1, 7, 0, 0, 0, 0)]),
            },
        };
        var terms = new Dictionary<int, ItemTerms>
        {
            [205002] = new(205002, "count", 300, 0, 0),
        };
        var state = new RecordingStateSender();
        var gifts = new ShopGiftService(new ShopOptions { ShopItemClientPath = "test.xml", StartingLucci = 1000 }, catalog,
            new ConditionalShopPurchaser(store, inventory, TimeProvider.System, 2), store,
            stock => terms.TryGetValue(stock.ItemId, out var term) ? term.ForStock(stock)
                : throw new PurchaseRejectedException($"Item {stock.ItemId} has no purchase terms."),
            state, NullLogger.Instance);
        return (gifts, inventory, state);
    }

    [Fact]
    public async Task OffSaleStockAndLucciSetAreGivenFreeAndPublished()
    {
        var (gifts, inventory, state) = Create();
        var session = new RecordingSession(UserNum);
        Assert.True(gifts.CanGrant(1001622));
        Assert.True(gifts.CanGrant(2000373));
        Assert.False(gifts.CanGrant(1999999));
        Assert.False(gifts.CanGrant(1234));

        Assert.True(await gifts.GrantAsync(session, UserNum, 1001622, default));
        Assert.True(await gifts.GrantAsync(session, UserNum, 2000373, default));

        var slot = Assert.Single(await inventory.ListSlotsAsync(UserNum, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(205002, slot.ItemId);
        Assert.Equal(200, slot.Quantity);
        Assert.Equal(2, state.Sent.Count);
        Assert.Equal(205002, Assert.Single(state.Sent[0].Slots).ItemId);
        Assert.False(state.Sent[0].FullInventory);
        // Nothing is charged: the seeded 1000 Lucci only grow by the set's 5000.
        Assert.Equal(1000, state.Sent[0].Wallet.Lucci);
        Assert.Empty(state.Sent[1].Slots);
        Assert.Equal(6000, state.Sent[1].Wallet.Lucci);
    }

    [Fact]
    public async Task GiftOverTheItemLimitIsRefusedWithoutChanges()
    {
        var (gifts, inventory, state) = Create();
        var session = new RecordingSession(UserNum);
        Assert.True(await gifts.GrantAsync(session, UserNum, 1001622, default));
        // 200 owned plus 200 more would pass the item's 300-shot limit.
        Assert.False(await gifts.GrantAsync(session, UserNum, 1001622, default));
        Assert.Equal(200, Assert.Single(await inventory.ListSlotsAsync(UserNum, InventoryBag.CharacterBag, 512, default)).Quantity);
        Assert.Single(state.Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await gifts.GrantAsync(session, UserNum, 1234, default));
    }

    private sealed class RecordingStateSender : IMessageSender<ShopStateUpdated>
    {
        public string Protocol => "test";
        public List<ShopStateUpdated> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, ShopStateUpdated message, CancellationToken ct)
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSession(long userNum) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(userNum);
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
