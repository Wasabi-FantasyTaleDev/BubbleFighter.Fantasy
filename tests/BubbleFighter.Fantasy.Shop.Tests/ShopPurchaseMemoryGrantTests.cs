using BubbleFighter.Fantasy.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Exercises atomic memory purchases through the real inventory and purchase wire services.</summary>
public sealed class ShopPurchaseMemoryGrantTests
{
    private const long SampleUserNum = 42;

    private static readonly IOpcodeTable Ops = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"),
        new ClientProfile("kr-1256", "kr", 1256, 1304, 0, "kr-1256")).ForConnection(OpcodeConnection.Game);

    private static readonly ShopCardRow LucciCard = new(
        4000001, "Rifle", "lucci", true, false, 4500, 4500, 0, "Weapon", "Rifle", 200001,
        [new ShopStockRow(3000001, 200001, true, 1, 1, 4500, 0, 4500, 0)], 300, 301);

    private static ShopCatalog Catalog() => new("test.xml", [LucciCard], []);

    private static ShopOptions Options() => new() { ShopItemClientPath = "test.xml" };

    private static MemoryGrantFixture NewFixture(ShopOptions? options = null)
    {
        var table = new MemoryPlayerTable(16);
        var money = new MemoryPlayerWalletRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var binding = new BubbleFighter.Fantasy.Dialect.Messages.InventoryBinding("kr-1256/game", Ops,
            BubbleFighter.Fantasy.Packets.PacketTables.Load(BubbleFighter.Fantasy.Tests.SharedDialectRows.PacketsDirectory()),
            "kr-1256");
        var push = new InventoryPush(new LoginInventoryResolver(inventory, seedStarterInventory: false), binding, binding, binding);
        var purchases = new MemoryShopPurchaseStore(table);
        var purchaser = new ShopPurchaser(
            options ?? Options(), Catalog(), purchases, push, NullLogger.Instance) { Packets = ShopTablePackets.Numbers };
        return new MemoryGrantFixture(purchaser, money, inventory, purchases);
    }

    private static InventorySlotRecord GrantedSlot(int ordinal) => new(
        SampleUserNum,
        InventoryBag.CharacterBag,
        ShopPurchaser.PurchaseSlotKey(SampleUserNum, ordinal),
        200001,
        ShopPurchaser.ReferenceField2,
        0);

    [Fact]
    public async Task PurchaseOverTheMemoryRowChargesGrantsAndPushesInOrder()
    {
        var fixture = NewFixture();
        await fixture.Money.UpsertAsync(
            new PlayerWallet(SampleUserNum, 100_000, 500), CancellationToken.None);
        var session = new RecordingSession(1, SampleUserNum);

        await fixture.Purchaser.PurchaseAsync(session, 4000001, CancellationToken.None);

        // The store side: the wallet is charged in the memory row and the
        // granted slot is the only character-bag row.
        var wallet = await fixture.Money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(100_000 - 4500, wallet!.Lucci);
        Assert.Equal(500, wallet.Cash);
        var bag = await fixture.Inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        var slot = Assert.Single(bag);
        Assert.Equal(200001, slot.ItemId);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), slot.SlotKey);
        var purchase = Assert.Single(await fixture.Purchases.ListRecentAsync(SampleUserNum, 16, CancellationToken.None));
        Assert.Equal(4000001, purchase.StockSetId);
        Assert.Equal(200001, purchase.ItemId);
        Assert.Equal(4500, purchase.Price);

        // Inventory, money, then the modal reply.
        var frames = session.Sent;
        Assert.Equal(
            new ushort[]
            {
                (ushort)802,
                (ushort)803,
                ShopTablePackets.Numbers.UserMoneyStat,
                ShopTablePackets.Numbers.ShopBuyReply,
            },
            frames.Select(f => f.Opcode).ToArray());

        // S2C 816 echoes the purchase key; all optional fields are empty.
        var (buyOpcode, buyPayload) = frames[^1];
        Assert.Equal(816, buyOpcode);
        var buy = new PacketReader(buyPayload);
        Assert.Equal(4000001, buy.ReadInt32());
        Assert.Equal(1, buy.ReadInt32());
        Assert.Equal(string.Empty, buy.ReadWideStringPrefixed());
        buy.ReadByte();
        buy.ReadInt32();
        Assert.Equal(0, buy.ReadInt32());
        buy.ReadInt32();
        Assert.Equal(0, buy.Remaining);

        // S2C 106: six i32, field 5 = charged lucci, field 6 = cash.
        var (_, moneyPayload) = frames[2];
        var money = new PacketReader(moneyPayload);
        Assert.Equal(0, money.ReadInt32());
        Assert.Equal(0, money.ReadInt32());
        Assert.Equal(0, money.ReadInt32());
        Assert.Equal(0, money.ReadInt32());
        Assert.Equal(100_000 - 4500, money.ReadInt32());
        Assert.Equal(500, money.ReadInt32());
        Assert.Equal(0, money.Remaining);

        // S2C 802 and S2C 803 both contain the complete bag: the modern mirror bag with the granted slot
        // (the bodies the shared inventory binding publishes; the login-burst slice moved their writer off
        // the modern login codec, so the pinned bytes are stated here).
        Assert.Equal("1C00000078016364606070E4656698DF066269313064FF706080010034480346",
            Convert.ToHexString(frames[0].Payload));
        Assert.Equal("1C00000078016364606070E4656698DF066269313064FF70604007004E780346",
            Convert.ToHexString(frames[1].Payload));
    }

    [Fact]
    public async Task InsufficientFundsOverTheMemoryRowRefusesAndPersistsNothing()
    {
        var fixture = NewFixture();
        await fixture.Money.UpsertAsync(
            new PlayerWallet(SampleUserNum, 100, 0), CancellationToken.None);
        var session = new RecordingSession(2, SampleUserNum);

        await fixture.Purchaser.PurchaseAsync(session, 4000001, CancellationToken.None);

        var wallet = await fixture.Money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.Equal(100, wallet!.Lucci);
        Assert.Empty(await fixture.Inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));
        Assert.Empty(await fixture.Purchases.ListRecentAsync(SampleUserNum, 16, CancellationToken.None));

        // One refusal frame, no wallet push, no inventory push.
        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        var buy = new PacketReader(payload);
        Assert.Equal(4000001, buy.ReadInt32());
        Assert.Equal(0, buy.ReadInt32());
    }

    [Fact]
    public async Task FirstPurchaseSeedsTheMemoryWalletThenChargesTheStoredRow()
    {
        var fixture = NewFixture();
        var session = new RecordingSession(3, SampleUserNum);

        // First buy: no memory row exists, so the purchaser seeds the row
        // tunables and charges them in one step.
        await fixture.Purchaser.PurchaseAsync(session, 4000001, CancellationToken.None);
        // Second buy: the seeded row is now the store of record, so the charge
        // lands on it instead of re-seeding.
        await fixture.Purchaser.PurchaseAsync(session, 4000001, CancellationToken.None);

        var wallet = await fixture.Money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(100_000 - (2 * 4500), wallet!.Lucci);
        Assert.Equal(0, wallet.Cash);

        var bag = await fixture.Inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(2, bag.Count);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), bag[0].SlotKey);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 1), bag[1].SlotKey);

        // Both purchases travel the full grant order, so each bought item
        // reached the client when it was granted.
        var grantOrder = new ushort[]
        {
            (ushort)802,
            (ushort)803,
                ShopTablePackets.Numbers.UserMoneyStat,
                ShopTablePackets.Numbers.ShopBuyReply,
        };
        Assert.Equal(grantOrder.Concat(grantOrder), session.Sent.Select(f => f.Opcode));
    }

    private sealed record MemoryGrantFixture(
        ShopPurchaser Purchaser,
        MemoryPlayerWalletRepository Money,
        MemoryInventoryRepository Inventory,
        MemoryShopPurchaseStore Purchases);

    private sealed class RecordingSession(long id, long userNum) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(userNum);

        public long Id { get; } = id;

        public IConnection Connection { get; } = new StubConnection();

        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public List<(ushort Opcode, byte[] Payload)> Sent { get; } = [];

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record FakePlayer(long UserNum);

    private sealed class StubConnection : IConnection
    {
        public long Id => 0;

        public System.Net.EndPoint RemoteEndPoint { get; } = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);

        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();

        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();

        public ConcurrentDictionary<string, object> Items { get; } = new();

        public bool IsClosed => false;

        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
