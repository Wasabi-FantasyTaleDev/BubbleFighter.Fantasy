using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Presents;
using BubbleFighter.Fantasy.Shop.Purchasing;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// The gift box service over the in-memory stores: what a send charges and refuses, what the lists and actions answer,
/// and that a gift is granted exactly once however often, however concurrently and over however broken a connection the
/// receiver asks for it.
/// </summary>
public sealed partial class PresentServiceTests
{
    private const string Build = "kr-812";
    private const int GiftKey = 4000001;

    // A 4500 Lucci rifle (item 200001) sold for 30 days, an item whose limit is 30 days.
    private static readonly ShopCardRow Card = new(
        4000001, "Rifle", "lucci", true, false, 4500, 4500, 0, "Weapon", "Rifle", 200001,
        [new ShopStockRow(GiftKey, 200001, true, 1, 30, 4500, 0, 4500, 0)], 300, 301);

    private static readonly ShopCardRow CashCard = new(
        4000002, "Cash rifle", "cash", true, false, 100, 100, 0, "Weapon", "Rifle", 200002,
        [new ShopStockRow(3000002, 200002, true, 1, 30, 100, 0, 100, 0)], 300, 301);

    private sealed class Fixture
    {
        public MemoryPlayerTable Table { get; } = new(16);
        public MemoryPlayerRepository Players { get; }
        public MemoryPresentStore Presents { get; }
        public MemoryShopPurchaseStore Purchases { get; }
        public MemoryInventoryRepository Inventory { get; }
        public MemoryPlayerWalletRepository Wallets { get; }
        public MemoryPlayerProgressRepository Progress { get; }
        public Dictionary<int, ShopGift> RewardGifts { get; } = [];
        public ShopOpenScope Open { get; } = new(16);
        public Recorder<ShopStateUpdated> State { get; } = new();
        public Recorder<ShopBalanceUpdated> Balance { get; } = new();
        public Recorder<PresentSendReplied> Sends { get; } = new();
        public Recorder<PresentListReplied> Lists { get; } = new();
        public Recorder<PresentActionReplied> Actions { get; } = new();
        public Recorder<PresentNoticeSent> Notices { get; } = new();
        public Registry Sessions { get; } = new();
        public ShopOptions Options { get; } = new()
        {
            ShopItemClientPath = "test.xml", Protocol = Build + "/game", ClientProfile = Build, PresentBox = true,
            GrantPurchasedItems = true, StartingLucci = 100_000, StartingCash = 500, MaxPresentsPerPlayer = 3,
            MaxPresentMessageChars = 10,
        };
        public PresentService Service { get; }
        public PresentPlayer SenderPlayer { get; private set; } = null!;
        public PresentPlayer ReceiverPlayer { get; private set; } = null!;
        public Session SenderSession { get; private set; } = null!;
        public Session ReceiverSession { get; private set; } = null!;

        public Fixture(Func<ShopStateUpdated, ISession, ValueTask>? stateFailure = null)
        {
            Players = new MemoryPlayerRepository(Table);
            Presents = new MemoryPresentStore(Table, Players);
            Purchases = new MemoryShopPurchaseStore(Table, Presents);
            Inventory = new MemoryInventoryRepository(Table);
            Wallets = new MemoryPlayerWalletRepository(Table);
            Progress = new MemoryPlayerProgressRepository(Table);
            foreach (var stock in Card.Stocks.Concat(CashCard.Stocks))
                RewardGifts[stock.StockId] = new(stock.StockId, 0, 0, [stock]);
            var catalog = new ShopCatalog("test.xml", [Card, CashCard], []) { Gifts = RewardGifts };
            var terms = new Dictionary<int, ItemTerms_Legacy>
            {
                [200001] = new(200001, "day", 30, 0, 0),
                [200002] = new(200002, "day", 365, 0, 0),
            };
            Func<ShopStockRow, PurchaseGrantTerm> resolve = stock => terms.TryGetValue(stock.ItemId, out var term)
                ? term.ForStock(stock) : throw new PurchaseRejectedException($"Item {stock.ItemId} has no purchase terms.");
            State.OnSend = stateFailure;
            Service = new PresentService(Options, catalog, new ConditionalShopPurchaser(Purchases, Inventory, TimeProvider.System, 2),
                resolve, Inventory, Presents, Open, State, Balance, Sends, Lists, Actions, Notices, Sessions, TimeProvider.System,
                NullLogger.Instance);
        }

