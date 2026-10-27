using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Equipment;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Exercises the shared inventory action binding (<c>inventory_actions</c> on the <c>dialect.messages</c> row)
/// together with the <c>inventory.throw</c> handler through real protocol routing, expiry and in-memory
/// transactions. Pins the request and reply bodies of every audited build.
/// </summary>
public sealed class InventoryThrowDialectTests
{
    [Theory]
    [InlineData("kr-812", 807, 812)]
    [InlineData("cn-279", 807, 812)]
    [InlineData("cn-324", 807, 812)]
    [InlineData("id-400", 807, 812)]
    [InlineData("id-1809", 807, 812)]
    [InlineData("cn-717", 807, 812)]
    [InlineData("cn-406", 807, 812)]
    [InlineData("cn-404", 807, 812)]
    [InlineData("cn-408", 807, 812)]
    [InlineData("cn-706", 807, 812)]
    [InlineData("kr-1256", 808, 815)]
    [InlineData("cn-774", 808, 815)]
    [InlineData("tw-454", 808, 815)]
    public async Task AuditedClientsShareRemovalAndEquipmentSynchronization(string build, int request, int reply)
    {
        await using var rig = await Rig.CreateAsync(build);
        await rig.SeedAsync(42);
        await rig.SeedAsync(43);
        var session = rig.Session(42);
        await rig.RawAsync(session, (ushort)request, BitConverter.GetBytes(202003));

        var response = Assert.Single(session.Sent);
        Assert.Equal(reply, response.Opcode);
        Assert.Equal(8, response.Body.Length);
        Assert.Equal(202003, BinaryPrimitives.ReadInt32LittleEndian(response.Body));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(response.Body.AsSpan(4)));
        Assert.Equal(["event", "reply", "inventory", "view"], rig.Order);
        Assert.Equal([300001, 202001], rig.Publications.View);
        Assert.Equal([300001, 202001], rig.Publications.Bag!.Select(row => row.ItemId));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            Assert.Equal([300001, 202001], (await rig.Store.ListSlotsAsync(42, bag, 512, default)).Select(row => row.ItemId));
            Assert.Equal(3, (await rig.Store.ListSlotsAsync(43, bag, 512, default)).Count);
        }
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-717")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task ActiveEntitlementsAreRefusedBeforeAnyEquipmentPublication(string build)
    {
        await using var rig = await Rig.CreateAsync(build);
        await rig.SeedAsync(42, active: true);
        var session = rig.Session(42);
        await rig.RawAsync(session, rig.Request, BitConverter.GetBytes(202003));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(Assert.Single(session.Sent).Body.AsSpan(4)));
        Assert.Equal(["reply"], rig.Order);
        Assert.Equal(3, (await rig.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, 512, default)).Count);
        Assert.Null(rig.Publications.Bag);
    }

    [Theory]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-404", false)]
    [InlineData("cn-408", true)]
    [InlineData("cn-706", true)]
    [InlineData("kr-812", true)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    public async Task NativeCategoryRestrictionComesFromBuildData(string build, bool excluded)
    {
        await using var rig = await Rig.CreateAsync(build);
        await rig.SeedAsync(42, target: 301001);
        var session = rig.Session(42);
        await rig.RawAsync(session, rig.Request, BitConverter.GetBytes(301001));
        Assert.Equal(excluded ? 1 : 0,
            BinaryPrimitives.ReadInt32LittleEndian(Assert.Single(session.Sent).Body.AsSpan(4)));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(excluded, (await rig.Store.ListSlotsAsync(42, bag, 512, default)).Any(row => row.ItemId == 301001));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task MalformedActionCannotReachTheTransaction(int length)
    {
        await using var rig = await Rig.CreateAsync("kr-1256");
        await rig.SeedAsync(42);
        var session = rig.Session(42);
        await Assert.ThrowsAsync<PacketFormatException>(() => rig.RawAsync(session, rig.Request, new byte[length]));
        Assert.Empty(session.Sent);
        Assert.Empty(rig.Order);
        Assert.Equal(3, (await rig.Store.ListSlotsAsync(42, InventoryBag.CharacterBag, 512, default)).Count);
    }

    [Fact]
    public async Task NamedOpcodeRemappingAndUnloadDoNotLeaveAHandlerOrSender()
    {
        await using var rig = await Rig.CreateAsync("kr-1256", 41010, 41011);
        await rig.SeedAsync(42);
        var session = rig.Session(42);
        await rig.RawAsync(session, 41010, BitConverter.GetBytes(202003));
        Assert.Equal((ushort)41011, Assert.Single(session.Sent).Opcode);
        session.Sent.Clear();
        await rig.Domain.UnwindAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RawAsync(session, 41010, BitConverter.GetBytes(202003)));
        await rig.Dialect.UnwindAsync();
        Assert.Empty(rig.Services.GetAll<IInboundMessageBinding<InventoryThrowRequested>>());
        Assert.Empty(rig.Services.GetAll<IMessageSender<InventoryThrowReplied>>());
        Assert.False(await rig.Services.Get<IMessageRouter>().DispatchAsync(rig.Protocol, session, new InventoryThrowRequested(202003), default));
        await rig.RawAsync(session, 41010, BitConverter.GetBytes(202003));
        Assert.Empty(session.Sent);
    }

    private sealed class Rig(string build) : IAsyncDisposable
    {
        internal string Protocol => build + "/game";
        internal ushort Request { get; private set; }
        internal ServiceRegistry Services { get; } = new();
        private EventBus Events { get; } = new(NullLogger.Instance);
        internal MemoryInventoryRepository Store { get; } = new(new MemoryPlayerTable(4));
        internal List<string> Order { get; } = [];
        internal Publications Publications { get; private set; } = null!;
        internal PluginContext Domain { get; private set; } = null!;
        internal PluginContext Dialect { get; private set; } = null!;
        private readonly List<PluginContext> _contexts = [];

        internal static async Task<Rig> CreateAsync(string build, ushort? request = null, ushort? reply = null)
        {
            var rig = new Rig(build);
            try
            {
                var root = rig.Context("root");
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new MessageRouterPlugin().ActivateAsync(root, default);
                await new OpcodeTableRegistryPlugin().ActivateAsync(root, default);
                var profile = Profile(build);
                var table = request is { } inbound && reply is { } outbound ? new OpcodeTable(profile,
                [
                    new(inbound, PacketDirection.ClientToServer, "gcpReqInventoryThrowItem", Evidence.Verified, null, null),
                    new(outbound, PacketDirection.ServerToClient, "gspRepInventoryThrowItem", Evidence.Verified, null, null),
                ]) : OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", build, "opcodes.yaml"), profile)
                    .ForConnection(OpcodeConnection.Game);
                rig.Request = table.C2S("gcpReqInventoryThrowItem");
                root.Effect(rig.Services.Get<IOpcodeTableRegistry>().Register(table));
                var selection = new Selection(rig.Store);
                root.Effect(rig.Services.Register<IEquipmentSelection>(selection));
                root.Effect(rig.Services.Register<IInventoryItemRemoval>(new InventoryExpirySweep(rig.Store, selection,
                    StarterKit.Default, new Clock())));
                rig.Publications = new(rig.Order);
                root.Effect(rig.Services.Register<IInventoryPushService>(rig.Publications));
                root.Effect(rig.Services.Register<IEquippedViewPush>(rig.Publications));
                root.Effect(rig.Events.OnAsync<EquipmentSelectionCommitted>((_, _) =>
                {
                    rig.Order.Add("event");
                    return ValueTask.CompletedTask;
                }));
                var tables = PacketTables.Load(Path.Combine(Root(), "packets"));
                rig.Dialect = rig.Context("dialect.messages", new()
                    { ["protocol"] = rig.Protocol, ["packets_directory"] = Path.Combine(Root(), "packets") });
                new InventoryActionsBinding(rig.Protocol, table, tables,
                    tables.NearestBuild([build]) ?? throw new ConfigException($"No packet build for '{build}'."))
                    .Bind(rig.Dialect, rig.Services.Get<IMessageRouter>());
                rig.Domain = rig.Context("inventory.throw", new()
                    { ["protocol"] = rig.Protocol, ["packets_directory"] = Path.Combine(Root(), "packets") });
                await new InventoryThrowPlugin().ActivateAsync(rig.Domain, default);
                return rig;
            }
            catch { await rig.DisposeAsync(); throw; }
        }

        private PluginContext Context(string name, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow { Id = name, Plugin = name, Source = "inventory-action-test",
                Lineage = [build], Config = config }, Services, Events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        internal async Task SeedAsync(long user, bool active = false, int target = 202003)
        {
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                InventorySlotRecord Row(int id, bool worn) => new(user, bag, id, id, 1,
                    worn ? InventorySlotRecord.WornFlagBit : (byte)0) { Lifetime = InventoryLifetime.Permanent };
                await Store.ReplaceSlotsAsync(user, bag,
                    [Row(300001, true), Row(202001, false), Row(target, true) with
                        { Lifetime = InventoryLifetime.Until(Now.AddDays(active ? 1 : -1)) }], default);
            }
        }

        internal Session Session(int user) => new(Profile(build), user, Order);
        internal Task RawAsync(Session session, ushort opcode, byte[] body) =>
            Services.Get<IPacketRouter>().DispatchAsync(Protocol, session, new(opcode, body), default).AsTask();
        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
    }

    /// <summary>A table that retypes the request's item id is refused when the dialect row activates.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheDialectActivates()
    {
        var directory = PacketTableEdits.WithFieldType("InventoryActionRequest", "item_id", "u32", "wstr");
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var root = new PluginContext(new PluginRow { Id = "root", Plugin = "root", Source = "test" }, services, events, NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(root, default);
            await new MessageRouterPlugin().ActivateAsync(root, default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(root, default);
            var table = OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", "kr-812", "opcodes.yaml"), Profile("kr-812"))
                .ForConnection(OpcodeConnection.Game);
            root.Effect(services.Get<IOpcodeTableRegistry>().Register(table));
            var tables = PacketTables.Load(directory.FullName);
            var context = new PluginContext(new PluginRow
            {
                Id = "dialect.messages", Plugin = "dialect.messages", Source = "test", Lineage = ["kr-812"],
                Config = new Dictionary<string, object?> { ["protocol"] = "kr-812/game", ["packets_directory"] = directory.FullName },
            }, services, events, NullLogger.Instance);
            try
            {
                var error = Assert.Throws<ConfigException>(() =>
                    new InventoryActionsBinding("kr-812/game", table, tables, "kr-812").Bind(context, new Messages()));
                Assert.Contains("item_id", error.Message, StringComparison.Ordinal);
                Assert.Contains("Inventory throw", error.Message, StringComparison.Ordinal);
                Assert.Empty(services.GetAll<IInboundMessageBinding<InventoryThrowRequested>>());
                Assert.Empty(services.GetAll<IMessageSender<InventoryThrowReplied>>());
            }
            finally { await context.UnwindAsync(); }
        }
        finally { await root.UnwindAsync(); }
    }

    /// <summary>An <see cref="IMessageRouter"/> stub for binding-level mount checks.</summary>
    private sealed class Messages : IMessageRouter
    {
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<TMessage>(string protocol, ISession session, TMessage message,
            CancellationToken ct) where TMessage : IMessage => ValueTask.FromResult(false);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Selection(IInventoryRepository inventory) : IEquipmentSelection
    {
        private static readonly InventoryWearGroup[] Groups =
            [new(300001, 300031, 300001, true), new(202000, 204999, 202001, true)];
        public async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct) =>
            await inventory.UpdateWornSelectionAsync(userNum, Groups, null, false, ct)
                ?? throw new InvalidOperationException("Selection returned no inventory.");
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            inventory.UpdateWornSelectionAsync(userNum, Groups, itemId, worn, ct);
    }

    private sealed class Publications(List<string> order) : IInventoryPushService, IEquippedViewPush
    {
        internal IReadOnlyList<int>? View { get; private set; }
        internal IReadOnlyList<InventorySlotRecord>? Bag { get; private set; }
        public ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
        {
            order.Add("inventory");
            Bag = slots;
            return ValueTask.CompletedTask;
        }
        public ValueTask SendSelectionAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct)
        {
            order.Add("view");
            View = equippedIds;
            return ValueTask.CompletedTask;
        }
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Session(ClientProfile profile, int user, List<string> order) : ISessionWithFeatures
    {
        public long Id => user;
        public BubbleFighter.Fantasy.Abstractions.Net.IConnection Connection { get; } = new StubConnection(user);
        public ISessionFeatures Features { get; } = TestSessionFeatures.WithPlayer(user);
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            order.Add("reply");
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubConnection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }

    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..], CultureInfo.InvariantCulture), 0, 0, build);
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("The test must run below the repository.");
    }
}
