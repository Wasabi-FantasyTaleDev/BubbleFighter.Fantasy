using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Lobby;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.InventoryView;
using BubbleFighter.Fantasy.Equipment;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class StoredCharacterSceneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSelectionAndUnloadedSceneDoNotNotifyObservers(bool unload)
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        var (room, seat) = h.Registry.RequireGame(h.Host);
        await h.DispatchAsync(h.Guest, 517, BitConverter.GetBytes(room.Id));
        var before = h.Guest.Opcodes.Count();
        if (unload) h.UnloadScene();
        await h.DispatchAsync(h.Host, 804, BitConverter.GetBytes(unload ? 204123L : 299999L));
        Assert.Empty(h.Guest.Opcodes.Skip(before));
        Assert.Equal(InitialItems(11, 300010).Order(), seat.EquippedItemIds!.Order());
    }

    [Theory]
    [InlineData(300001, 300010)]
    [InlineData(204123, 203073)]
    [InlineData(201001, 201119)]
    [InlineData(205002, 205001)]
    [InlineData(207002, 207001)]
    [InlineData(100224, 0)]
    public async Task LiveSelectionUpdatesPeerAndSurvivesReadyRefreshAndStart(int item, int displaced)
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        var (room, hostSeat) = h.Registry.RequireGame(h.Host);
        await h.DispatchAsync(h.Guest, 517, BitConverter.GetBytes(room.Id));
        var peerBefore = h.Guest.Opcodes.Count();
        var ownBefore = h.Host.Opcodes.Count();
        await h.DispatchAsync(h.Host, 804, BitConverter.GetBytes((long)item));
        var character = item == 300001 ? item : 300010;
        var items = InitialItems(11, 300010).Where(id => id != displaced).Append(item).Distinct().ToArray();
        var worn = new Dictionary<int, int[]> { [11] = items };
        Assert.Equal(items.Order(), hostSeat.EquippedItemIds!.Order());
        Assert.Equal(character, hostSeat.CharacterId);
        Assert.Equal(new ushort[] { 811, 803, 836, 837, 806 }, h.Host.Opcodes.Skip(ownBefore));
        Assert.Equal(new ushort[] { 806 }, h.Guest.Opcodes.Skip(peerBefore));
        AssertView(h.Host, 11, character, items);
        AssertView(h.Guest, 11, character, items);
        AssertPreset(h.Host, item == 204123 ? item : 203073, item == 205002 ? item : 205001);
        AssertPreset(h.Guest, 204123, 205001);
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        AssertInstall(h.Host, [(11, "host", character), (22, "guest", 300017)], worn);
        AssertSeatReply(h.Host, hostSeat, [(11, character), (22, 300017)], worn);
        await h.DispatchAsync(h.Guest, 505, WireLengthSupport.Written(w =>
        { w.WriteInt32(1); w.WriteInt32(0); w.WriteInt32(0); }));
        var (_, guestSeat) = h.Registry.RequireGame(h.Guest);
        AssertSeatReply(h.Host, guestSeat, [(11, character), (22, 300017)], worn);
        var combat = new InGameFlows(new InGameOptions { StartMatches = true }, h.Registry,
            NullLogger.Instance, signatureSender: new RecordingSignatureSender());
        await combat.Start.StartMatchAsync(room, default, h.Host.Id);
        Assert.Equal(RoomPhase.Starting, room.Phase);
        AssertSeatReply(h.Guest, guestSeat, [(11, character), (22, 300017)], worn);
    }

    [Fact]
    public async Task PlazaWearRefreshesObserversWithoutInstallingTheirSceneOrChangingTheirPreset()
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, 106, []);
        await h.DispatchAsync(h.Guest, 106, []);
        var before = h.Guest.Opcodes.Count();
        await h.DispatchAsync(h.Host, 804, BitConverter.GetBytes(204123L));
        var items = InitialItems(11, 300010).Select(id => id == 203073 ? 204123 : id).ToArray();
        Assert.Equal(new ushort[] { 806 }, h.Guest.Opcodes.Skip(before));
        AssertView(h.Guest, 11, 300010, items);
        await h.DispatchAsync(h.Guest, 106, []);
        AssertInstall(h.Guest, [(11, "host", 300010), (22, "guest", 300017)], new() { [11] = items });
    }

    [Theory]
    [InlineData(402)]
    [InlineData(503)]
    [InlineData(107)]
    [InlineData(108)]
    public async Task CreateWithoutFightersUsesStoredCharacterInEveryProfileWriter(int opcode)
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, opcode, CreateBody(opcode));
        var (room, seat) = h.Registry.RequireGame(h.Host);
        Assert.Equal(300010, seat.CharacterId);
        AssertInstall(h.Host, [(11, "host", 300010)]);
        AssertParty(h.Host, 11, "host", 300010);
        AssertView(h.Host, 11, 300010);
        AssertPreset(h.Host, 203073, 205001);
        AssertSeatReply(h.Host, seat, [(11, 300010)]);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(517)]
    public async Task JoinRefreshAndReadyKeepBothPlayersStoredCharacters(int joinOpcode)
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        var (room, hostSeat) = h.Registry.RequireGame(h.Host);
        await h.DispatchAsync(h.Guest, joinOpcode, WireLengthSupport.Written(w =>
        {
            w.WriteInt32(room.Id);
            if (joinOpcode == 403) w.WriteWideStringPrefixed(string.Empty);
        }));
        var (_, guestSeat) = h.Registry.RequireGame(h.Guest);
        Assert.Equal(300017, guestSeat.CharacterId);
        AssertInstall(h.Guest, [(11, "host", 300010), (22, "guest", 300017)]);
        AssertParty(h.Guest, 22, "guest", 300017);
        AssertView(h.Guest, 22, 300017);
        // Both join paths announce the new seat to the host with S2C 503; neither reinstalls its room.
        AssertSeatReply(h.Host, guestSeat, [(11, 300010), (22, 300017)]);
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        AssertInstall(h.Host, [(11, "host", 300010), (22, "guest", 300017)]);
        AssertSeatReply(h.Host, hostSeat, [(11, 300010), (22, 300017)]);
        await h.DispatchAsync(h.Guest, 505, WireLengthSupport.Written(w =>
        {
            w.WriteInt32(1); w.WriteInt32(0); w.WriteInt32(0);
        }));
        AssertSeatReply(h.Host, guestSeat, [(11, 300010), (22, 300017)]);
        AssertSeatReply(h.Guest, guestSeat, [(11, 300010), (22, 300017)]);
    }

    [Fact]
    public async Task PlazaAndReturnReadLatestSelectionInsteadOfTheLoginSnapshot()
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(h.Host, 106, []);
        AssertInstall(h.Host, [(11, "host", 300010)]);
        AssertParty(h.Host, 11, "host", 300010);
        await h.DispatchAsync(h.Host, 402, CreateBody(402));
        await h.Selection.ApplyAsync(11, 300001, true, default);
        await h.DispatchAsync(h.Host, 404, []);
        Assert.Null(h.Registry.TryGet(h.Host.Id));
        AssertInstall(h.Host, [(11, "host", 300001)]);
        AssertParty(h.Host, 11, "host", 300001);
        await h.Selection.ApplyAsync(11, 300010, true, default);
        await h.DispatchAsync(h.Host, 106, []);
        AssertInstall(h.Host, [(11, "host", 300010)]);
    }

    private static byte[] CreateBody(int opcode) => WireLengthSupport.Written(w =>
    {
        if (opcode == 402)
        {
            var setting = RoomWireSupport.OrdinarySettingWithMap();
            (setting with { Map = setting.Map with { Unknown4 = 1 } }).Write(w);
            w.WriteInt32(0);
            return;
        }
        if (opcode == 503) w.WriteInt32(0);
        if (opcode == 503) WriteMap(w);
        if (opcode == 503) w.WriteInt32(0);
        for (var i = 0; i < 13; i++) w.WriteInt32(0);
        w.WriteByte(0);
        if (opcode == 107) WriteMap(w);
        if (opcode != 108) w.WriteInt32(0);
    });

    private static void WriteMap(PacketWriter w)
    {
        w.WriteWideStringPrefixed("rel_Village00_boomhill");
        w.WriteWideStringPrefixed("Boomhill");
        w.WriteWideStringPrefixed("rel_Village00_boomhill");
    }

    private static void AssertInstall(RoomPushTests.RecordingSession session,
        (int User, string Name, int Character)[] expected, Dictionary<int, int[]>? worn = null)
    {
        var r = new PacketReader(session.Body(117));
        SessionSettingBlob.Read(ref r);
        r.ReadInt32(); r.ReadInt32();
        Assert.Equal((uint)expected.Length, r.ReadUInt32());
        foreach (var row in expected)
            r.ReadBytes(WireLengthSupport.Written(w => RoomPlayerInfo.Default(row.User, row.Name).Write(w)).Length);
        AssertProfiles(ref r, expected.Select(row => (row.User, row.Character)).ToArray(), worn);
    }

    private static void AssertSeatReply(RoomPushTests.RecordingSession session, RoomSeat seat,
        (int User, int Character)[] expected, Dictionary<int, int[]>? worn = null)
    {
        var r = new PacketReader(session.Body(503));
        Assert.Equal(seat.Slot, r.ReadInt32());
        r.ReadBytes(WireLengthSupport.Written(w => seat.ToPlayerInfo().Write(w)).Length);
        AssertProfiles(ref r, expected, worn);
    }

    private static void AssertProfiles(ref PacketReader r, (int User, int Character)[] expected,
        Dictionary<int, int[]>? worn = null)
    {
        Assert.Equal((uint)expected.Length, r.ReadUInt32());
        foreach (var (user, character) in expected)
        {
            Assert.Equal(user, r.ReadInt32());
            Assert.Equal(character, r.ReadInt32());
            var items = new int[r.ReadUInt32()];
            for (var i = 0; i < items.Length; i++) items[i] = r.ReadInt32();
            var expectedItems = worn?.GetValueOrDefault(user) ?? InitialItems(user, character);
            Assert.Equal(expectedItems.Where(id => id != character).Order(), items.Order());
            for (var v = 0; v < 2; v++) r.ReadBytes(checked((int)r.ReadUInt32() * 4));
            r.ReadBytes(checked((int)r.ReadUInt32()));
            Assert.Equal(0u, r.ReadUInt32());
        }
    }

    private static void AssertParty(RoomPushTests.RecordingSession session, int user, string name, int character)
    {
        var r = new PacketReader(session.Body(201));
        Assert.Equal(2, r.ReadInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(user, r.ReadInt32());
        Assert.Equal(name, r.ReadWideStringPrefixed());
        r.ReadBytes(16);
        AssertProfiles(ref r, [(user, character)]);
    }

    private static void AssertView(RoomPushTests.RecordingSession session, int user, int character, int[]? expected = null)
    {
        // The peer equipped-view exchange delivers several 806 records per session; the
        // client folds them per identity, so the assertion locates the record by identity
        // rather than taking the last one.
        var body = session.Bodies(806).Last(b => BinaryPrimitives.ReadInt32LittleEndian(b) == user);
        Assert.Equal(character, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
        var count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(8));
        var items = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12 + 4 * i)));
        Assert.Equal((expected ?? InitialItems(user, character)).Order(), items.Order());
    }

    private static int[] InitialItems(int user, int character) =>
        [character, user == 11 ? 203073 : 204123, 201119, 205001, 207001];

    private static void AssertPreset(RoomPushTests.RecordingSession session, int primary, int special)
    {
        var body = session.Body(837);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(5)));
        for (var slot = 0; slot < 3; slot++)
        {
            Assert.Equal(primary, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(9 + 12 * slot)));
            Assert.Equal(special, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(13 + 12 * slot)));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<PluginContext> _contexts = [];
        internal RoomPushTests.RecordingSession Host { get; } = Player(101, 11, "host");
        internal RoomPushTests.RecordingSession Guest { get; } = Player(102, 22, "guest");
        internal RoomRegistry Registry => (RoomRegistry)_services.Get<IMatchRoomDirectory>();
        internal IEquipmentSelection Selection => _services.Get<IEquipmentSelection>();
        private PluginContext _sceneContext = null!;

        internal static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            try
            {
                var root = h.Context("router");
                var store = new MemoryInventoryRepository(new MemoryPlayerTable(8));
                root.Effect(h._services.Register<IInventoryRepository>(store));
                root.Effect(h._services.RegisterMany<IMessageSender<MatchSessionSignature>>(new RecordingSignatureSender()));
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new OpcodeTableRegistryPlugin().ActivateAsync(root, default);
                root.Effect(h._services.Get<IOpcodeTableRegistry>().Register(
                    BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
                        Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"),
                        TestWireDialects.Get("kr-1256").Profile)));
                await new MessageRouterPlugin().ActivateAsync(h.Context("core.message-router"), default);
                await new BubbleFighter.Fantasy.Dialect.Messages.MessagesPlugin().ActivateAsync(
                    h.Context("dialect.messages", new() { ["inventory"] = true }), default);
                await new InventoryViewPlugin().ActivateAsync(h.Context("inventoryview", new()
                    { ["enabled"] = true, ["publish_equipped_view_push"] = true }), default);
                root.Effect(h._services.Register<IInventoryPushService>(new InventoryPush(
                    new LoginInventoryResolver(store, true, h.Selection),
                    h._services.GetAll<IMessageSender<InventoryReplaced>>().Single(),
                    h._services.GetAll<IMessageSender<InventoryPurchaseApplied>>().Single(),
                    h._services.GetAll<IMessageSender<InventorySlotsUpdated>>().Single())));
                h._sceneContext = h.Context("ingame", new() { ["send_plaza_snapshot"] = true });
                await new InGamePlugin().ActivateAsync(h._sceneContext, default);
                await new EquipmentPlugin().ActivateAsync(h.Context("equipment"), default);
                foreach (var (user, character) in new[] { (11, 300010), (22, 300017) })
                {
                    var all = InitialItems(user, character).Concat([300001, 203073, 204123, 201001, 205002, 207002, 100224]).Distinct();
                    await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, all.Select((id, index) =>
                        new InventorySlotRecord(user, InventoryBag.CharacterBag, index + 1, id, 0,
                            (byte)(InitialItems(user, character).Contains(id) ? 1 : 0))).ToArray(), default);
                    await h.Selection.ApplyAsync(user, character, true, default);
                }
                return h;
            }
            catch { h.Dispose(); throw; }
        }

        private PluginContext Context(string name, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(name == "dialect.messages" ? SharedDialectRows.Kr1256(config) : new PluginRow { Id = name, Plugin = name, Source = "test", Config = config },
                _services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        internal async Task DispatchAsync(RoomPushTests.RecordingSession session, int opcode, byte[] body) =>
            await _services.Get<IPacketRouter>().DispatchAsync("kr-1256/game", session,
                new((ushort)opcode, body), default);

        internal void UnloadScene() => _sceneContext.Unwind();

        private static RoomPushTests.RecordingSession Player(long sessionId, int user, string name)
        {
            var session = new RoomPushTests.RecordingSession(sessionId);
            session.SetLobbyPlayer(user, name);
            return session;
        }

        public void Dispose()
        {
            for (var i = _contexts.Count - 1; i >= 0; i--) _contexts[i].Unwind();
        }
    }
}
