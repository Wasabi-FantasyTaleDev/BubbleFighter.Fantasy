using System.Collections.Concurrent;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Checks authenticated rename admission and publications independently of optional server notices.</summary>
public sealed class InventoryItemUseTests
{
    /// <summary>A spray commits its catalog color, refreshes stock and local state, then notifies peers.</summary>
    [Fact]
    public async Task PaintPublishesCommittedStateWithoutRenameParameters()
    {
        var rig = new Rig(0, paint: true);
        var identity = rig.Session.Feature<IPlayerSession>().Identity;
        await rig.Handler.HandleAsync(rig.Session, new(100180, 1, ""), default);
        Assert.Equal([(7L, 100180, (ushort)14)], rig.Repository.PaintCalls);
        Assert.Equal(new InventoryItemUseReplied(0, "") { ItemId = 100180 }, Assert.Single(rig.Replies.Sent));
        Assert.Equal(identity with { ColorIndex = 14 }, rig.Session.Feature<IPlayerSession>().Identity);
        Assert.Equal((ushort)14, Assert.Single(rig.Stats).ColorIndex);
        Assert.Equal(new PlayerColorChanged(7, 14), Assert.Single(rig.Events.Emitted));
        Assert.Equal(["commit", "reply", "inventory", "stat", "event"], rig.Order);
        Assert.Empty(rig.Repository.Calls);
    }

    /// <summary>Invalid quantity and invented color parameters cannot reach the paint transaction.</summary>
    [Theory]
    [InlineData(0, "")]
    [InlineData(2, "")]
    [InlineData(1, "14")]
    [InlineData(1, "color=14")]
    public async Task InvalidPaintCannotConsumeSpray(int quantity, string parameters)
    {
        var rig = new Rig(0, paint: true);
        await rig.Handler.HandleAsync(rig.Session, new(100180, quantity, parameters), default);
        Assert.Empty(rig.Repository.PaintCalls);
        Assert.Equal(1, Assert.Single(rig.Replies.Sent).Result);
        Assert.Empty(rig.Events.Emitted);
        Assert.Empty(rig.Pushed);
        Assert.Empty(rig.Stats);
    }

    /// <summary>Rejected paint transactions never publish a new color or refreshed inventory.</summary>
    [Fact]
    public async Task RejectedPaintPreservesSessionAndPublications()
    {
        var rig = new Rig(1, paint: true);
        await rig.Handler.HandleAsync(rig.Session, new(100180, 1, ""), default);
        Assert.Equal((ushort)0, rig.Session.Feature<IPlayerSession>().Identity.ColorIndex);
        Assert.Equal(1, Assert.Single(rig.Replies.Sent).Result);
        Assert.Empty(rig.Events.Emitted);
        Assert.Empty(rig.Pushed);
        Assert.Empty(rig.Stats);
    }

    /// <summary>A committed rename replies with its native item identity before updating the shared nickname and inventory.</summary>
    [Fact]
    public async Task SuccessPublishesCommittedNameAndInventoryWithoutNoticeSender()
    {
        var rig = new Rig(0);
        await rig.Handler.HandleAsync(rig.Session, new(100104, 1, "changeUserName=new-name"), default);
        Assert.Equal([(7L, "new-name", 100104)], rig.Repository.Calls);
        Assert.Equal(new InventoryItemUseReplied(0, "changeUserName=new-name") { ItemId = 100104 }, Assert.Single(rig.Replies.Sent));
        Assert.Equal("new-name", rig.Session.Feature<IPlayerSession>().Identity.Nickname);
        Assert.Equal(new PlayerNicknameChanged(7, "new-name"), Assert.Single(rig.Events.Emitted));
        Assert.Equal([7L], rig.Pushed);
        Assert.Equal(["commit", "reply", "event", "inventory"], rig.Order);
    }

    /// <summary>Failed atomic commits preserve session identity and publish no inventory or nickname change.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task StoreFailureDoesNotPublishUncommittedName(int result)
    {
        var rig = new Rig(result);
        await rig.Handler.HandleAsync(rig.Session, new(100104, 1, "changeUserName=new-name"), default);
        Assert.Equal(new InventoryItemUseReplied(result, "") { ItemId = 100104 }, Assert.Single(rig.Replies.Sent));
        Assert.Equal("player", rig.Session.Feature<IPlayerSession>().Identity.Nickname);
        Assert.Empty(rig.Events.Emitted);
        Assert.Empty(rig.Pushed);
    }

    /// <summary>Unsupported consumables, quantity and malformed rename parameters never reach the atomic store.</summary>
    [Theory]
    [InlineData(100105, 1, "changeUserName=new-name", 1)]
    [InlineData(100104, 0, "changeUserName=new-name", 1)]
    [InlineData(100104, 2, "changeUserName=new-name", 1)]
    [InlineData(100104, 1, "ChangeUserName=new-name", 1)]
    [InlineData(100104, 1, "changeUserName=x", 11)]
    [InlineData(100104, 1, "changeUserName=1234567890123", 11)]
    public async Task InvalidUseCannotConsumeCard(int itemId, int quantity, string parameters, int result)
    {
        var rig = new Rig(0);
        await rig.Handler.HandleAsync(rig.Session, new(itemId, quantity, parameters), default);
        Assert.Empty(rig.Repository.Calls);
        Assert.Equal(new InventoryItemUseReplied(result, "") { ItemId = itemId }, Assert.Single(rig.Replies.Sent));
        Assert.Empty(rig.Events.Emitted);
        Assert.Empty(rig.Pushed);
    }

