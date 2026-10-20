using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the persisted purchase path: atomic charge and grant, inventory push, S2C 106 money update
/// and S2C 816 reply, plus the refusal paths. The wallet mapping is lucci is the S2C 106 field 5 wallet
/// (+0x4C), cash is field 6 (+0x50).
/// </summary>
public sealed class ShopPurchaseTests
{
    private const long SampleUserNum = 42;

    private static readonly ShopCardRow LucciCard = new(
        4000001, "Rifle", "lucci", true, false, 4500, 4500, 0, "Weapon", "Rifle", 200001,
        [new ShopStockRow(3000001, 200001, true, 1, 1, 4500, 0, 4500, 0)], 300, 301);

    private static readonly ShopCardRow CashCard = new(
        4000002, "Cash set", "cash", true, false, 10, 30, 0, "Accessories", "Spray", 200002,
        [new ShopStockRow(3000002, 200002, true, 1, 1, 10, 20, 30, 40)], 200, 205);

    private static ShopCatalog Catalog() => new("test.xml", [LucciCard, CashCard], []);

    private static ShopOptions Options() => new() { ShopItemClientPath = "test.xml" };

    private static ShopPurchaser NewPurchaser(
        FakeMoneyRepository money,
        FakeInventoryRepository inventory,
        FakeInventoryPushService push,
        FakeShopPurchaseStore purchases,
        ShopOptions? options = null) =>
        new(options ?? Options(), Catalog(), purchases.Configure(money, inventory), push, NullLogger.Instance) { Packets = ShopTablePackets.Numbers };

    private static ISession Session(long id) => new RecordingSession(id, SampleUserNum);

