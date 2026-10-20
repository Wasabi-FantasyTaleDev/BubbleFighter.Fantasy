using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>Checks admission, committed equipment publication and failure propagation for expired-item actions.</summary>
public sealed class InventoryThrowHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessAndAbsentRetryPublishOneAuthoritativeSelection(bool absent)
    {
        var state = new State { Removed = absent ? [] : [Row(202003)] };
        await RunAsync(state);
        Assert.Equal(["remove", "read", "event", "reply", "inventory", "view"], state.Order);
        Assert.Equal(new InventoryThrowReplied(202003, 0), Assert.Single(state.Replies));
        Assert.Equal(42, Assert.Single(state.Events).UserNum);
        Assert.Equal([300001, 202001], state.ViewItems);
        Assert.Equal([300001, 202001], state.Published!.Select(row => row.ItemId));
        Assert.Equal((42L, 202003), state.Target);
    }

    [Fact]
    public async Task ActiveEntitlementRefusalPublishesOnlyTheFailure()
    {
        var state = new State { Removed = null };
        await RunAsync(state);
        Assert.Equal(["remove", "reply"], state.Order);
        Assert.Equal(new InventoryThrowReplied(202003, 1), Assert.Single(state.Replies));
        Assert.Empty(state.Events);
        Assert.Null(state.Published);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(301000)]
    [InlineData(399999)]
    public async Task InvalidAndExcludedCategoryNeverReachStorage(int itemId)
    {
        var state = new State();
        await RunAsync(state, itemId: itemId);
        Assert.Equal(["reply"], state.Order);
        Assert.Equal(new InventoryThrowReplied(itemId, 1), Assert.Single(state.Replies));
    }

    [Theory]
    [InlineData(100000000)]
    [InlineData(int.MaxValue)]
    public async Task PositiveItemIdentityIsNotComparedWithTheNativeSlotKeyThreshold(int itemId)
    {
        var state = new State { Removed = [] };
        await RunAsync(state, itemId: itemId);
        Assert.Equal((42L, itemId), state.Target);
        Assert.Equal(new InventoryThrowReplied(itemId, 0), Assert.Single(state.Replies));
    }

    [Theory]
    [InlineData(SessionPhase.Authenticated)]
    [InlineData(SessionPhase.Match)]
    public async Task InadmissibleSceneCannotRemoveEquipment(SessionPhase phase)
    {
        var state = new State();
        var session = new RecordingSession(1, 42);
        var player = session.Feature<IPlayerSession>();
        if (phase == SessionPhase.Authenticated)
        {
            session = new RecordingSession(2);
            session.RegisterFeature<IPlayerSession>(new PlayerSession(player.Identity));
        }
        else
        {
            player.MoveTo(SessionPhase.Room);
            player.MoveTo(SessionPhase.Match);
        }
        await RunAsync(state, session);
        Assert.Equal(["reply"], state.Order);
        Assert.Equal(1, Assert.Single(state.Replies).Result);
    }

    [Fact]
    public async Task AnonymousAndPreCancelledActionsCannotReachTheStore()
    {
        var state = new State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(state, new RecordingSession(1)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(state, ct: cancelled.Token));
        Assert.Empty(state.Order);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("read")]
    [InlineData("event")]
    [InlineData("reply")]
    [InlineData("inventory")]
    [InlineData("view")]
    public async Task FailurePreservesItsIdentityAndPreventsLaterPublications(string stage)
    {
        var state = new State { FailAt = stage };
        Assert.Same(state.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(state)));
        string[] order = ["remove", "read", "event", "reply", "inventory", "view"];
        Assert.Equal(order.Take(Array.IndexOf(order, stage) + 1), state.Order);
    }

    [Fact]
    public async Task PublishedSelectionOwnsItsDataBeforeTheFirstEventYields()
    {
        var state = new State();
        state.DuringEvent = () => state.Bag[0] = Row(300005);
        await RunAsync(state);
        Assert.Equal([300001, 202001], Assert.Single(state.Events).EquippedItemIds);
        Assert.Equal([300001, 202001], state.Published!.Select(row => row.ItemId));
        Assert.Equal([300001, 202001], state.ViewItems);
        Assert.True(Assert.IsAssignableFrom<ICollection<InventorySlotRecord>>(state.Published).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<ICollection<int>>(state.Events[0].EquippedItemIds).IsReadOnly);
    }

    [Theory]
    [InlineData("foreign-owner")]
    [InlineData("wrong-bag")]
    [InlineData("missing-character")]
    [InlineData("two-characters")]
    [InlineData("too-many-slots")]
    public async Task InvalidSelectionCannotBeAcknowledgedOrPublished(string fault)
    {
        var state = new State();
        state.Bag = fault switch
        {
            "foreign-owner" => [Row(300001) with { UserNum = 99 }],
            "wrong-bag" => [Row(300001) with { Bag = InventoryBag.EquippedBag }],
            "missing-character" => [Row(202001)],
            "two-characters" => [Row(300001), Row(300005)],
            "too-many-slots" => Enumerable.Repeat(Row(300001), InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(),
            _ => throw new InvalidOperationException(fault),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(state));
        Assert.Equal(["remove", "read"], state.Order);
        Assert.Empty(state.Replies);
        Assert.Empty(state.Events);
    }

    private static async Task RunAsync(State state, RecordingSession? session = null, int itemId = 202003,
        CancellationToken ct = default)
    {
        var events = new EventBus(NullLogger.Instance);
        using var subscription = events.OnAsync<EquipmentSelectionCommitted>((change, _) =>
        {
            state.Step("event");
            state.Events.Add(change);
            state.DuringEvent?.Invoke();
            return ValueTask.CompletedTask;
        });
        var handler = new InventoryThrowHandler(state, state, state, state, state, events, excludeCategorySix: true);
        await handler.HandleAsync(session ?? new RecordingSession(1, 42), new(itemId), ct);
    }

    private static InventorySlotRecord Row(int itemId) =>
        new(42, InventoryBag.CharacterBag, itemId, itemId, 1, InventorySlotRecord.WornFlagBit);

    private sealed class State : IInventoryItemRemoval, IEquipmentSelection, IInventoryPushService,
        IEquippedViewPush, IMessageSender<InventoryThrowReplied>
    {
        public string Protocol => "test/game";
        internal List<string> Order { get; } = [];
        internal InventorySlotRecord[] Bag { get; set; } = [Row(300001), Row(202001)];
        internal IReadOnlyList<InventorySlotRecord>? Removed { get; init; } = [Row(202003)];
        internal IReadOnlyList<InventorySlotRecord>? Published { get; private set; }
        internal IReadOnlyList<int>? ViewItems { get; private set; }
        internal List<InventoryThrowReplied> Replies { get; } = [];
        internal List<EquipmentSelectionCommitted> Events { get; } = [];
        internal (long User, int Item) Target { get; private set; }
        internal string? FailAt { get; init; }
        internal Action? DuringEvent { get; set; }
        internal InvalidOperationException Failure { get; } = new("The selected stage failed.");
        internal void Step(string stage)
        {
            Order.Add(stage);
            if (FailAt == stage) throw Failure;
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveItemAsync(long userNum, int itemId, CancellationToken ct)
        {
            Step("remove");
            Target = (userNum, itemId);
            return ValueTask.FromResult(Removed);
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct)
        {
            Step("read");
            Assert.Equal(42, userNum);
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(Bag);
        }
        public ValueTask SendAsync(ISession session, InventoryThrowReplied message, CancellationToken ct)
        {
            Step("reply");
            Replies.Add(message);
            return ValueTask.CompletedTask;
        }
        public ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            Step("inventory");
            Published = slots;
            return ValueTask.CompletedTask;
        }
        public ValueTask SendSelectionAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            Step("view");
            Assert.Equal(42, userNum);
            Assert.Equal(300001, characterId);
            ViewItems = equippedIds;
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) => throw new NotSupportedException();
    }
}