    /// <summary>An unauthenticated connection cannot invoke the store or receive a successful use reply.</summary>
    [Fact]
    public async Task UnauthenticatedUseFailsBeforeStore()
    {
        var rig = new Rig(0, authenticated: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Handler.HandleAsync(rig.Session, new(100104, 1, "changeUserName=new-name"), default).AsTask());
        Assert.Empty(rig.Repository.Calls);
        Assert.Empty(rig.Replies.Sent);
    }

    private sealed class Rig
    {
        internal List<string> Order { get; } = [];
        internal List<long> Pushed { get; } = [];
        internal List<PlayerRecord> Stats { get; } = [];
        internal Session Session { get; } = new();
        internal RepositoryProxy Repository { get; }
        internal EventsProxy Events { get; }
        internal Replies Replies { get; }
        internal InventoryItemUseHandler Handler { get; }
        internal Rig(int result, bool authenticated = true, bool paint = false)
        {
            var repository = DispatchProxy.Create<IPlayerRepository, RepositoryProxy>();
            Repository = (RepositoryProxy)repository;
            Repository.Result = result;
            Repository.Order = Order;
            var events = DispatchProxy.Create<IEventBus, EventsProxy>();
            Events = (EventsProxy)events;
            Events.Order = Order;
            Replies = new(Order);
            if (authenticated) Session.SetLobbyPlayer(7);
            if (paint) Session.Feature<IPlayerSession>().MoveTo(SessionPhase.MainMenu);
            Handler = new(repository, Replies, (_, user, _) =>
            {
                Order.Add("inventory");
                Pushed.Add(user);
                return ValueTask.CompletedTask;
            }, events, 100104, paint ? new Paints() : null, (session, state, _) =>
            {
                Order.Add("stat");
                Stats.Add(state);
                return ValueTask.CompletedTask;
            });
        }
    }

    /// <summary>Records only atomic name-change calls; accidental unrelated store access fails the test.</summary>
    public class RepositoryProxy : DispatchProxy
    {
        /// <summary>Gets or sets the native store result.</summary>
        public int Result { get; set; }
        /// <summary>Gets or sets the shared publication order recorder.</summary>
        public List<string> Order { get; set; } = [];
        /// <summary>Gets atomic rename arguments.</summary>
        public List<(long User, string Name, int Item)> Calls { get; } = [];
        /// <summary>Gets atomic spray arguments.</summary>
        public List<(long User, int Item, ushort Color)> PaintCalls { get; } = [];
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is null) throw new NotSupportedException();
            if (targetMethod?.Name == nameof(IPlayerRepository.ApplyPaintWithItemAsync))
            {
                PaintCalls.Add(((long)args[0]!, (int)args[1]!, (ushort)args[2]!));
                Order.Add("commit");
                return ValueTask.FromResult(Result == 0);
            }
            if (targetMethod?.Name == nameof(IPlayerRepository.FindByUsernameAsync))
                return ValueTask.FromResult<PlayerRecord?>(new(7, (string)args[0]!, "player", 1) { ColorIndex = 14 });
            if (targetMethod?.Name != nameof(IPlayerRepository.RenameWithItemAsync)) throw new NotSupportedException();
            Calls.Add(((long)args[0]!, (string)args[1]!, (int)args[2]!));
            Order.Add("commit");
            return ValueTask.FromResult(Result);
        }
    }

    /// <summary>Records nickname publications through the actual event contract.</summary>
    public class EventsProxy : DispatchProxy
    {
        /// <summary>Gets or sets the shared publication order recorder.</summary>
        public List<string> Order { get; set; } = [];
        /// <summary>Gets published nickname events.</summary>
        public List<IEvent> Emitted { get; } = [];
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IEventBus.SerialAsync) || args is null) throw new NotSupportedException();
            Emitted.Add(Assert.IsAssignableFrom<IEvent>(args[0]));
            Order.Add("event");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Paints : IPaintItemCatalog
    {
        public bool TryGetColorIndex(int itemId, out ushort colorIndex)
        { colorIndex = itemId == 100180 ? (ushort)14 : (ushort)0; return colorIndex != 0; }
    }

    private sealed class Replies(List<string> order) : IMessageSender<InventoryItemUseReplied>
    {
        internal List<InventoryItemUseReplied> Sent { get; } = [];
        public string Protocol => "test/game";
        public ValueTask SendAsync(ISession session, InventoryItemUseReplied message, CancellationToken ct)
        { order.Add("reply"); Sent.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile => new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
