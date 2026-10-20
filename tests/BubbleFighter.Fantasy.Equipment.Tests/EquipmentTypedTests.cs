using BubbleFighter.Fantasy.Tests;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

public sealed class EquipmentTypedTests
{
    public static IEnumerable<object[]> UnsupportedKeys()
    {
        foreach (var key in new[] { 0L, -1L, 100000000L, long.MaxValue })
        foreach (var worn in new[] { false, true })
        foreach (var persisted in new[] { false, true })
            yield return [key, worn, persisted];
    }

    [Theory]
    [MemberData(nameof(UnsupportedKeys))]
    public async Task ValidUnsupportedKeysKeepTheirFullEchoWithoutTouchingStorage(long key, bool worn, bool persisted)
    {
        using var h = await WireHarness.CreateAsync(persisted);
        await h.Router.DispatchAsync("kr-1256/game", h.Session,
            new((ushort)(worn ? 804 : 805), BitConverter.GetBytes(key)), default);
        var reply = Assert.Single(h.Session.Sent);
        Assert.Equal((ushort)(worn ? 811 : 812), reply.Opcode);
        Assert.Equal(12, reply.Payload.Length);
        Assert.Equal(persisted ? EquipmentWire.FailureResult : EquipmentWire.SuccessResult, BinaryPrimitives.ReadInt32LittleEndian(reply.Payload));
        Assert.Equal(key, BinaryPrimitives.ReadInt64LittleEndian(reply.Payload.AsSpan(4)));
        Assert.Empty(h.State.Calls);
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(true, 9)]
    [InlineData(false, 7)]
    [InlineData(false, 9)]
    public async Task MalformedBodiesFailBeforeDispatchOrMutation(bool worn, int length)
    {
        using var h = await WireHarness.CreateAsync(true);
        await Assert.ThrowsAsync<PacketFormatException>(() => h.Router.DispatchAsync("kr-1256/game", h.Session,
            new((ushort)(worn ? 804 : 805), new byte[length]), default).AsTask());
        Assert.Empty(h.State.Calls);
        Assert.Empty(h.Session.Sent);
    }

    [Fact]
    public async Task CodecWithDisabledBusinessFailsLoudInsteadOfInventingSuccess()
    {
        using var h = await WireHarness.CreateAsync(false, enabled: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Router.DispatchAsync("kr-1256/game", h.Session,
            new(804, BitConverter.GetBytes(203073L)), default).AsTask());
        Assert.Contains("The equipment message has no handler", error.Message);
        Assert.Empty(h.State.Calls);
        Assert.Empty(h.Session.Sent);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task BusinessRequiresExactlyOneBindingAndSenderInItsProtocol(int bindings, int senders)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = new PluginContext(new PluginRow { Id = "root", Plugin = "core.message-router", Source = "test" },
            services, events, NullLogger.Instance);
        var row = new PluginContext(new PluginRow { Id = "equipment", Plugin = "equipment", Source = "test",
            Config = new Dictionary<string, object?> { ["persist_worn_state"] = false } }, services, events, NullLogger.Instance);
        try
        {
            await new MessageRouterPlugin().ActivateAsync(root, default);
            var opcodes = EquipmentOpcodeTests.LoadTable();
            for (var i = 0; i < bindings; i++)
                row.Effect(services.RegisterMany<IInboundMessageBinding<EquipmentChangeRequested>>(new EquipmentCodec("kr-1256/game", opcodes)));
            for (var i = 0; i < senders; i++)
                row.Effect(services.RegisterMany<IMessageSender<EquipmentChangeReplied>>(new EquipmentCodec("kr-1256/game", opcodes)));
            var relay = new EquipmentCodec("kr-1256/relay", opcodes);
            row.Effect(services.RegisterMany<IInboundMessageBinding<EquipmentChangeRequested>>(relay));
            row.Effect(services.RegisterMany<IMessageSender<EquipmentChangeReplied>>(relay));
            var error = await Assert.ThrowsAsync<ConfigException>(() => new EquipmentPlugin().ActivateAsync(row, default).AsTask());
            Assert.Contains(bindings != 1 ? nameof(EquipmentChangeRequested) : nameof(EquipmentChangeReplied), error.Message);
            Assert.Empty(services.Get<IMessageRouter>().Handlers);
        }
        finally { row.Unwind(); root.Unwind(); }
    }

