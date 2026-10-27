using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Equipment.Tests;

public sealed class EquipmentOpcodeTests
{
    internal static OpcodeTable LoadTable() => OpcodeTableLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);

    [Fact]
    public async Task NamesResolveFromTheSelectedConnectionWithoutNumericFallbacks()
    {
        var original = LoadTable();
        var entries = original.All.Select(e => e with
        {
            Value = checked((ushort)(e.Value + 3000)), Connection = OpcodeConnection.Game,
        }).Concat(original.All.Select(e => e with
        {
            Value = checked((ushort)(e.Value + 6000)), Connection = OpcodeConnection.Relay,
        }));
        using var h = await Harness.CreateAsync(new OpcodeTable(original.Profile, entries));
        foreach (var (protocol, shift) in new[] { ("kr-1256/game", 3000), ("kr-1256/relay", 6000) })
        {
            var table = h.Tables.ForProtocol(protocol);
            var equipment = table;
            Assert.Equal(804 + shift, equipment.C2S("gcpReqInventoryEquip"));
            Assert.Equal(805 + shift, equipment.C2S("gcpReqInventoryUnequip"));
            Assert.Equal(811 + shift, equipment.S2C("gspRepInventoryEquip"));
            Assert.Equal(812 + shift, equipment.S2C("gspRepInventoryUnequip"));
            Assert.Equal(806 + shift, table.C2S("gcpReqInventoryUseItem"));
            Assert.Equal(806 + shift, table.S2C("gspInventoryViewUpdateView"));
            Assert.Equal(836 + shift, table.S2C("gspReqInventoryChangeWeaponPresetIdx"));
            Assert.Equal(837 + shift, table.S2C("gspReqWeaponPreSetUpdate"));
        }

        var equipmentCodecContext = h.Context("dialect.messages");
        await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(equipmentCodecContext, default);
        var equipmentContext = h.Context("equipment", new() { ["persist_worn_state"] = false });
        await new EquipmentPlugin().ActivateAsync(equipmentContext, default);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(3804, BitConverter.GetBytes(201003L)), default);
        Assert.Equal((ushort)3811, Assert.Single(h.Session.Sent).Opcode);
        equipmentContext.Unwind();
        Assert.Empty(h.Services.Get<IMessageRouter>().Handlers);
        equipmentCodecContext.Unwind();
        Assert.Empty(h.Services.GetAll<IInboundMessageBinding<EquipmentChangeRequested>>());
        Assert.Empty(h.Services.GetAll<IMessageSender<EquipmentChangeReplied>>());
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(3804, BitConverter.GetBytes(201003L)), default);
        Assert.Equal(new[] { true, false }, h.Handled);
        Assert.Single(h.Session.Sent);
    }

    [Theory]
    [InlineData("equipment", "gspRepInventoryEquip")]
    [InlineData("equipment", "gspRepInventoryUnequip")]
    [InlineData("inventoryview", "gspInventoryViewUpdateView")]
    [InlineData("inventoryview", "gspReqInventoryChangeWeaponPresetIdx")]
    [InlineData("inventoryview", "gspReqWeaponPreSetUpdate")]
    public async Task MissingRequiredNameRejectsActivationEvenWhenOtherScopesContainIt(string plugin, string name)
    {
        var original = LoadTable();
        var missing = Assert.Single(original.All, e => e.Name == name);
        var entries = original.All.Where(e => e != missing).Concat(
        [
            missing with { Value = 60000, Connection = OpcodeConnection.Relay },
            missing with { Value = 60001, Direction = missing.Direction == PacketDirection.ClientToServer
                ? PacketDirection.ServerToClient : PacketDirection.ClientToServer },
        ]);
        using var h = await Harness.CreateAsync(new OpcodeTable(original.Profile, entries));
        var row = h.Context(plugin, plugin == "equipment"
            ? new() { ["persist_worn_state"] = false }
            : new() { ["enabled"] = true, ["publish_equipped_view_push"] = true });
        IPlugin instance = plugin == "equipment" ? new EquipmentPlugin() : new InventoryViewPlugin();
        var error = await Assert.ThrowsAsync<ConfigException>(async () =>
        {
            await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(h.Context("dialect.messages"), default);
            await instance.ActivateAsync(row, default);
        });
        Assert.Contains(name, error.Message);
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(804, new byte[8]), default);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(806, new byte[12]), default);
        Assert.Equal(new[] { false, false }, h.Handled);
        Assert.Empty(h.Session.Sent);
    }

    [Fact]
    public async Task RequestOnlyViewNeedsNoSendersAndUnloadsItsRegistration()
    {
        var original = LoadTable();
        using var h = await Harness.CreateAsync(new OpcodeTable(original.Profile,
            original.All.Where(e => e.Name == "gcpReqInventoryUseItem")));
        var row = h.Context("inventoryview", new() { ["enabled"] = true, ["view_request"] = "gcpReqInventoryUseItem" });
        await new InventoryViewPlugin().ActivateAsync(row, default);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(806, new byte[12]), default);
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
        row.Unwind();
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(806, new byte[12]), default);
        Assert.Equal(new[] { true, false }, h.Handled);
        Assert.Empty(h.Session.Sent);
    }

    [Fact]
    public async Task PersistPresetPagesWithoutAPlayerStateFailsLoudAtActivation()
    {
        // The pages cannot survive a restart without a store, so the switch is refused before the row
        // registers anything rather than silently reverting to the derived bar after a restart.
        using var h = await Harness.CreateAsync(LoadTable());
        var row = h.Context("inventoryview",
            new() { ["enabled"] = true, ["publish_equipped_view_push"] = true, ["persist_preset_pages"] = true });
        var sender = new SnapshotSender("kr-1256/game");
        row.Effect(h.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(sender));
        row.Effect(h.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(sender));
        var error = await Assert.ThrowsAsync<ConfigException>(() => new InventoryViewPlugin().ActivateAsync(row, default).AsTask());
        Assert.Contains("persist_preset_pages", error.Message);
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
    }

    [Fact]
    public async Task ViewPublisherUsesResolvedSendersForLocalAndRemoteSnapshotsAndUnloads()
    {
        var original = LoadTable();
        using var h = await Harness.CreateAsync(new OpcodeTable(original.Profile,
            original.All.Select(e => e with { Value = checked((ushort)(e.Value + 3000)) })));
        var codecRow = h.Context("dialect.messages", new() { ["equipment"] = false });
        await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(codecRow, default);
        var row = h.Context("inventoryview", new() { ["enabled"] = true, ["publish_equipped_view_push"] = true });
        await new InventoryViewPlugin().ActivateAsync(row, default);
        var view = h.Services.Get<IEquippedViewPush>();
        await view.SendSelectionAsync(h.Session, 42, 300001, [300001, 202001, 205001], default);
        await view.SendRemoteAsync(h.Session, 43, 300001, [300001, 202001, 205001], default);
        Assert.Equal(new ushort[] { 3836, 3837, 3806, 3806 }, h.Session.Sent.Select(f => f.Opcode));
        row.Unwind();
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(3806, new byte[12]), default);
        Assert.False(Assert.Single(h.Handled));
        Assert.Single(h.Services.GetAll<IMessageSender<LocalEquipmentViewUpdated>>());
        Assert.Single(h.Services.GetAll<IMessageSender<RemoteEquipmentViewUpdated>>());
        codecRow.Unwind();
        Assert.Empty(h.Services.GetAll<IMessageSender<LocalEquipmentViewUpdated>>());
        Assert.Empty(h.Services.GetAll<IMessageSender<RemoteEquipmentViewUpdated>>());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task MissingOrAmbiguousTypedSendersFailBeforeRegisteringTheRequest(int localCount, int remoteCount)
    {
        using var h = await Harness.CreateAsync(LoadTable());
        var row = h.Context("inventoryview", new() { ["enabled"] = true, ["publish_equipped_view_push"] = true });
        for (var i = 0; i < localCount; i++)
            row.Effect(h.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(new SnapshotSender("kr-1256/game")));
        for (var i = 0; i < remoteCount; i++)
            row.Effect(h.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(new SnapshotSender("kr-1256/game")));
        // A sender for another connection must never satisfy or disambiguate this row.
        row.Effect(h.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(new SnapshotSender("kr-1256/relay")));
        row.Effect(h.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(new SnapshotSender("kr-1256/relay")));
        var error = await Assert.ThrowsAsync<ConfigException>(() => new InventoryViewPlugin().ActivateAsync(row, default).AsTask());
        Assert.Contains(localCount != 1 ? nameof(LocalEquipmentViewUpdated) : nameof(RemoteEquipmentViewUpdated), error.Message);
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(806, new byte[12]), default);
        Assert.False(Assert.Single(h.Handled));
    }

    [Fact]
    public async Task TypedPublicationOwnsTheCommittedSnapshotBeforeAwaitingPresetState()
    {
        using var h = await Harness.CreateAsync(LoadTable());
        var row = h.Context("inventoryview", new() { ["enabled"] = true, ["publish_equipped_view_push"] = true });
        var sender = new SnapshotSender("kr-1256/game");
        var otherConnection = new SnapshotSender("kr-1256/relay");
        var state = new WaitingPresetState();
        row.Effect(h.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(sender));
        row.Effect(h.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(sender));
        row.Effect(h.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(otherConnection));
        row.Effect(h.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(otherConnection));
        row.Effect(h.Services.Register<IPlayerInventoryState>(state));
        await new InventoryViewPlugin().ActivateAsync(row, default);
        var push = h.Services.Get<IEquippedViewPush>();
        int[] items = [300001, 201003, 203073, 205001];
        var send = push.SendSelectionAsync(h.Session, 42, 300001, items, default).AsTask();
        Assert.Equal(1, state.Reads);
        Assert.False(send.IsCompleted);
        items[2] = 204003;
        state.Result.SetResult(2);
        await send;
        var local = Assert.Single(sender.Local);
        Assert.Equal(new[] { 300001, 201003, 203073, 205001 }, local.EquippedItemIds);
        Assert.Equal(203073, local.PrimaryWeaponId);
        Assert.Equal(205001, local.SpecialWeaponId);
        Assert.Equal((byte)2, local.ActiveWeaponPresetSlot);
        await push.SendRemoteAsync(h.Session, 43, 300001, items, default);
        items[2] = 202001;
        var remote = Assert.Single(sender.Remote);
        Assert.Equal(43, remote.UserNum);
        Assert.Contains(204003, remote.EquippedItemIds);
        Assert.DoesNotContain(202001, remote.EquippedItemIds);
        Assert.Equal(1, state.Reads);
        Assert.Single(sender.Local);
        Assert.Empty(otherConnection.Local);
        Assert.Empty(otherConnection.Remote);
        Assert.Empty(h.Session.Sent);
    }

    [Fact]
    public async Task CodecFreezesAllBodiesBeforeTheFirstSendYields()
    {
        using var h = await Harness.CreateAsync(LoadTable());
        await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(h.Context("dialect.messages", new() { ["equipment"] = false }), default);
        var sender = Assert.Single(h.Services.GetAll<IMessageSender<LocalEquipmentViewUpdated>>());
        var session = new WaitingSession(h.Session);
        int[] items = [300001, 203073, 205001];
        var send = sender.SendAsync(session, new(42, 300001, items, 2, 203073, 205001), default).AsTask();
        Assert.Single(h.Session.Sent);
        Assert.False(send.IsCompleted);
        items[1] = 204003;
        session.Resume.SetResult();
        await send;
        Assert.Equal(new ushort[] { 836, 837, 806 }, h.Session.Sent.Select(frame => frame.Opcode));
        Assert.Equal("0200000000", Convert.ToHexString(h.Session.Sent[0].Payload.ToArray()));
        Assert.Equal("2A000000" + "E1930400" + "03000000" + "E1930400" + "41190300" + "C9200300" + new string('0', 32),
            Convert.ToHexString(h.Session.Sent[2].Payload.ToArray()));
        Assert.Equal(203073, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.Session.Sent[1].Payload.AsSpan(9)));
    }

    [Fact]
    public async Task DisabledRowsDoNotRequireAnyPacketNames()
    {
        using var h = await Harness.CreateAsync(new OpcodeTable(LoadTable().Profile, []));
        // The shared row now mounts the login path in a real profile; this scenario asks only that disabled
        // feature rows need no packet names, so it turns login, the main-menu probe, the round signature
        // and the bag row off.
        await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(h.Context("dialect.messages",
            new() { ["login"] = false, ["main_menu_init"] = false, ["session_signature"] = false, ["inventory"] = false, ["equipment"] = false, ["equipment_view"] = false }), default);
        await new EquipmentPlugin().ActivateAsync(h.Context("equipment", new() { ["enabled"] = false }), default);
        Assert.Empty(h.Services.GetAll<IInboundMessageBinding<EquipmentChangeRequested>>());
        Assert.Empty(h.Services.GetAll<IMessageSender<EquipmentChangeReplied>>());
        Assert.Empty(h.Services.Get<IMessageRouter>().Handlers);
        await h.Router.DispatchAsync("kr-1256/game", h.Session, new(804, new byte[7]), default);
        Assert.False(Assert.Single(h.Handled));
        await new InventoryViewPlugin().ActivateAsync(h.Context("inventoryview", new() { ["enabled"] = false }), default);
        Assert.False(h.Services.TryGet<IEquippedViewPush>(out _));
    }

    private sealed class SnapshotSender(string protocol)
        : IMessageSender<LocalEquipmentViewUpdated>, IMessageSender<RemoteEquipmentViewUpdated>
    {
        public string Protocol => protocol;
        internal List<LocalEquipmentViewUpdated> Local { get; } = [];
        internal List<RemoteEquipmentViewUpdated> Remote { get; } = [];
        public ValueTask SendAsync(ISession session, LocalEquipmentViewUpdated message, CancellationToken ct)
        {
            Local.Add(message);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, RemoteEquipmentViewUpdated message, CancellationToken ct)
        {
            Remote.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WaitingPresetState : IPlayerInventoryState
    {
        internal TaskCompletionSource<byte> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct)
        {
            Reads++;
            return new(Result.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        }
        public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct) =>
            throw new InvalidOperationException("A view publication must never write preset state.");

        // A default-off publication never reads pages; reaching this member would mean the switch leaked.
        public ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("A derived view publication must never read preset pages.");

        public ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct) =>
            throw new InvalidOperationException("This fixture does not initialize preset pages.");
        public ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct) =>
            throw new InvalidOperationException("A view publication must never write preset pages.");
    }

    private sealed class WaitingSession(RecordingSession inner) : ISession
    {
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Id => inner.Id;
        public BubbleFighter.Fantasy.Abstractions.Net.IConnection Connection => inner.Connection;
        public ClientProfile Profile => inner.Profile;
        public System.Collections.Concurrent.ConcurrentDictionary<string, object> Items => inner.Items;
        public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await inner.SendAsync(opcode, payload, ct);
            if (inner.Sent.Count == 1)
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
    }

    private sealed class Harness : IDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<PluginContext> _contexts = [];
        internal RecordingSession Session { get; } = new(1);
        internal List<bool> Handled { get; } = [];
        internal IPacketRouter Router => Services.Get<IPacketRouter>();
        internal IOpcodeTableRegistry Tables => Services.Get<IOpcodeTableRegistry>();

        internal PluginContext Context(string plugin, Dictionary<string, object?>? config = null)
        {
            PluginRow row;
            if (plugin == "dialect.messages")
            {
                row = SharedDialectRows.Kr1256(config);
            }
            else if (plugin == "inventoryview")
            {
                // A view-request row resolves its build and tables from the row lineage, as the
                // profile bundles mount it; rows that log no request never read these values.
                var merged = new Dictionary<string, object?>(config ?? [], StringComparer.Ordinal)
                {
                    ["packets_directory"] = SharedDialectRows.PacketsDirectory(),
                };
                row = new PluginRow
                {
                    Id = plugin, Plugin = plugin, Source = "test",
                    ClientBuild = ClientBuild.Parse("kr:1256:1304"), Lineage = ["kr-1256", "kr-812"],
                    Config = merged,
                };
            }
            else
            {
                row = new PluginRow { Id = plugin, Plugin = plugin, Source = "test", Config = config };
            }
            var context = new PluginContext(row, Services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        internal static async Task<Harness> CreateAsync(IOpcodeTable table)
        {
            var h = new Harness();
            var root = h.Context("root");
            await new PacketRouterPlugin().ActivateAsync(h.Context("router"), default);
            await new MessageRouterPlugin().ActivateAsync(h.Context("messages"), default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(h.Context("tables"), default);
            root.Effect(h.Tables.Register(table));
            root.Effect(h._events.On<PacketReceivedEvent>(e => h.Handled.Add(e.HasHandler)));
            return h;
        }

        public void Dispose()
        {
            for (var i = _contexts.Count - 1; i >= 0; i--) _contexts[i].Unwind();
        }
    }
}
