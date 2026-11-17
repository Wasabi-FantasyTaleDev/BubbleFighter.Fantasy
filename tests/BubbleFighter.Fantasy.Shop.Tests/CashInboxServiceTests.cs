using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop.CashInbox;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Exercises <see cref="CashInboxService"/> end-to-end against <see cref="MemoryCashInboxStore"/> across both
/// legacy (<see cref="ShopStateUpdated"/>) and modern (<see cref="IInventoryPushService"/> + <see cref="ShopBalanceUpdated"/>)
/// publication paths, including ownership, expiration, capacity, wire ordering, concurrency, and login recovery.
/// </summary>
public sealed class CashInboxServiceTests
{
    private const string Build = "kr-812";
    private const string Protocol = Build + "/game";
    private const int TimedStockKey = 3000001;
    private const int TimedItemId = 200001;
    private const int PermanentStockKey = 3000002;
    private const int PermanentItemId = 200002;
    private const int SetWithLucciKey = 4000002;

    [Fact]
    public async Task EnqueueOpenPickupAndRepeatedPickupRespectWireOrderAndPreventDoubleGrants()
    {
        var f = await new Fixture(legacyStatePath: true).StartAsync();

        var granted = await f.Service.EnqueueStockAsync(f.Owner.UserNum, TimedStockKey, "EventGift", "claim-1");
        Assert.Equal(CashInboxGrantOutcome.Granted, granted.Outcome);
        var slotId = granted.Entry!.CashSlotId;

        // Live push went only to the target player's session on matching build; other build received nothing.
        Assert.Single(f.Lists.Items);
        Assert.Single(Assert.Single(f.Lists.Items).Entries);
        Assert.Same(f.OwnerSession, Assert.Single(f.Lists.Sessions));

        // Login burst waterfall handler answers only for matching protocol.
        var wrongProto = await f.Service.OnSnapshotRequestedAsync(
            new CashInboxSnapshotRequested("cn-324/game", f.Owner.UserNum), default);
        Assert.False(wrongProto.Handled);
        var rightProto = await f.Service.OnSnapshotRequestedAsync(
            new CashInboxSnapshotRequested(Protocol, f.Owner.UserNum), default);
        Assert.True(rightProto.Handled);
        Assert.Single(rightProto.Entries!);

        // Explicit open request (gcpReqCashInven) returns active item.
        f.Lists.Clear();
        await f.Service.OpenAsync(f.OwnerSession, new(), default);
        Assert.Equal(slotId, Assert.Single(Assert.Single(f.Lists.Items).Entries).CashSlotId);

        // Pickup commits bag, pushes ShopStateUpdated BEFORE CashInboxPickupReplied(Success), and clears inbox.
        await f.Service.PickupAsync(f.OwnerSession, new(slotId), default);

        Assert.Equal(["state", "reply"], f.Order);
        var reply = Assert.Single(f.Replies.Items);
        Assert.Equal((slotId, CashInboxPickupStatus.Success), (reply.CashSlotId, reply.Status));
        var bagSlot = Assert.Single(await f.BagOf(f.Owner));
        Assert.Equal(TimedItemId, bagSlot.ItemId);

        // Opening inbox again shows empty; re-clicking the same slot fails without double granting.
        f.Lists.Clear();
        await f.Service.OpenAsync(f.OwnerSession, new(), default);
        Assert.Empty(Assert.Single(f.Lists.Items).Entries);

        f.Replies.Clear();
        f.Order.Clear();
        await f.Service.PickupAsync(f.OwnerSession, new(slotId), default);
        Assert.Equal(CashInboxPickupStatus.Failed, Assert.Single(f.Replies.Items).Status);
        Assert.Equal(["reply"], f.Order);
        Assert.Single(await f.BagOf(f.Owner));
    }

    [Fact]
    public async Task ModernPickupPublishesInventoryPushThenBalanceBeforeSuccessReplyAndCreditsSetLucci()
    {
        var f = await new Fixture(legacyStatePath: false).StartAsync();
        await f.Wallets.UpsertAsync(new PlayerWallet(f.Owner.UserNum, 10_000, 500), default);

        var grant = await f.Service.EnqueueStockAsync(f.Owner.UserNum, SetWithLucciKey, "Bundle", "set-1");
        Assert.Equal(CashInboxGrantOutcome.Granted, grant.Outcome);

        await f.Service.PickupAsync(f.OwnerSession, new(grant.Entry!.CashSlotId), default);

        Assert.Equal(["push", "balance", "reply"], f.Order);
        Assert.Equal(CashInboxPickupStatus.Success, Assert.Single(f.Replies.Items).Status);
        var wallet = await f.Wallets.FindByUserNumAsync(f.Owner.UserNum, default);
        Assert.Equal(10_000 + 2500, wallet!.Lucci);
        Assert.Equal(PermanentItemId, Assert.Single(await f.BagOf(f.Owner)).ItemId);
    }