    [Fact]
    public async Task RepaintingAcknowledgementFollowsCommittedInventoryAndView()
    {
        using var scope = new EquippedItemScope(4, 4);
        var state = new TraceState { ReplyAfterInventory = true };
        var events = new EventBus(NullLogger.Instance);
        using var subscription = events.OnAsync<EquipmentSelectionCommitted>((_, _) => state.StepAsync("event"));
        var handler = new EquipmentHandlers(scope, new ForbiddenStore(), state, NullLogger.Instance, state, state, state, events);
        await handler.HandleAsync(new RecordingSession(1, 42), new(203073, 203073, true), default);
        Assert.Equal(new[] { "commit", "event", "inventory", "view", "ack" }, state.Calls);
        Assert.True(Assert.Single(state.Replies).Succeeded);
    }

    [Fact]
    public async Task CommittedSnapshotSurvivesMutationDuringEventAndAcknowledgement()
    {
        using var scope = new EquippedItemScope(4, 4);
        var state = new TraceState { WaitForAck = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var events = new EventBus(NullLogger.Instance);
        IReadOnlyList<int>? eventItems = null;
        using var subscription = events.OnAsync<EquipmentSelectionCommitted>((e, _) =>
        {
            state.Calls.Add("event");
            eventItems = e.EquippedItemIds;
            state.Bag![1] = state.Bag[1] with { ItemId = 204003 };
            return ValueTask.CompletedTask;
        });
        var request = new EquipmentChangeRequested(long.MaxValue, 203073, true);
        var handler = new EquipmentHandlers(scope, new ForbiddenStore(), state, NullLogger.Instance, state, state, state, events);
        var pending = handler.HandleAsync(new RecordingSession(1, 42), request, default).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(new[] { "commit", "event", "ack" }, state.Calls);
        state.Bag![0] = state.Bag[0] with { ItemId = 300027 };
        state.WaitForAck.SetResult();
        await pending;
        Assert.Equal(new[] { "commit", "event", "ack", "inventory", "view" }, state.Calls);
        Assert.Equal(new[] { 300001, 203073 }, eventItems);
        Assert.Equal(new[] { 300001, 203073 }, state.Pushed!.Select(slot => slot.ItemId));
        Assert.Equal(new[] { 300001, 203073 }, state.ViewItems);
        Assert.Equal(300001, state.Character);
        Assert.Same(request, Assert.Single(state.Replies).Request);
        Assert.True(state.Replies[0].Succeeded);
        Assert.Equal(203073, state.AppliedItem);
        Assert.True(Assert.IsAssignableFrom<ICollection<InventorySlotRecord>>(state.Pushed).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<ICollection<int>>(eventItems).IsReadOnly);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("event")]
    [InlineData("ack")]
    [InlineData("inventory")]
    [InlineData("view")]
    public async Task FailuresPropagateWithoutRetryOrLaterPublication(string failedStage)
    {
        using var scope = new EquippedItemScope(4, 4);
        var state = new TraceState { FailAt = failedStage };
        var events = new EventBus(NullLogger.Instance);
        using var subscription = events.OnAsync<EquipmentSelectionCommitted>((_, _) => state.StepAsync("event"));
        var handler = new EquipmentHandlers(scope, new ForbiddenStore(), state, NullLogger.Instance, state, state, state, events);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new RecordingSession(1, 42),
            new(203073, 203073, true), default).AsTask());
        Assert.Same(state.Failure, error);
        string[] order = ["commit", "event", "ack", "inventory", "view"];
        Assert.Equal(order.Take(Array.IndexOf(order, failedStage) + 1), state.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("bag")]
    [InlineData("count")]
    [InlineData("missing-character")]
    [InlineData("two-characters")]
    [InlineData("invalid-item")]
    public async Task InvalidCommittedSnapshotsCannotPublishSuccess(string invalid)
    {
        using var scope = new EquippedItemScope(4, 4);
        var state = new TraceState();
        state.Bag = invalid switch
        {
            "owner" => [state.Bag![0], state.Bag[1] with { UserNum = 43 }],
            "bag" => [state.Bag![0] with { Bag = InventoryBag.EquippedBag }, state.Bag[1]],
            "count" => Enumerable.Repeat(state.Bag![0], InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(),
            "missing-character" => [state.Bag![1]],
            "two-characters" => [state.Bag![0], state.Bag[0] with { ItemId = 300027 }],
            "invalid-item" => [state.Bag![0], state.Bag[1] with { ItemId = 0 }],
            _ => throw new InvalidOperationException(),
        };
        var handler = new EquipmentHandlers(scope, new ForbiddenStore(), state, NullLogger.Instance, state, state, state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new RecordingSession(1, 42),
            new(203073, 203073, true), default).AsTask());
        Assert.Equal(new[] { "commit" }, state.Calls);
        Assert.Empty(state.Replies);
    }

