using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Handlers;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the held container-2 equip delivery: the S2C 803 body bytes, the
/// C2S 803 flush trigger, the per-player cap and the option default. The kr-1256 client writes the
/// my-fighter weapon category only when the player opens the inventory dialog,
/// and stamps C2S 803 right after, so the server holds container-2 records until
/// that packet arrives.
/// </summary>
public sealed class EquipHoldTests
{
    private const long SampleUserNum = 42;

    private static readonly ShopCardRow LucciCard = new(
        4000001, "Rifle", "lucci", true, false, 4500, 4500, 0, "Weapon", "Rifle", 200001,
        [new ShopStockRow(3000001, 200001, true, 1, 1, 4500, 0, 4500, 0)], 300, 301);

    private static ShopCatalog Catalog() => new("test.xml", [LucciCard], []);

    private static ShopOptions HoldOptions() => new()
    {
        ShopItemClientPath = "test.xml",
        HoldEquipUntilInventoryOpen = true,
    };

    private static ShopPurchaser NewPurchaser(
        FakeMoneyRepository money,
        FakeInventoryRepository inventory,
        FakeInventoryPushService push,
        FakeShopPurchaseStore purchases,
        EquipHoldStore hold,
        ShopOptions options) =>
        new(options, Catalog(), purchases.Configure(money, inventory), push, NullLogger.Instance, hold) { Packets = ShopTablePackets.Numbers };

    private static RecordingSession Session(long id) => new(id, SampleUserNum);

    /// <summary>One held record: the identity a first purchase of the sample card produces.</summary>
    private static EquippedSlotRecord FirstHeldRecord() =>
        new(SampleUserNum, ShopPurchaser.EquipHoldKey(SampleUserNum, 0), 200001, ShopPurchaser.EquipRecordState, false);

    [Fact]
    public void ContainerTwoBodyPinsThirtySixBytesForOneRecord()
    {
        var body = ShopWire.InventoryUpdateSlotsEquipHold([FirstHeldRecord()]);

        Assert.Equal(ShopWire.EquipHoldSingleRecordBytes, body.Length);
        Assert.Equal(36, body.Length);
        Assert.Equal(
            "00000000"                                  // container 1 count: no bag change
            + "01000000"                                // container 2 count: one record
            + "00002A00006BF840"                        // unknown10 = EquipHoldKey(42, 0)
            + "410D0300"                                // unknown11 = 200001
            + "01000000"                                // unknown12 = 1 (equip)
            + "00000000"                                // unknown13 = 0
            + "00000000"                                // nested vector count 0
            + "00000000",                               // container 3 count: removal list, empty
            Convert.ToHexString(body));
    }