    [Fact]
    public async Task AccountMismatchExpiredAlreadyOwnedFullBagAndMissingCatalogAreRefusedWithoutMutating()
    {
        var f = await new Fixture(legacyStatePath: false).StartAsync();
        var grant = await f.Service.EnqueueStockAsync(f.Owner.UserNum, PermanentStockKey, "UniqueItem");
        var slotId = grant.Entry!.CashSlotId;

        // 1. Cross-account attempt returns AccountMismatch (7) and keeps slot pending.
        await f.Service.PickupAsync(f.OtherSession, new(slotId), default);
        Assert.Equal(CashInboxPickupStatus.AccountMismatch, Assert.Single(f.Replies.Items).Status);
        Assert.Empty(await f.BagOf(f.Other));
        Assert.Single(await f.Service.GetInboxSnapshotAsync(f.Owner.UserNum));

        // 2. Owner already holds permanent item -> AlreadyOwned (8) and keeps slot active.
        f.Replies.Clear();
        await f.Inventory.ReplaceSlotsAsync(
            f.Owner.UserNum,
            InventoryBag.CharacterBag,
            [new InventorySlotRecord(f.Owner.UserNum, InventoryBag.CharacterBag, 500, PermanentItemId, 1, 0)],
            default);
        await f.Service.PickupAsync(f.OwnerSession, new(slotId), default);
        Assert.Equal(CashInboxPickupStatus.AlreadyOwned, Assert.Single(f.Replies.Items).Status);
        Assert.Single(await f.Service.GetInboxSnapshotAsync(f.Owner.UserNum));

        // 3. Full bag returns Failed (2) and keeps slot active.
        var fullBagSlots = Enumerable.Range(1, InventoryLimits.MaxInventorySlotsPerBag)
            .Select(i => new InventorySlotRecord(f.Other.UserNum, InventoryBag.CharacterBag, 1000 + i, 290_000 + i, 1, 0))
            .ToArray();
        await f.Inventory.ReplaceSlotsAsync(f.Other.UserNum, InventoryBag.CharacterBag, fullBagSlots, default);
        var fullGrant = await f.Service.EnqueueStockAsync(f.Other.UserNum, TimedStockKey, "FullBagTest");
        f.Replies.Clear();
        await f.Service.PickupAsync(f.OtherSession, new(fullGrant.Entry!.CashSlotId), default);
        Assert.Equal(CashInboxPickupStatus.Failed, Assert.Single(f.Replies.Items).Status);
        Assert.Equal(InventoryLimits.MaxInventorySlotsPerBag, (await f.BagOf(f.Other)).Count);
        Assert.Single(await f.Service.GetInboxSnapshotAsync(f.Other.UserNum));

        // 4. Expired entry returns Failed (2) without granting.
        f.Clock.Advance(TimeSpan.FromSeconds(f.Options.CashInboxTtlSeconds + 10));
        f.Replies.Clear();
        await f.Service.PickupAsync(f.OwnerSession, new(slotId), default);
        Assert.Equal(CashInboxPickupStatus.Failed, Assert.Single(f.Replies.Items).Status);
        Assert.Empty(await f.Service.GetInboxSnapshotAsync(f.Owner.UserNum));

        // 5. Missing catalog key returns Failed without deleting row.
        var orphanGrant = await f.Inbox.GrantAsync(new CashInboxGrantCommand(
            Build, f.Owner.UserNum, 9999999, 9999999, 1, 0, "Orphan",
            f.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            0, f.Options.MaxCashInboxEntriesPerPlayer), default);
        f.Replies.Clear();
        await f.Service.PickupAsync(f.OwnerSession, new(orphanGrant.Entry!.CashSlotId), default);
        Assert.Equal(CashInboxPickupStatus.Failed, Assert.Single(f.Replies.Items).Status);
        Assert.Equal(CashInboxEntryState.Active, (await f.Inbox.FindBySlotIdAsync(Build, orphanGrant.Entry.CashSlotId, default))!.State);
    }

    [Fact]
    public async Task PostCommitPushFailureLogsWithoutRollingBackCommittedGrant()
    {
        var f = await new Fixture(legacyStatePath: false).StartAsync();
        f.Push.OnSend = (_, _, _) => ValueTask.FromException(new IOException("socket dropped"));

        var grant = await f.Service.EnqueueStockAsync(f.Owner.UserNum, TimedStockKey, "PostCommit");
        await f.Service.PickupAsync(f.OwnerSession, new(grant.Entry!.CashSlotId), default);

        // Durable store committed the pickup and emitted Success reply instead of pretending pickup failed.
        Assert.Equal(CashInboxEntryState.PickedUp, (await f.Inbox.FindBySlotIdAsync(Build, grant.Entry.CashSlotId, default))!.State);
        Assert.Single(await f.BagOf(f.Owner));
        Assert.Equal(CashInboxPickupStatus.Success, Assert.Single(f.Replies.Items).Status);
    }