    [Fact]
    public async Task RefusedSelectionOnlyPublishesFailure()
    {
        using var scope = new EquippedItemScope(4, 4);
        var state = new TraceState { Bag = null };
        var handler = new EquipmentHandlers(scope, new ForbiddenStore(), state, NullLogger.Instance, state, state, state);
        await handler.HandleAsync(new RecordingSession(1, 42), new(203073, 203073, true), default);
        Assert.Equal(new[] { "commit", "ack" }, state.Calls);
        Assert.False(Assert.Single(state.Replies).Succeeded);
    }

    private sealed class ForbiddenStore : IInventoryRepository
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) => throw new InvalidOperationException("Unexpected inventory read.");
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) => throw new InvalidOperationException("Unexpected inventory replacement.");
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) => throw new InvalidOperationException("Unexpected flag-only mutation.");
    }

    private sealed class TraceState : IEquipmentSelection, IInventoryPushService, IEquippedViewPush, IMessageSender<EquipmentChangeReplied>, IEquipmentReplyPolicy
    {
        public bool ReplyAfterInventory { get; init; }
        public string Protocol => "kr-1256/game";
        internal List<string> Calls { get; } = [];
        internal List<EquipmentChangeReplied> Replies { get; } = [];
        internal InventorySlotRecord[]? Bag { get; set; } = [new(42, InventoryBag.CharacterBag, 1, 300001, 1, 1), new(42, InventoryBag.CharacterBag, 2, 203073, 1, 1)];
        internal IReadOnlyList<InventorySlotRecord>? Pushed { get; private set; }
        internal IReadOnlyList<int>? ViewItems { get; private set; }
        internal int Character { get; private set; }
        internal int AppliedItem { get; private set; }
        internal string? FailAt { get; init; }
        internal InvalidOperationException Failure { get; } = new("Expected publication failure.");
        internal TaskCompletionSource? WaitForAck { get; init; }
        internal ValueTask StepAsync(string stage)
        {
            Calls.Add(stage);
            if (FailAt == stage) throw Failure;
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) => throw new InvalidOperationException("Committed selections must never be reread.");
        public async ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct)
        {
            await StepAsync("commit");
            Assert.Equal(42, userNum);
            AppliedItem = itemId;
            return Bag;
        }
        public async ValueTask SendAsync(ISession session, EquipmentChangeReplied message, CancellationToken ct)
        {
            await StepAsync("ack");
            Replies.Add(message);
            if (WaitForAck is not null) await WaitForAck.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) => throw new InvalidOperationException("Unexpected inventory reread.");
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) => throw new InvalidOperationException("Unexpected purchase publication.");
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) => throw new InvalidOperationException("Expected complete committed snapshot.");
        public async ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            await StepAsync("inventory");
            Pushed = slots;
        }
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) => throw new InvalidOperationException("Expected committed selection publication.");
        public async ValueTask SendSelectionAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            await StepAsync("view");
            Assert.Equal(42, userNum);
            Character = characterId;
            ViewItems = equippedIds;
        }
    }

    private sealed class WireHarness : IDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<PluginContext> _contexts = [];
        internal RecordingSession Session { get; } = new(1, 42);
        internal TraceState State { get; } = new();
        internal IPacketRouter Router => _services.Get<IPacketRouter>();
        private PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(id == "dialect.messages" ? SharedDialectRows.Kr1256(config) : new PluginRow { Id = id, Plugin = id, Source = "test", Config = config }, _services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }
        internal static async Task<WireHarness> CreateAsync(bool persisted, bool enabled = true)
        {
            var h = new WireHarness();
            try
            {
                var root = h.Context("root");
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new MessageRouterPlugin().ActivateAsync(root, default);
                await new OpcodeTableRegistryPlugin().ActivateAsync(root, default);
                root.Effect(h._services.Get<IOpcodeTableRegistry>().Register(EquipmentOpcodeTests.LoadTable()));
                root.Effect(h._services.Register<IInventoryRepository>(new ForbiddenStore()));
                root.Effect(h._services.Register<IEquipmentSelection>(h.State));
                root.Effect(h._services.Register<IEquippedViewPush>(h.State));
                root.Effect(h._services.Register<IInventoryPushService>(h.State));
                await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(h.Context("dialect.messages"), default);
                await new EquipmentPlugin().ActivateAsync(h.Context("equipment", new() { ["persist_worn_state"] = persisted, ["enabled"] = enabled }), default);
                return h;
            }
            catch { h.Dispose(); throw; }
        }
        public void Dispose() { for (var i = _contexts.Count - 1; i >= 0; i--) _contexts[i].Unwind(); }
    }
}
