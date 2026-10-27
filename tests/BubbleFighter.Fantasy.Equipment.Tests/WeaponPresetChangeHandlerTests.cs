using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Equipment.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Pins the shared preset-switch logic: the granted slot persists before the view
/// republishes, the reply always carries the authoritative slot (granted or still-active),
/// and a slot whose published bar position is unbound is refused. The client latches its
/// sender until the reply arrives (<c>sub_97A8D0</c>+196), so every request is answered.
/// </summary>
public sealed class WeaponPresetChangeHandlerTests
{
    private static InventorySlotRecord Worn(int itemId) =>
        new(42, InventoryBag.CharacterBag, itemId, itemId, 1, InventorySlotRecord.WornFlagBit)
        { Lifetime = InventoryLifetime.Permanent };

    [Fact]
    public async Task StoredPagesDoNotEnableThePolicyForAnotherBuild()
    {
        var state = new FakeState { Pages = WeaponPresetPages.SeedFromWorn(204001, 0) };
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001)]), new FakeView(), replies, NullLogger.Instance);
        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 4), default);
        Assert.Null(state.Granted);
        Assert.Equal(1, Assert.Single(replies.Sent).Status);
    }

    [Fact]
    public async Task GrantPersistsTheSlotThenRepublishesTheViewThenReplies()
    {
        var order = new List<string>();
        var state = new FakeState(order);
        var view = new FakeView(order);
        var replies = new FakeReplies(order);
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001), Worn(201001)]), view, replies, NullLogger.Instance);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 1), default);

        Assert.Equal((42, (byte)1), state.Granted);
        var sent = Assert.Single(view.Selections);
        Assert.Equal(42, sent.UserNum);
        Assert.Equal(300001, sent.CharacterId);
        Assert.Equal([300001, 202001, 201001], sent.EquippedItemIds);
        var reply = Assert.Single(replies.Sent);
        Assert.Equal((byte)1, reply.Slot);
        Assert.Equal(0, reply.Status);
        Assert.Equal((byte)1, reply.Request.Slot);
        // The store write precedes the view read: the handler's ordering is the wire order.
        Assert.Equal(["grant", "view", "reply"], order);
    }

    [Theory]
    [InlineData(4)] // the fifth hotkey stays unbound in the derived bar
    [InlineData(5)] // past MaxWeaponPresetSlot; the client builder already refuses it
    [InlineData(255)]
    public async Task UnboundOrOutOfRangeSlotsReplyWithTheStillActiveSlot(byte requested)
    {
        var order = new List<string>();
        var state = new FakeState(order) { Current = 2 };
        var view = new FakeView(order);
        var replies = new FakeReplies(order);
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001), Worn(201001)]), view, replies, NullLogger.Instance);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, requested), default);

        Assert.Null(state.Granted);
        Assert.Empty(view.Selections);
        var reply = Assert.Single(replies.Sent);
        Assert.Equal((byte)2, reply.Slot);
        Assert.Equal(1, reply.Status);
        Assert.Equal(["reply"], order);
    }

    [Fact]
    public async Task SlotTwoIsBoundWhenASpecialWeaponIsWorn()
    {
        var state = new FakeState();
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001), Worn(201001), Worn(205001)]), new FakeView(), replies, NullLogger.Instance);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 2), default);

        Assert.Equal((byte)2, Assert.Single(replies.Sent).Slot);
        Assert.Equal(0, Assert.Single(replies.Sent).Status);
    }

    [Fact]
    public async Task SessionsWithoutAPlayerAreRefusedAtSlotZero()
    {
        var state = new FakeState();
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state, null, null, replies, NullLogger.Instance);

        await handler.HandleAsync(new RecordingSession(1), new WeaponPresetChangeRequested(-1, 0), default);

        Assert.Null(state.Granted);
        Assert.Equal((byte)0, Assert.Single(replies.Sent).Slot);
        Assert.Equal(1, Assert.Single(replies.Sent).Status);
    }

    [Fact]
    public async Task MaterializedPagesMakeTheExpansionSlotsGrantable()
    {
        // Pages win over the worn set: slot 4 is dead in the derived bar (FromWorn pins it unbound) but
        // grantable here because its page carries a weapon; the worn set still feeds the republished view.
        var state = new FakeState
        {
            Pages = [new(202001, 205001, 0), new(203001, 205001, 0), new(204001, 0, 0), new(202003, 205001, 0), new(204003, 0, 0)],
        };
        var view = new FakeView();
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001), Worn(201001)]), view, replies, NullLogger.Instance, state);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 4), default);

        Assert.Equal((42, (byte)4), state.Granted);
        Assert.Equal(0, Assert.Single(replies.Sent).Status);
        Assert.Equal((byte)4, Assert.Single(replies.Sent).Slot);
    }

    [Fact]
    public async Task APageWithoutAWeaponIsRefusedLikeAnUnboundSlot()
    {
        var state = new FakeState
        {
            Pages = [new(202001, 205001, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)],
        };
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(202001)]), new FakeView(), replies, NullLogger.Instance, state);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 1), default);

        Assert.Null(state.Granted);
        Assert.Equal(1, Assert.Single(replies.Sent).Status);
    }

    [Fact]
    public async Task APersistedPageGrantsWhatTheWornSetAloneWouldRefuse()
    {
        // Nothing primary is worn, so the derived bar would refuse every slot; the materialized page 0
        // still binds a weapon, and its grant is what a login on the persisted bar replays.
        var state = new FakeState { Pages = [new(202001, 205001, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)] };
        var replies = new FakeReplies();
        var handler = new WeaponPresetChangeHandler(state,
            new FakeSelection([Worn(300001), Worn(201001)]), new FakeView(), replies, NullLogger.Instance, state);

        await handler.HandleAsync(new RecordingSession(1, 42), new WeaponPresetChangeRequested(-1, 0), default);

        Assert.Equal((42, (byte)0), state.Granted);
        Assert.Equal(0, Assert.Single(replies.Sent).Status);
    }

    private sealed class FakeState(List<string>? order = null) : IPlayerInventoryState, IWeaponPresetPageSource
    {
        private readonly List<string> _order = order ?? [];
        internal byte Current { get; set; }
        internal (long User, byte Slot)? Granted { get; private set; }
        /// <summary>Gets or sets materialized pages; null keeps the derived bar (the default-off look).</summary>
        internal WeaponPresetPage[]? Pages { get; set; }
        public ValueTask<WeaponPresetPage[]?> ReadAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(Pages?.ToArray());
        public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(Current);
        public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct)
        {
            _order.Add("grant");
            Granted = (userNum, slot);
            Current = slot;
            return ValueTask.CompletedTask;
        }
        public ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(Pages?.ToArray());
        public ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct) =>
            throw new InvalidOperationException("This fixture does not initialize preset pages.");
        public ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct) =>
            throw new InvalidOperationException("Preset switches never materialize pages.");
    }

    private sealed class FakeSelection(IReadOnlyList<InventorySlotRecord> bag) : IEquipmentSelection
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            ValueTask.FromResult(bag);
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Preset switches never change the worn selection.");
    }

    private sealed class FakeView(List<string>? order = null) : IEquippedViewPush
    {
        private readonly List<string> _order = order ?? [];
        internal List<(int UserNum, int CharacterId, int[] EquippedItemIds)> Selections { get; } = [];
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
            throw new InvalidOperationException("Preset grants publish the committed selection.");
        public ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
            throw new InvalidOperationException("Preset grants never touch remote observers.");
        public ValueTask SendSelectionAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            _order.Add("view");
            Selections.Add((userNum, characterId, equippedIds.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeReplies(List<string>? order = null) : IMessageSender<WeaponPresetChangeReplied>
    {
        private readonly List<string> _order = order ?? [];
        internal List<WeaponPresetChangeReplied> Sent { get; } = [];
        public string Protocol => "kr-812/game";
        public ValueTask SendAsync(ISession session, WeaponPresetChangeReplied message, CancellationToken ct)
        {
            _order.Add("reply");
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