    [Fact]
    public async Task ConcurrentPickupsOfSameSlotAllowExactlyOneWinnerAndWelcomeGrantIsIdempotent()
    {
        var f = await new Fixture(legacyStatePath: false).StartAsync();
        f.Options.CashInboxWelcomeStocks = [TimedStockKey];
        f.Options.CashInboxWelcomeNote = "FirstLogin";

        var login = new LoginMenuInstalled(
            Protocol,
            f.OwnerSession,
            new LoginSucceeded(checked((int)f.Owner.UserNum), f.Owner.Username, f.Owner.Nickname, 1, DateTime.UtcNow, 300001,
                [300001], new PlayerWallet(f.Owner.UserNum, 0, 0), [], []));

        await f.Service.OnLoginWelcomeAsync(login, default);
        await f.Service.OnLoginWelcomeAsync(login, default);

        var snapshot = await f.Service.GetInboxSnapshotAsync(f.Owner.UserNum);
        var welcomeSlot = Assert.Single(snapshot);
        Assert.Equal("FirstLogin", welcomeSlot.Note);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            f.Service.PickupAsync(f.OwnerSession, new(welcomeSlot.CashSlotId), default).AsTask()));

        Assert.Single(await f.BagOf(f.Owner));
        Assert.Equal(1, f.Replies.Items.Count(r => r.Status == CashInboxPickupStatus.Success));
        Assert.Equal(7, f.Replies.Items.Count(r => r.Status == CashInboxPickupStatus.Failed));
    }

    [Fact]
    public async Task FrameByteBudgetCapsSnapshotWithoutSplittingPackets()
    {
        var f = await new Fixture(legacyStatePath: true).StartAsync();
        f.Options.MaxCashInboxEntriesPerPlayer = 32;
        f.Options.MaxCashInboxFrameBytes = CashInboxLimits.MinFrameBytes; // 512 bytes

        var longNote = new string('A', 60); // 4 + 25 + 120 = 149 bytes per slot => fits 3 slots (4 + 3*149 = 451 <= 512)
        for (var i = 0; i < 6; i++)
            await f.Service.EnqueueStockAsync(f.Owner.UserNum, TimedStockKey, longNote, $"bulk-{i}");

        var bounded = await f.Service.GetInboxSnapshotAsync(f.Owner.UserNum);
        Assert.Equal(3, bounded.Count);
    }

    private sealed class Fixture(bool legacyStatePath)
    {
        private readonly MemoryPlayerTable _playersTable = new(16);
        public MemoryPlayerRepository Players { get; private set; } = null!;
        public MemoryInventoryRepository Inventory { get; private set; } = null!;
        public MemoryPlayerWalletRepository Wallets { get; private set; } = null!;
        public MemoryCashInboxStore Inbox { get; private set; } = null!;
        public ShopOptions Options { get; private set; } = null!;
        public CashInboxService Service { get; private set; } = null!;
        public Recorder<ShopStateUpdated> States { get; } = new();
        public InventoryPushRecorder Push { get; } = new();
        public Recorder<ShopBalanceUpdated> Balances { get; } = new();
        public Recorder<CashInventoryPublished> Lists { get; } = new();
        public Recorder<CashInboxPickupReplied> Replies { get; } = new();
        public Registry Sessions { get; } = new();
        public List<string> Order { get; } = [];
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        public PlayerRecord Owner { get; private set; } = null!;
        public PlayerRecord Other { get; private set; } = null!;
        public Session OwnerSession { get; private set; } = null!;
        public Session OtherSession { get; private set; } = null!;

        public async Task<Fixture> StartAsync()
        {
            Players = new MemoryPlayerRepository(_playersTable);
            Inventory = new MemoryInventoryRepository(_playersTable);
            Wallets = new MemoryPlayerWalletRepository(_playersTable);
            Inbox = new MemoryCashInboxStore(_playersTable);
            Options = new ShopOptions
            {
                Protocol = Protocol,
                ClientProfile = Build,
                ShopItemClientPath = "test.xml",
                CashInbox = true,
                GrantPurchasedItems = true,
                MaxCashInboxEntriesPerPlayer = 16,
                CashInboxTtlSeconds = 3600,
                StartingLucci = 100_000,
                StartingCash = 0,
            };

            var timedCard = new ShopCardRow(
                4000001, "TimedGun", "cash", true, false, 100, 100, 0, "Weapon", "Rifle", TimedItemId,
                [new ShopStockRow(TimedStockKey, TimedItemId, true, 1, 7, 100, 0, 100, 0)], 300, 301);
            var permCard = new ShopCardRow(
                4000003, "PermGun", "cash", true, false, 500, 500, 0, "Weapon", "Rifle", PermanentItemId,
                [new ShopStockRow(PermanentStockKey, PermanentItemId, true, 9, -1, 500, 0, 500, 0)], 300, 301);
            var setCard = new ShopCardRow(
                SetWithLucciKey, "Bundle", "cash", true, false, 800, 800, 0, "Package", "Bundle", PermanentItemId,
                [new ShopStockRow(PermanentStockKey, PermanentItemId, true, 9, -1, 500, 0, 500, 0)], 800, 801);

            var gifts = new ShopGiftIndex(
                new Dictionary<int, ShopStockRow>
                {
                    [TimedStockKey] = timedCard.Stocks[0],
                    [PermanentStockKey] = permCard.Stocks[0],
                },
                new Dictionary<int, ShopGift>
                {
                    [SetWithLucciKey] = new ShopGift(SetWithLucciKey, SetWithLucciKey, 2500, setCard.Stocks),
                });
            var catalog = new ShopCatalog("test.xml", [timedCard, permCard, setCard], []) { Gifts = gifts };

            var itemTerms = new Dictionary<int, ItemTerms>
            {
                [TimedItemId] = new ItemTerms(TimedItemId, "day", 30, 1, 0),
                [PermanentItemId] = new ItemTerms(PermanentItemId, "day", 365, 0, 0),
            };
            var resolver = ShopPlugin.Kr1256Resolve(ItemTermRules.Baseline, itemTerms);

            States.OnSend = (_, _) => { lock (Order) Order.Add("state"); return ValueTask.CompletedTask; };
            Push.OnSend = (_, _, _) => { lock (Order) Order.Add("push"); return ValueTask.CompletedTask; };
            Balances.OnSend = (_, _) => { lock (Order) Order.Add("balance"); return ValueTask.CompletedTask; };
            Replies.OnSend = (_, _) => { lock (Order) Order.Add("reply"); return ValueTask.CompletedTask; };

            Service = new CashInboxService(
                Options, catalog, resolver, Inventory, Inbox,
                legacyStatePath ? States : null,
                legacyStatePath ? null : Push,
                Balances, Lists, Replies, Sessions, Clock, NullLogger.Instance);

            Owner = await Players.CreateAsync("acct-owner", "owner", default);
            Other = await Players.CreateAsync("acct-other", "other", default);
            OwnerSession = new Session(1, (int)Owner.UserNum, "owner", Build);
            OtherSession = new Session(2, (int)Other.UserNum, "other", Build);
            Sessions.Add(OwnerSession);
            Sessions.Add(OtherSession);
            Sessions.Add(new Session(3, (int)Owner.UserNum, "owner", "cn-324"));
            return this;
        }

        public ValueTask<IReadOnlyList<InventorySlotRecord>> BagOf(PlayerRecord player) =>
            Inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default);
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InventoryPushRecorder : IInventoryPushService
    {
        public Func<ISession, long, IReadOnlyList<InventorySlotRecord>, ValueTask>? OnSend { get; set; }
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) =>
            OnSend?.Invoke(session, userNum, grants) ?? ValueTask.CompletedTask;
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Recorder<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => CashInboxServiceTests.Protocol;
        public List<T> Items { get; } = [];
        public List<ISession> Sessions { get; } = [];
        public Func<T, ISession, ValueTask>? OnSend { get; set; }
        public void Clear() { lock (Items) { Items.Clear(); Sessions.Clear(); } }

        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            lock (Items) { Items.Add(message); Sessions.Add(session); }
            return OnSend?.Invoke(message, session) ?? ValueTask.CompletedTask;
        }
    }

    private sealed class Registry : ISessionRegistry
    {
        private readonly List<ISession> _sessions = [];
        public IReadOnlyList<ISession> All => _sessions.ToArray();
        public void Add(ISession session) => _sessions.Add(session);
        public void Remove(ISession session) => _sessions.Remove(session);
    }

    private sealed class Session : ISessionWithFeatures
    {
        public Session(long id, int userNum, string nickname, string build)
        {
            Id = id;
            Connection = new Link(id);
            Profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
            var features = new TestSessionFeatures();
            var player = new PlayerSession(new PlayerIdentity(userNum, nickname, nickname, 1));
            player.MoveTo(SessionPhase.MainMenu);
            features.Register<IPlayerSession>(player);
            Features = features;
        }

        public ISessionFeatures Features { get; }
        public long Id { get; }
        public IConnection Connection { get; }
        public ClientProfile Profile { get; }
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Link(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