    [Fact]
    public void ContainerTwoBodyAddsTwentyFourBytesPerExtraRecord()
    {
        var second = new EquippedSlotRecord(
            SampleUserNum, ShopPurchaser.EquipHoldKey(SampleUserNum, 1), 201001, ShopPurchaser.EquipRecordState, false);
        var body = ShopWire.InventoryUpdateSlotsEquipHold([FirstHeldRecord(), second]);

        Assert.Equal(ShopWire.EquipHoldSingleRecordBytes + ShopWire.ArticleSlotBytes, body.Length);
        Assert.Equal(60, body.Length);

        var r = new PacketReader(body);
        Assert.Equal(0u, r.ReadUInt32());            // container 1
        Assert.Equal(2u, r.ReadUInt32());            // container 2
        Assert.Equal(ShopPurchaser.EquipHoldKey(SampleUserNum, 0), r.ReadInt64());
        Assert.Equal(200001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.False(r.ReadBool32());
        Assert.Equal(0u, r.ReadUInt32());            // nested vector
        Assert.Equal(ShopPurchaser.EquipHoldKey(SampleUserNum, 1), r.ReadInt64());
        Assert.Equal(201001, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        Assert.False(r.ReadBool32());
        Assert.Equal(0u, r.ReadUInt32());            // nested vector
        Assert.Equal(0u, r.ReadUInt32());            // container 3
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void EmptyContainerTwoBurstIsRefusedLoud()
    {
        Assert.Throws<ArgumentException>(() => ShopWire.InventoryUpdateSlotsEquipHold([]));
        Assert.Throws<ArgumentNullException>(() => ShopWire.InventoryUpdateSlotsEquipHold(null!));
    }

    [Fact]
    public void EnvelopeDecodesBackToThePlainBody()
    {
        var plain = ShopWire.InventoryUpdateSlotsEquipHold([FirstHeldRecord()]);
        var wire = ShopWire.InventoryUpdateSlotsEquipHoldEnvelope([FirstHeldRecord()]);

        // The wire form is the envelope, not the plain body: a u32 plain length
        // followed by the zlib stream the S2C 803 stream binder reads.
        Assert.NotEqual(plain, wire);
        Assert.Equal((uint)plain.Length, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(0, 4)));

        using var source = new MemoryStream(wire, 4, wire.Length - 4);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        zlib.CopyTo(target);
        Assert.Equal(plain, target.ToArray());
    }

    [Fact]
    public async Task PurchaseQueuesTheRecordButSendsNothingUntilC2S803()
    {
        var hold = new EquipHoldStore();
        var session = Session(1);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, HoldOptions())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        // The purchase path itself stays byte-identical: 816, 834, 106 and the
        // inventory push, and no S2C 803 of any kind.
        var sent = session.Sent;
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain(sent, frame => frame.Opcode == ShopTablePackets.Numbers.InventorySlotsUpdate);
        Assert.Equal(1, hold.Count(SampleUserNum));

        var handlers = new InventoryRequestHandlers(hold, ShopTablePackets.Numbers.InventorySlotsUpdate, NullLogger.Instance);
        await handlers.HandleRequestInventoryAsync(session, new InboundPacket(803, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Equal(3, sent.Count);
        var (opcode, payload) = sent[2];
        Assert.Equal(ShopTablePackets.Numbers.InventorySlotsUpdate, opcode);
        Assert.Equal(803, opcode);
        Assert.Equal(ShopWire.InventoryUpdateSlotsEquipHoldEnvelope([FirstHeldRecord()]), payload);
        Assert.Equal(0, hold.Count(SampleUserNum));
    }

    [Fact]
    public async Task SecondC2S803SendsNothingBecauseTheSetWasTaken()
    {
        var hold = new EquipHoldStore();
        var session = Session(2);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, HoldOptions())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var handlers = new InventoryRequestHandlers(hold, ShopTablePackets.Numbers.InventorySlotsUpdate, NullLogger.Instance);
        await handlers.HandleRequestInventoryAsync(session, new InboundPacket(803, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        await handlers.HandleRequestInventoryAsync(session, new InboundPacket(803, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Equal(3, session.Sent.Count);
        Assert.Single(session.Sent, frame => frame.Opcode == ShopTablePackets.Numbers.InventorySlotsUpdate);
    }

    [Fact]
    public async Task C2S803WithoutPendingRecordsSendsNothing()
    {
        var hold = new EquipHoldStore();
        var session = Session(3);

        await new InventoryRequestHandlers(hold, ShopTablePackets.Numbers.InventorySlotsUpdate, NullLogger.Instance)
            .HandleRequestInventoryAsync(session, new InboundPacket(803, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Empty(session.Sent);
    }

    [Fact]
    public async Task OptionOffKeepsThePurchasePathFreeOfHeldRecords()
    {
        var hold = new EquipHoldStore();
        var session = Session(4);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, new ShopOptions { ShopItemClientPath = "test.xml" })
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        Assert.Equal(2, session.Sent.Count);
        Assert.Equal(0, hold.Count(SampleUserNum));

        await new InventoryRequestHandlers(hold, ShopTablePackets.Numbers.InventorySlotsUpdate, NullLogger.Instance)
            .HandleRequestInventoryAsync(session, new InboundPacket(803, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.Equal(2, session.Sent.Count);
    }

    [Fact]
    public async Task RefusedPurchaseQueuesNothing()
    {
        var hold = new EquipHoldStore();
        var session = Session(5);
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 10, 0));

        await NewPurchaser(money, new FakeInventoryRepository(), new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, HoldOptions())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var (opcode, _) = Assert.Single(session.Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        Assert.Equal(0, hold.Count(SampleUserNum));
    }

    [Fact]
    public async Task RepeatedPurchasesQueueOneRecordEachWithDistinctIdentities()
    {
        var hold = new EquipHoldStore();
        var money = new FakeMoneyRepository(new PlayerWallet(SampleUserNum, 100_000, 0));
        var inventory = new FakeInventoryRepository();
        var purchaser = NewPurchaser(money, inventory, new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, HoldOptions());

        await purchaser.PurchaseAsync(Session(6), 4000001, CancellationToken.None);
        await purchaser.PurchaseAsync(Session(7), 4000001, CancellationToken.None);

        Assert.Equal(2, hold.Count(SampleUserNum));
        Assert.True(hold.TryTake(SampleUserNum, out var records));
        Assert.Equal(2, records.Count);
        Assert.Equal(ShopPurchaser.EquipHoldKey(SampleUserNum, 0), records[0].Unknown10);
        Assert.Equal(ShopPurchaser.EquipHoldKey(SampleUserNum, 1), records[1].Unknown10);
        Assert.NotEqual(records[0].Unknown10, records[1].Unknown10);

        // The held identity is the granted bag slot key: one purchase, one key.
        var bag = await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), bag[0].SlotKey);
        Assert.Equal(records[0].Unknown10, bag[0].SlotKey);
    }

    [Fact]
    public async Task FullHoldQueueRefusesBeforeCharging()
    {
        var hold = new EquipHoldStore();
        for (var i = 0; i < EquipHoldStore.MaxRecordsPerPlayer; i++)
            Assert.True(hold.TryRemember(SampleUserNum, new EquippedSlotRecord(SampleUserNum, i, 200001, 1, false)));
        var wallet = new PlayerWallet(SampleUserNum, 100_000, 0);
        var money = new FakeMoneyRepository(wallet);
        var inventory = new FakeInventoryRepository();
        var session = Session(8);

        await NewPurchaser(money, inventory, new FakeInventoryPushService(), new FakeShopPurchaseStore(), hold, HoldOptions())
            .PurchaseAsync(session, 4000001, CancellationToken.None);

        var (opcode, payload) = Assert.Single(session.Sent);
        Assert.Equal(ShopTablePackets.Numbers.ShopBuyReply, opcode);
        Assert.Equal(0, payload[4]);
        Assert.Equal(wallet, await money.FindByUserNumAsync(SampleUserNum, CancellationToken.None));
        Assert.Empty(await inventory.ListSlotsAsync(SampleUserNum, InventoryBag.CharacterBag, 512, CancellationToken.None));
        Assert.False(hold.TryRemember(SampleUserNum, new EquippedSlotRecord(SampleUserNum, 999, 200001, 1, false)));
        Assert.Equal(EquipHoldStore.MaxRecordsPerPlayer, hold.Count(SampleUserNum));
    }

    [Fact]
    public void HeldSetIsCappedAndFailsLoudAboveTheCap()
    {
        var hold = new EquipHoldStore();
        for (var i = 0; i < EquipHoldStore.MaxRecordsPerPlayer; i++)
            hold.Remember(SampleUserNum, new EquippedSlotRecord(SampleUserNum, i, 200001, 1, false));

        Assert.Equal(EquipHoldStore.MaxRecordsPerPlayer, hold.Count(SampleUserNum));
        var overflow = Assert.Throws<InvalidOperationException>(
            () => hold.Remember(SampleUserNum, new EquippedSlotRecord(SampleUserNum, 999, 200001, 1, false)));
        Assert.Contains(nameof(EquipHoldStore.MaxRecordsPerPlayer), overflow.Message, StringComparison.Ordinal);
        Assert.Equal(EquipHoldStore.MaxRecordsPerPlayer, hold.Count(SampleUserNum));
    }

    [Fact]
    public void HeldSetsArePerPlayer()
    {
        var hold = new EquipHoldStore();
        hold.Remember(1, new EquippedSlotRecord(1, 1, 200001, 1, false));
        hold.Remember(2, new EquippedSlotRecord(2, 2, 201001, 1, false));

        Assert.Equal(1, hold.Count(1));
        Assert.Equal(1, hold.Count(2));
        Assert.Equal(0, hold.Count(3));
        Assert.True(hold.TryTake(1, out var first));
        Assert.Equal(200001, Assert.Single(first).Unknown11);
        Assert.False(hold.TryTake(1, out _));
        Assert.True(hold.TryTake(2, out var second));
        Assert.Equal(201001, Assert.Single(second).Unknown11);
    }

    [Fact]
    public void EquipHoldKeyIsDeterministicAndAboveTheMarkerRange()
    {
        var first = ShopPurchaser.EquipHoldKey(SampleUserNum, 0);
        var second = ShopPurchaser.EquipHoldKey(SampleUserNum, 1);
        var other = ShopPurchaser.EquipHoldKey(SampleUserNum + 1, 0);

        Assert.Equal(first, ShopPurchaser.EquipHoldKey(SampleUserNum, 0));
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(ShopPurchaser.PurchaseSlotKey(SampleUserNum, 0), first);
        Assert.True(first > unchecked((long)0x40F86A0000000000));
    }

    [Fact]
    public void HoldOptionDefaultsFalseWithACleanValidate()
    {
        var options = new ShopOptions { ShopItemClientPath = "test.xml" };
        Assert.False(options.HoldEquipUntilInventoryOpen);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void HoldWithoutGrantFailsValidationNamingBothMembers()
    {
        var options = new ShopOptions { ShopItemClientPath = "test.xml", HoldEquipUntilInventoryOpen = true, GrantPurchasedItems = false };
        var failures = options.Validate().ToList();

        var failure = Assert.Single(failures);
        Assert.Contains(nameof(ShopOptions.HoldEquipUntilInventoryOpen), failure.MemberNames);
        Assert.Contains(nameof(ShopOptions.GrantPurchasedItems), failure.MemberNames);
    }

    [Fact]
    public void HoldWithGrantPassesValidation()
    {
        var options = HoldOptions();
        Assert.True(options.HoldEquipUntilInventoryOpen);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidationStillRejectsTheMissingCatalogPathUnderHold()
    {
        var options = new ShopOptions { HoldEquipUntilInventoryOpen = true };
        Assert.Contains(options.Validate(), result => result.MemberNames.Contains(nameof(ShopOptions.ShopItemClientPath)));
        options.ShopItemClientPath = "Config/ShopItem2.xml";
        Assert.DoesNotContain(options.Validate(), result => result.MemberNames.Contains(nameof(ShopOptions.ShopItemClientPath)));
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

        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);

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

    private sealed class FakeInventoryRepository : IInventoryRepository
    {
        private readonly Dictionary<(long, InventoryBag), List<InventorySlotRecord>> _slots = [];

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
            long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
        {
            if (!_slots.TryGetValue((userNum, bag), out var rows))
            {
                rows = [];
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

        public int PushCount { get; private set; }

        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct)
        {
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