        public async Task<Fixture> StartAsync(bool openShop = true)
        {
            var sender = await Players.CreateAsync("account-sender", "sender", default);
            var receiver = await Players.CreateAsync("account-receiver", "receiver", default);
            SenderPlayer = new(sender.UserNum, "sender");
            ReceiverPlayer = new(receiver.UserNum, "receiver");
            SenderSession = new Session(1, (int)sender.UserNum, "sender", Build);
            ReceiverSession = new Session(2, (int)receiver.UserNum, "receiver", Build);
            Sessions.Add(SenderSession);
            Sessions.Add(ReceiverSession);
            if (openShop)
                Open.Remember(SenderSession.Connection.Id, new ShopOpenRecord(sender.UserNum, 1, 0, ShopKind.ItemShop));
            return this;
        }

        public ValueTask<IReadOnlyList<InventorySlotRecord>> BagOf(PresentPlayer player) =>
            Inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 512, default);

        public async Task<PresentRecord> GiftAsync(int key = GiftKey)
        {
            await Service.SendAsync(SenderSession, new PresentSendRequested("receiver", key, "hi"), default);
            return (await Presents.ListReceivedAsync(Build, ReceiverPlayer.UserNum, 0, 16, default)).Records[0];
        }
    }

    [Fact]
    public async Task ASendChargesTheCatalogPriceStoresTheGiftAndTellsTheReceiverOnTheSameBuild()
    {
        var f = await new Fixture().StartAsync();
        var otherBuild = new Session(3, (int)f.ReceiverPlayer.UserNum, "receiver", "cn-324");
        f.Sessions.Add(otherBuild);

        await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("RECEIVER", GiftKey, "hi there"), default);

        Assert.Equal(PresentSendStatus.Sent, Assert.Single(f.Sends.Items).Status);
        Assert.Equal(100_000 - 4500, (await f.Wallets.FindByUserNumAsync(f.SenderPlayer.UserNum, default))!.Lucci);
        Assert.Equal(100_000 - 4500, Assert.Single(f.Balance.Items).Wallet.Lucci);
        var gift = Assert.Single((await f.Presents.ListReceivedAsync(Build, f.ReceiverPlayer.UserNum, 0, 4, default)).Records);
        Assert.Equal((GiftKey, "hi there", PresentState.Pending), (gift.StockKey, gift.Message, gift.State));
        // Nothing is delivered until the receiver takes it.
        Assert.Empty(await f.BagOf(f.ReceiverPlayer));
        // The receiver's live connection of this build gets the icon; the other build's does not (its workbook differs).
        var notice = Assert.Single(f.Notices.Items);
        Assert.Equal(new PresentNoticeSent(true, 1), notice);
        Assert.Same(f.ReceiverSession, Assert.Single(f.Notices.Sessions));
    }

    [Fact]
    public async Task EachRefusalAnswersItsStatusAndChargesNothing()
    {
        var f = await new Fixture().StartAsync();
        async Task<PresentSendStatus> Try(string nick, int key, string message = "hi", Session? from = null)
        {
            f.Sends.Items.Clear();
            await f.Service.SendAsync(from ?? f.SenderSession, new PresentSendRequested(nick, key, message), default);
            return Assert.Single(f.Sends.Items).Status;
        }

        Assert.Equal(PresentSendStatus.ReceiverMissing, await Try("nobody", GiftKey));
        Assert.Equal(PresentSendStatus.ReceiverMissing, await Try("   ", GiftKey));
        Assert.Equal(PresentSendStatus.ReceiverMissing, await Try(new string('x', PresentLimits.MaxNicknameChars + 1), GiftKey));
        Assert.Equal(PresentSendStatus.SelfGift, await Try("sender", GiftKey));
        Assert.Equal(PresentSendStatus.NotGiftable, await Try("receiver", 999));
        Assert.Equal(PresentSendStatus.MessageRejected, await Try("receiver", GiftKey, new string('m', 11)));
        f.Options.CashPresents = false;
        Assert.Equal(PresentSendStatus.CurrencyRefused, await Try("receiver", 4000002));
        // A connection that has not opened the shop buys nothing, as an ordinary purchase.
        var closed = new Session(9, (int)f.SenderPlayer.UserNum, "sender", Build);
        Assert.Equal(PresentSendStatus.SenderInvalid, await Try("receiver", GiftKey, from: closed));
        // The shop was opened by another player's connection record.
        f.Open.Remember(closed.Connection.Id, new ShopOpenRecord(f.ReceiverPlayer.UserNum, 1, 0, ShopKind.ItemShop));
        Assert.Equal(PresentSendStatus.SenderInvalid, await Try("receiver", GiftKey, from: closed));

        Assert.Empty(f.Balance.Items);
        Assert.Empty(f.Notices.Items);
        Assert.Null(await f.Wallets.FindByUserNumAsync(f.SenderPlayer.UserNum, default));
        Assert.Empty((await f.Presents.ListSentAsync(Build, f.SenderPlayer.UserNum, 0, 4, default)).Records);
    }

    [Fact]
    public async Task APoorSenderAndFullBoxesAreRefusedBeforeAnyCharge()
    {
        var f = await new Fixture().StartAsync();
        await f.Wallets.UpsertAsync(new PlayerWallet(f.SenderPlayer.UserNum, 4499, 0), default);
        await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "hi"), default);
        Assert.Equal(PresentSendStatus.InsufficientFunds, f.Sends.Items[^1].Status);

        await f.Wallets.UpsertAsync(new PlayerWallet(f.SenderPlayer.UserNum, 100_000, 0), default);
        for (var i = 0; i < 3; i++)
            await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "hi"), default);
        Assert.All(f.Sends.Items.Skip(1), reply => Assert.Equal(PresentSendStatus.Sent, reply.Status));
        await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "hi"), default);
        Assert.Equal(PresentSendStatus.ReceiverBoxFull, f.Sends.Items[^1].Status);
        Assert.Equal(100_000 - 3 * 4500, (await f.Wallets.FindByUserNumAsync(f.SenderPlayer.UserNum, default))!.Lucci);
    }

    [Fact]
    public async Task AReceiverWhoAlreadyHoldsTheItemCannotBeSentAnotherCopy()
    {
        var f = await new Fixture().StartAsync();
        var first = await f.GiftAsync();
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, first.Id), default);
        f.Sends.Items.Clear();

        await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "hi"), default);

        // The item's catalog limit is 30 days, so another 30 days on top of the 30 owned would pass it.
        Assert.Equal(PresentSendStatus.ReceiverOwnsItem, Assert.Single(f.Sends.Items).Status);
        Assert.Single((await f.Presents.ListSentAsync(Build, f.SenderPlayer.UserNum, 0, 4, default)).Records);
    }

    [Fact]
    public async Task TheListsPageNewestFirstWithTheTotalAndThePendingCount()
    {
        var f = await new Fixture().StartAsync();
        await f.Wallets.UpsertAsync(new PlayerWallet(f.SenderPlayer.UserNum, 100_000, 0), default);
        for (var i = 0; i < 3; i++)
            await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "m" + i), default);

        await f.Service.ListAsync(f.ReceiverSession, new PresentListRequested(PresentBox.Received, 0, 2), default);
        await f.Service.ListAsync(f.SenderSession, new PresentListRequested(PresentBox.Sent, 2, 2), default);

        var received = f.Lists.Items[0];
        Assert.Equal((PresentBox.Received, 3, 3), (received.Box, received.Total, received.PendingCount));
        Assert.Equal(["m2", "m1"], received.Records.Select(record => record.Message));
        var sent = f.Lists.Items[1];
        Assert.Equal((PresentBox.Sent, 3, 0), (sent.Box, sent.Total, sent.PendingCount));
        Assert.Equal(["m0"], sent.Records.Select(record => record.Message));
    }

    [Fact]
    public async Task TakingAGiftGrantsTheItemOnceChargesNothingAndPublishesTheBag()
    {
        var f = await new Fixture().StartAsync();
        var gift = await f.GiftAsync();
        f.Notices.Items.Clear();

        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default);

        var reply = Assert.Single(f.Actions.Items);
        Assert.Equal(new PresentActionReplied(PresentAction.Receive, true, gift.Id, GiftKey, 0), reply);
        var slot = Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Equal(200001, slot.ItemId);
        var state = Assert.Single(f.State.Items);
        Assert.Equal(200001, Assert.Single(state.Slots).ItemId);
        // The receiver pays nothing for a gift (only the seeded starting balance is stored).
        Assert.Equal(100_000, state.Wallet.Lucci);
        Assert.Equal(PresentState.Accepted, (await f.Presents.FindReceivedAsync(Build, f.ReceiverPlayer.UserNum, gift.Id, default))!.State);
    }

    [Fact]
    public async Task ARepeatedAcceptRequestGrantsNothingTheSecondTime()
    {
        var f = await new Fixture().StartAsync();
        var gift = await f.GiftAsync();

        for (var i = 0; i < 3; i++)
            await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default);

        Assert.Equal([true, false, false], f.Actions.Items.Select(reply => reply.Succeeded));
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Single(f.State.Items);
        Assert.Single(await f.Purchases.ListRecentAsync(f.ReceiverPlayer.UserNum, 8, default));
    }

    [Fact]
    public async Task ConcurrentAcceptRequestsGrantTheItemExactlyOnce()
    {
        var f = await new Fixture().StartAsync();
        var gift = await f.GiftAsync();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default))));

        Assert.Single(f.Actions.Items, reply => reply.Succeeded);
        Assert.Equal(11, f.Actions.Items.Count(reply => !reply.Succeeded));
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Single(f.State.Items);
    }

    [Fact]
    public async Task ADropAfterTheCommitDoesNotGrantAgainOnTheRetry()
    {
        // The connection breaks while the bag is being published: the grant is committed already, and the client that
        // reconnects and asks again finds the gift taken instead of receiving the item twice.
        var f = await new Fixture((_, _) => throw new IOException("connection reset")).StartAsync();
        var gift = await f.GiftAsync();

        await Assert.ThrowsAsync<IOException>(() => f.Service.ActionAsync(f.ReceiverSession,
            new PresentActionRequested(PresentAction.Receive, gift.Id), default).AsTask());

        Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Equal(PresentState.Accepted, (await f.Presents.FindReceivedAsync(Build, f.ReceiverPlayer.UserNum, gift.Id, default))!.State);
        f.State.OnSend = null;
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default);
        Assert.False(Assert.Single(f.Actions.Items).Succeeded);
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
    }

    [Fact]
    public async Task ACancelledAcceptChangesNothingAndTheGiftCanStillBeTakenOnce()
    {
        var f = await new Fixture().StartAsync();
        var gift = await f.GiftAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ActionAsync(f.ReceiverSession,
            new PresentActionRequested(PresentAction.Receive, gift.Id), cancelled.Token).AsTask());

        Assert.Empty(await f.BagOf(f.ReceiverPlayer));
        Assert.Equal(PresentState.Pending, (await f.Presents.FindReceivedAsync(Build, f.ReceiverPlayer.UserNum, gift.Id, default))!.State);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default);
        Assert.True(Assert.Single(f.Actions.Items).Succeeded);
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
    }

    [Fact]
    public async Task OnlyTheReceiverCanTakeAGiftAndAThrownOrRemovedOneStaysUndelivered()
    {
        var f = await new Fixture().StartAsync();
        var kept = await f.GiftAsync();
        var thrown = await f.GiftAsync();
        var removed = await f.GiftAsync();

        await f.Service.ActionAsync(f.SenderSession, new PresentActionRequested(PresentAction.Receive, kept.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Throw, thrown.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.DeleteReceived, removed.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, thrown.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, removed.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, 987_654), default);

        Assert.Equal([false, true, true, false, false, false], f.Actions.Items.Select(reply => reply.Succeeded));
        Assert.Empty(await f.BagOf(f.ReceiverPlayer));
        Assert.Empty(await f.BagOf(f.SenderPlayer));
        Assert.Empty(f.State.Items);
        // The pending count in each reply is what the task-bar badge shows.
        Assert.Equal([0, 2, 1, 1, 1, 1], f.Actions.Items.Select(reply => reply.PendingCount));
    }

    [Fact]
    public async Task RemovingAGiftIsSuccessfulEvenWhenItIsAlreadyGoneAndTheSenderKeepsItsOwnView()
    {
        var f = await new Fixture().StartAsync();
        var gift = await f.GiftAsync();

        await f.Service.ActionAsync(f.SenderSession, new PresentActionRequested(PresentAction.DeleteSent, gift.Id), default);
        await f.Service.ActionAsync(f.SenderSession, new PresentActionRequested(PresentAction.DeleteSent, gift.Id), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.DeleteReceived, 987_654), default);

        Assert.All(f.Actions.Items, reply => Assert.True(reply.Succeeded));
        Assert.Equal(0, (await f.Presents.ListSentAsync(Build, f.SenderPlayer.UserNum, 0, 4, default)).Total);
        Assert.Equal(1, await f.Presents.CountPendingAsync(Build, f.ReceiverPlayer.UserNum, default));
    }

    [Fact]
    public async Task AGiftWhoseKeyTheWorkbookNoLongerSellsStaysInTheBox()
    {
        var f = await new Fixture().StartAsync();
        var stale = await f.Presents.SendAsync(new PresentSendCommand(Build, f.SenderPlayer, f.ReceiverPlayer, 4242, 0, 1, "old",
            "lucci", 0, new PlayerWallet(f.SenderPlayer.UserNum, 0, 0), 1, 10), default);

        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, stale.Present!.Id), default);

        Assert.Equal(new PresentActionReplied(PresentAction.Receive, false, stale.Present.Id, 4242, 1), Assert.Single(f.Actions.Items));
        Assert.Equal(PresentState.Pending, (await f.Presents.FindReceivedAsync(Build, f.ReceiverPlayer.UserNum, stale.Present.Id, default))!.State);
    }

    [Fact]
    public async Task TheNoticeAnswersWhetherAGiftIsWaiting()
    {
        var f = await new Fixture().StartAsync();
        await f.Service.NoticeAsync(f.ReceiverSession, new PresentNoticeRequested(), default);
        var gift = await f.GiftAsync();
        f.Notices.Items.Clear();
        await f.Service.NoticeAsync(f.ReceiverSession, new PresentNoticeRequested(), default);
        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Throw, gift.Id), default);
        await f.Service.NoticeAsync(f.ReceiverSession, new PresentNoticeRequested(), default);

        Assert.Equal([new PresentNoticeSent(true, 1), new PresentNoticeSent(false, 0)], f.Notices.Items);
    }

    [Fact]
    public async Task LoginRaisesTheIconOnlyForWaitingGiftsOfThisBuildsProtocol()
    {
        var f = await new Fixture().StartAsync();
        PresentSendRequested Request() => new("receiver", GiftKey, "hi");
        LoginMenuInstalled Login(string protocol) => new(protocol, f.ReceiverSession,
            new LoginSucceeded((int)f.ReceiverPlayer.UserNum, "account", "receiver", 1, DateTime.UnixEpoch, 300001, [300001],
                new PlayerWallet(f.ReceiverPlayer.UserNum, 0, 0), [], []));

        await f.Service.LoginAsync(Login(Build + "/game"), default);
        Assert.Empty(f.Notices.Items);

        await f.Service.SendAsync(f.SenderSession, Request(), default);
        f.Notices.Items.Clear();
        await f.Service.LoginAsync(Login("cn-324/game"), default);
        Assert.Empty(f.Notices.Items);
        await f.Service.LoginAsync(Login(Build + "/game"), default);
        Assert.Equal(new PresentNoticeSent(true, 1), Assert.Single(f.Notices.Items));
    }

    [Fact]
    public async Task AReceiverWhoseConnectionFailsDoesNotFailTheSenderOrUndoTheGift()
    {
        var f = await new Fixture().StartAsync();
        f.Notices.OnSend = (_, session) => session == f.ReceiverSession ? throw new IOException("closed") : ValueTask.CompletedTask;

        await f.Service.SendAsync(f.SenderSession, new PresentSendRequested("receiver", GiftKey, "hi"), default);

        Assert.Equal(PresentSendStatus.Sent, Assert.Single(f.Sends.Items).Status);
        Assert.Equal(1, await f.Presents.CountPendingAsync(Build, f.ReceiverPlayer.UserNum, default));
    }

    private sealed class Recorder<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => Build + "/game";
        public List<T> Items { get; } = [];
        public List<ISession> Sessions { get; } = [];
        public Func<T, ISession, ValueTask>? OnSend { get; set; }

        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (OnSend is not null)
                return OnSend(message, session);
            // Concurrent tests send from several tasks at once; List<T> is not safe for concurrent adds.
            lock (Items)
            {
                Items.Add(message);
                Sessions.Add(session);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Registry : ISessionRegistry
    {
        private readonly List<ISession> _sessions = [];
        public IReadOnlyList<ISession> All => _sessions.ToArray();
        public void Add(ISession session) => _sessions.Add(session);
        public void Remove(ISession session) => _sessions.Remove(session);
    }

    internal sealed class Session : ISessionWithFeatures
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