    [Fact]
    public async Task SelectedCatalogTermUsesSharedPriceAuditAndConditionalRenewal()
    {
        var table = new Persistence.Memory.MemoryPlayerTable(1);
        var inventory = new Persistence.Memory.MemoryInventoryRepository(table);
        var store = new Persistence.Memory.MemoryShopPurchaseStore(table);
        var stock = new ShopStockRow(1234, 200001, true, 1, 7, 100, 100, 0, 0);
        var card = LucciCard with { StockSetId = 0, Stocks = [stock], SellPrice = 999 };
        var catalog = new ShopCatalog("test.xml", [card], []);
        var terms = new ItemTerms_Legacy(200001, "day", 360, 0, 0);
        var purchaser = new ShopPurchaser(new ShopOptions { ShopItemClientPath = "test.xml", StartingLucci = 1000 },
            catalog, store, null, NullLogger.Instance)
        {
            Packets = ShopTablePackets.Numbers,
            ItemPurchasePolicy = new(new ConditionalShopPurchaser(store, inventory, TimeProvider.System, 2), terms.ForStock),
        };
        var session = new RecordingSession(23, SampleUserNum);
        var offer = catalog.FindPurchase(1234)!;
        var first = await purchaser.CommitOfferAsync(session, SampleUserNum, offer, default);
        var next = await purchaser.CommitOfferAsync(session, SampleUserNum, offer, default);
        Assert.Equal(900, first!.Wallet.Lucci);
        Assert.Equal(800, next!.Wallet.Lucci);
        Assert.Equal(first.Grants[0].SlotKey, next.Grants[0].SlotKey);
        Assert.Equal(first.Grants[0].Lifetime.ExpiresAtUnixMilliseconds + 7 * 86400000L,
            next.Grants[0].Lifetime.ExpiresAtUnixMilliseconds);
        Assert.Single(await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, default));
        Assert.All(await store.ListRecentAsync(SampleUserNum, 10, default), r =>
        {
            Assert.Equal(1234, r.PurchaseKey);
            Assert.Equal(100, r.Price);
        });
        Assert.Empty(session.Sent);
    }

    // The ShopItem terms of the kr-1256 purchase tests: a day item, a permanent day item (stock type 9, duration -1,
    // as kr-1256 sells it) and a count item, in the columns the kr-1256 ShopItem sheet carries.
    private const string Kr1256TermsXml = """
        <?xml version="1.0"?>
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
         <Worksheet ss:Name="ShopItem"><Table>
          <Row><Cell><Data ss:Type="String">itemId</Data></Cell><Cell><Data ss:Type="String">durationType</Data></Cell>
           <Cell><Data ss:Type="String">maxDuration</Data></Cell><Cell><Data ss:Type="String">buyAgain</Data></Cell>
           <Cell><Data ss:Type="String">function</Data></Cell></Row>
          <Row><Cell><Data ss:Type="Number">200001</Data></Cell><Cell><Data ss:Type="String">day</Data></Cell>
           <Cell><Data ss:Type="Number">30</Data></Cell><Cell><Data ss:Type="Number">1</Data></Cell><Cell><Data ss:Type="Number">0</Data></Cell></Row>
          <Row><Cell><Data ss:Type="Number">200002</Data></Cell><Cell><Data ss:Type="String">day</Data></Cell>
           <Cell><Data ss:Type="Number">1</Data></Cell><Cell><Data ss:Type="Number">0</Data></Cell><Cell><Data ss:Type="Number">0</Data></Cell></Row>
          <Row><Cell><Data ss:Type="Number">200003</Data></Cell><Cell><Data ss:Type="String">count</Data></Cell>
           <Cell><Data ss:Type="Number">99</Data></Cell><Cell><Data ss:Type="Number">1</Data></Cell><Cell><Data ss:Type="Number">0</Data></Cell></Row>
         </Table></Worksheet>
        </Workbook>
        """;

    // The reply number by its kr-1256 label, from the build's opcode table rather than a constant.
    private static readonly ushort RepShopBuy = ShopTablePackets.Numbers.ShopBuyReply;

    private static (ShopPurchaser Purchaser, Persistence.Memory.MemoryInventoryRepository Inventory, ShopCatalog Catalog) Kr1256Purchaser()
    {
        var table = new Persistence.Memory.MemoryPlayerTable(1);
        var inventory = new Persistence.Memory.MemoryInventoryRepository(table);
        var store = new Persistence.Memory.MemoryShopPurchaseStore(table);
        ShopCardRow Card(int set, int stock, int item, ushort type, int duration) => LucciCard with
        {
            StockSetId = set, PrimaryItemId = item, SellPrice = 100, ShowPrice = 100,
            Stocks = [new ShopStockRow(stock, item, true, type, duration, 100, 100, 0, 0)],
        };
        var catalog = new ShopCatalog("test.xml", [Card(4000001, 3000001, 200001, 1, 7), Card(4000002, 3000002, 200002, 9, -1),
            Card(4000003, 3000003, 200003, 1, 5)], []);
        var options = new ShopOptions { ShopItemClientPath = "test.xml", StartingLucci = 1000 };
        var policy = ShopPlugin.Kr1256PurchasePolicy(options, ShopWorkbooks.FromXml("kr1256-terms", Kr1256TermsXml), store, inventory);
        return (new ShopPurchaser(options, catalog, store, new FakeInventoryPushService(), NullLogger.Instance) { ItemPurchasePolicy = policy, Packets = ShopTablePackets.Numbers },
            inventory, catalog);
    }

    /// <summary>
    /// A kr-1256 purchase stores the lifetime its ShopItem terms give (days, permanent or a count), never the raw
    /// quantity 99999 with no lifetime that the legacy bag writer of the same shared account could not show.
    /// </summary>
    [Fact]
    public async Task Kr1256PurchasesStoreTheLifetimeOfTheirItemTerms()
    {
        var (purchaser, inventory, _) = Kr1256Purchaser();
        var session = new RecordingSession(31, SampleUserNum);
        var before = DateTimeOffset.UtcNow;
        foreach (var key in new[] { 4000001, 4000002, 4000003, 4000003 })
            await purchaser.PurchaseAsync(session, key, default);

        var bag = (await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, default)).ToDictionary(s => s.ItemId);
        Assert.Equal(InventoryLifetimeKind.ExpiresAt, bag[200001].Lifetime.Kind);
        Assert.InRange(bag[200001].Lifetime.ExpiresAtUnixMilliseconds!.Value,
            before.AddDays(7).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds());
        Assert.Equal(1, bag[200001].Quantity);
        Assert.Equal(InventoryLifetimeKind.Permanent, bag[200002].Lifetime.Kind);
        Assert.Equal(1, bag[200002].Quantity);
        Assert.Equal(InventoryLifetimeKind.Permanent, bag[200003].Lifetime.Kind);
        Assert.Equal(10, bag[200003].Quantity); // two count stocks of 5 add up in one slot
        Assert.DoesNotContain(bag.Values, slot => slot.Lifetime.Kind == InventoryLifetimeKind.Unspecified);
        Assert.All(session.Sent.Where(f => f.Opcode == RepShopBuy),
            reply => Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload.AsSpan(4))));
    }

    /// <summary>A purchase the item terms refuse (a permanent item already owned) gets the failed reply and charges nothing.</summary>
    [Fact]
    public async Task Kr1256PurchaseRefusedByItsTermsRepliesFailureWithoutCharging()
    {
        var (purchaser, inventory, _) = Kr1256Purchaser();
        var session = new RecordingSession(32, SampleUserNum);
        await purchaser.PurchaseAsync(session, 4000002, default);
        session.Sent.Clear();

        await purchaser.PurchaseAsync(session, 4000002, default);

        var reply = Assert.Single(session.Sent);
        Assert.Equal(RepShopBuy, reply.Opcode);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload.AsSpan(4)));
        Assert.Single(await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, default));
    }

    [Fact]
    public async Task SelectedStockChargesItsOwnPriceAndEchoesItsKey()
    {
        var first = new ShopStockRow(1000725, 207026, true, 1, 1, 500, 500, 0, 0);
        var selected = first with { StockId = 1000726, Duration = 7, SellPrice = 2000 };
        var item = new ShopCardRow(0, "Hammer", "lucci", true, true, 500, 500, 0,
            "Weapons", "MeleeWeapon", 207026, [first, selected], 300, 306);
        var unrelatedSet = LucciCard with { Stocks = [LucciCard.Stocks[0], selected] };
        var catalog = new ShopCatalog("test.xml", [unrelatedSet, item], []);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 10000, 100));
        var inventory = new FakeInventoryRepository();
        var audit = new FakeShopPurchaseStore();
        var session = new RecordingSession(12, SampleUserNum);
        var purchaser = new ShopPurchaser(Options(), catalog, audit.Configure(money, inventory),
            new FakeInventoryPushService(), NullLogger.Instance) { Packets = ShopTablePackets.Numbers };

        await purchaser.PurchaseAsync(session, 1000726, CancellationToken.None);

        Assert.Equal(8000, (await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None))!.Lucci);
        var grant = Assert.Single(await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None));
        Assert.Equal(207026, grant.ItemId);
        Assert.Equal(2000, Assert.Single(audit.Rows).Price);
        Assert.Equal(0, audit.Rows[0].StockSetId);
        Assert.Equal(ShopWire.RepShopBuy(1000726, 1, string.Empty), session.Sent[^1].Payload);
    }

    [Fact]
    public async Task StockSetGrantsEveryIncludedStockAtOneSetPrice()
    {
        var set = LucciCard with { Stocks = [LucciCard.Stocks[0], CashCard.Stocks[0]] };
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 10000, 100));
        var inventory = new FakeInventoryRepository();
        var audit = new FakeShopPurchaseStore();
        var purchaser = new ShopPurchaser(Options(), new ShopCatalog("test.xml", [set], []), audit.Configure(money, inventory), new FakeInventoryPushService(), NullLogger.Instance) { Packets = ShopTablePackets.Numbers };

        await purchaser.PurchaseAsync(Session(13), set.StockSetId, CancellationToken.None);

        var bag = await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None);
        Assert.Equal(new[] { 200001, 200002 }, bag.Select(s => s.ItemId));
        Assert.Equal(5500, (await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None))!.Lucci);
        Assert.Single(audit.Rows);
    }

    [Fact]
    public void PurchaseLookupDoesNotTreatItemIdsOrBundledGiftsAsPurchases()
    {
        var catalog = Catalog();
        Assert.Null(catalog.FindPurchase(200001));
        Assert.Null(catalog.FindPurchase(3000001));
        Assert.Equal(30, catalog.FindPurchase(4000002)!.Price);
    }

    [Fact]
    public async Task PersistedPurchaseChargesLucciGrantsBagAndPushes()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));
        var inventory = new FakeInventoryRepository();
        var push = new FakeInventoryPushService();
        var purchases = new FakeShopPurchaseStore();

        await NewPurchaser(money, inventory, push, purchases)
            .PurchaseAsync(Session(1), 4000001, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(100_000 - 4500, wallet!.Lucci);
        Assert.Equal(0, wallet.Cash);

        var purchase = Assert.Single(purchases.Rows);
        Assert.Equal(SampleUserNum, purchase.UserNum);
        Assert.Equal(4000001, purchase.StockSetId);
        Assert.Equal(200001, purchase.ItemId);
        Assert.Equal("lucci", purchase.MoneyType);
        Assert.Equal(4500, purchase.Price);

        var bag = await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None);
        var slot = Assert.Single(bag);
        Assert.Equal(200001, slot.ItemId);
        Assert.Equal(99999, slot.Quantity);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), slot.SlotKey);
        Assert.Equal((byte)0, slot.Flags);

        Assert.Equal(SampleUserNum, push.LastUserNum);
    }

    [Fact]
    public async Task ReplyEchoesPurchaseKeyAfterTheWalletPush()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 500));
        var inventory = new FakeInventoryRepository();
        var push = new FakeInventoryPushService();
        var session = Session(2);

        await NewPurchaser(money, inventory, push, new FakeShopPurchaseStore())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var frames = ((RecordingSession)session).Sent;
        Assert.Equal(2, frames.Count);

        var (opcode, payload) = frames[^1];
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        Assert.Equal(816, opcode);
        var r = new PacketReader(payload);
        Assert.Equal(4000001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());
        r.ReadByte();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        Assert.Equal(0, r.Remaining);
        Assert.Equal(25, payload.Length);

        (opcode, payload) = frames[0];
        Assert.Equal(ShopTablePackets.Numbers.UserMoneyStat, opcode);
        Assert.Equal(106, opcode);
        var m = new PacketReader(payload);
        Assert.Equal(0, m.ReadInt32());
        Assert.Equal(0, m.ReadInt32());
        Assert.Equal(0, m.ReadInt32());
        Assert.Equal(0, m.ReadInt32());
        Assert.Equal(100_000 - 4500, m.ReadInt32());
        Assert.Equal(500, m.ReadInt32());
        Assert.Equal(0, m.Remaining);
    }

    [Fact]
    public async Task CashPricedPurchaseChargesTheCashWallet()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 10, 100));
        var inventory = new FakeInventoryRepository();

        await NewPurchaser(money, inventory, new FakeInventoryPushService(), new FakeShopPurchaseStore())
            .PurchaseAsync(Session(3), 4000002, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(10, wallet!.Lucci);
        Assert.Equal(70, wallet.Cash);
    }

    [Fact]
    public async Task LegacyCashCnTermChargesCashForTheSelectedStock()
    {
        var oneDay = new ShopStockRow(1000001, 100001, true, 1, 1, 100, 100, 0, 0);
        var sevenDays = oneDay with { StockId = 1000002, Duration = 7, SellPrice = 600 };
        var card = new ShopCardRow(0, "Character", "cashCN", true, false, 100, 100, 0,
            "Characters", "Characters", 100001, [oneDay, sevenDays], 400, 401);
        var catalog = new ShopCatalog("test.xml", [card], []);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 1000, 1000));
        var inventory = new FakeInventoryRepository();
        var purchases = new FakeShopPurchaseStore();
        var purchaser = new ShopPurchaser(Options(), catalog, purchases.Configure(money, inventory),
            new FakeInventoryPushService(), NullLogger.Instance) { Packets = ShopTablePackets.Numbers };

        await purchaser.PurchaseAsync(Session(4), sevenDays.StockId, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(1000, wallet!.Lucci);
        Assert.Equal(400, wallet.Cash);
        var audit = Assert.Single(purchases.Rows);
        Assert.Equal("cash", audit.MoneyType);
        Assert.Equal(600, audit.Price);
        Assert.Equal(sevenDays.StockId, audit.PurchaseKey);
        Assert.Equal(100001, Assert.Single(await inventory.ListSlotsAsync(
            SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None)).ItemId);
    }

    [Fact]
    public async Task CommittedOfferLeavesInventoryAndWalletPublicationToTheDialect()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 1000, 1000));
        var inventory = new FakeInventoryRepository();
        var audit = new FakeShopPurchaseStore();
        var session = new RecordingSession(14, SampleUserNum);
        var purchaser = new ShopPurchaser(Options(), Catalog(), audit.Configure(money, inventory),
            null, NullLogger.Instance);

        var result = await purchaser.CommitOfferAsync(session, SampleUserNum,
            Catalog().FindPurchase(4000002)!, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(970, result.Wallet.Cash);
        Assert.Equal(200002, Assert.Single(result.Grants).ItemId);
        Assert.Equal("cash", Assert.Single(audit.Rows).MoneyType);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task InsufficientLucciRefusesWithoutChargingOrGranting()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100, 0));
        var inventory = new FakeInventoryRepository();
        var purchases = new FakeShopPurchaseStore();
        var session = Session(4);

        await NewPurchaser(money, inventory, new FakeInventoryPushService(), purchases)
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.Equal(100, wallet!.Lucci);
        Assert.Empty(purchases.Rows);
        Assert.Empty(await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None));

        var (opcode, payload) = Assert.Single(((RecordingSession)session).Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        var r = new PacketReader(payload);
        Assert.Equal(4000001, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
    }

    [Fact]
    public async Task MissingWalletRowIsSeededWithTheStartingBalances()
    {
        var money = new FakeMoneyRepository(null);
        var session = Session(5);

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(100_000 - 4500, wallet!.Lucci);
        Assert.Equal(0, wallet.Cash);

        var frames = ((RecordingSession)session).Sent;
        var (opcode, payload) = frames[^1];
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        var r = new PacketReader(payload);
        r.ReadInt32();
        Assert.Equal(1, r.ReadInt32());
    }

    [Fact]
    public async Task StartingCashBuysTheCashCard()
    {
        var money = new FakeMoneyRepository(null);

        await NewPurchaser(
            money,
            new FakeInventoryRepository(),
            new FakeInventoryPushService(),
            new FakeShopPurchaseStore(),
            new ShopOptions { ShopItemClientPath = "test.xml", StartingLucci = 0, StartingCash = 30 })
            .PurchaseAsync(Session(11), 4000002, CancellationToken.None);

        var wallet = await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None);
        Assert.NotNull(wallet);
        Assert.Equal(0, wallet!.Lucci);
        Assert.Equal(0, wallet.Cash);
    }

    [Fact]
    public async Task UnknownKeyRepliesResultZeroWithEmptyName()
    {
        var session = Session(6);
        await NewPurchaser(new FakeMoneyRepository(null), new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore())
            .PurchaseAsync(session, 12345, CancellationToken.None);

        var (opcode, payload) = Assert.Single(((RecordingSession)session).Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        Assert.Equal(
            ShopWire.RepShopBuyRefused(12345),
            payload);
    }

    [Fact]
    public async Task NoLobbyIdentityRepliesResultZeroWithoutPersisting()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));
        var purchases = new FakeShopPurchaseStore();
        var session = new RecordingSession(7, 0);

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), purchases)
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        Assert.Empty(purchases.Rows);
        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        var r = new PacketReader(payload);
        r.ReadInt32();
        Assert.Equal(0, r.ReadInt32());
    }

    [Fact]
    public async Task SecondPurchaseAppendsWithTheNextSlotKey()
    {
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));
        var inventory = new FakeInventoryRepository();
        var purchaser = NewPurchaser(money, inventory, new FakeInventoryPushService(), new FakeShopPurchaseStore());

        await purchaser.PurchaseAsync(Session(8), 4000001, CancellationToken.None);
        await purchaser.PurchaseAsync(Session(9), 4000001, CancellationToken.None);

        var bag = await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None);
        Assert.Equal(2, bag.Count);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), bag[0].SlotKey);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 1), bag[1].SlotKey);
    }

    [Fact]
    public async Task FullBagRefusesBeforeCharging()
    {
        var full = new List<InventorySlotRecord>();
        for (var i = 0; i < InventoryLimits.MaxInventorySlotsPerBag; i++)
            full.Add(new InventorySlotRecord(SampleUserNum, InventoryBag.CharacterBag, i, 200001, 99999, 0));
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));
        var inventory = new FakeInventoryRepository(full);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NewPurchaser(money, inventory, new FakeInventoryPushService(), new FakeShopPurchaseStore())
                .PurchaseAsync(Session(10), 4000001, CancellationToken.None));
        Assert.Equal(100_000, (await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None))!.Lucci);
    }

    [Fact]
    public void PurchaseSlotKeyStaysUniqueAndAboveTheStarterMarkers()
    {
        var first = ShopPurchaser.PurchaseSlotKey(1, 0);
        var second = ShopPurchaser.PurchaseSlotKey(1, 1);
        var other = ShopPurchaser.PurchaseSlotKey(2, 0);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, other);
        Assert.True(first > unchecked((long)0x40F86A0000000000));
        Assert.True(first < ShopPurchaser.PurchaseKeyBase + (1L << 32));
    }

    [Fact]
    public void GrantOptionDefaultsOn()
    {
        var options = new ShopOptions { ShopItemClientPath = "test.xml" };
        Assert.True(options.GrantPurchasedItems);
        Assert.Empty(options.Validate());
    }

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

    private sealed class FakeMoneyRepository(PlayerWallet? stored) : IPlayerWalletRepository
    {
        private PlayerWallet? _stored = stored;

        public ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(_stored is not null && _stored.UserNum == userNum ? _stored : null);

        public ValueTask UpsertAsync(PlayerWallet record, CancellationToken ct)
        {
            _stored = record;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInventoryRepository(List<InventorySlotRecord>? seed = null) : IInventoryRepository
    {
        private readonly Dictionary<(long, InventoryBag), List<InventorySlotRecord>> _slots = [];

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
            long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
        {
            if (!_slots.TryGetValue((userNum, bag), out var rows))
            {
                rows = seed is not null && bag == InventoryBag.CharacterBag ? [.. seed] : [];
                _slots[(userNum, bag)] = rows;
            }

            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(rows.Take(maxCount).ToList());
        }

        public ValueTask ReplaceSlotsAsync(
            long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            _slots[(userNum, bag)] = [.. slots];
            return ValueTask.CompletedTask;
        }

        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct)
        {
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

    private sealed class FakeInventoryPushService : IInventoryPushService
    {
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) =>
            PushInventoryAsync(session, userNum, ct);

        public long LastUserNum { get; private set; }

        public int PushCount { get; private set; }

        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct)
        {
            LastUserNum = userNum;
            PushCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeShopPurchaseStore : IShopPurchaseStore
    {
        private FakeMoneyRepository _money = null!;
        private FakeInventoryRepository _inventory = null!;

        public FakeShopPurchaseStore Configure(FakeMoneyRepository money, FakeInventoryRepository inventory)
        {
            _money = money;
            _inventory = inventory;
            return this;
        }

        public async ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct)
        {
            command.Validate();
            var user = command.SeedWallet.UserNum;
            var wallet = await _money.FindByUserNumAsync(user, ct) ?? command.SeedWallet;
            var balance = command.MoneyType == "cash" ? wallet.Cash : wallet.Lucci;
            if (balance < command.Price) return null;
            var bag = await _inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, ct);
            if (bag.Count + command.Grants.Count > 512) throw new InvalidOperationException("Full bag.");
            var grants = command.Grants.Select((g, i) => new InventorySlotRecord(user, InventoryBag.CharacterBag,
                command.SlotKeyBase + bag.Count + i, g.ItemId, g.Quantity, 0)).ToArray();
            var charged = command.MoneyType == "cash" ? wallet with { Cash = balance - command.Price }
                : wallet with { Lucci = balance - command.Price };
            await _money.UpsertAsync(charged, ct);
            await _inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, bag.Concat(grants).ToArray(), ct);
            Rows.Add(new(user, command.StockSetId, grants[0].ItemId, command.MoneyType, command.Price,
                grants[0].SlotKey, command.PurchaseKey));
            return new(charged, grants);
        }

        public List<ShopPurchaseRecord> Rows { get; } = [];

        public ValueTask RememberAsync(ShopPurchaseRecord record, CancellationToken ct)
        {
            Rows.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ShopPurchaseRecord>>(Rows.Where(r => r.UserNum == userNum).Take(maxCount).ToList());
    }
}
