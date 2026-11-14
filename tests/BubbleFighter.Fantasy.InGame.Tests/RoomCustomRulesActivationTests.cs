using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Activates the declared room and game plugins to verify native custom settings, recipient ordering and teardown.</summary>
public sealed class RoomCustomRulesActivationTests
{
    /// <summary>Native and remapped requests commit the same settings, synchronize joiners, reject guests and clear on reset.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledPluginsDriveTheNativeRoomExchangeAndUnload(bool remap)
    {
        await using var rig = await Rig.CreateAsync(remap: remap);
        await rig.ActivateInGameAsync();
        Assert.Single(rig.Services.GetAll<IInboundMessageBinding<RoomCustomRulesChangeRequested>>());
        Assert.Single(rig.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
        var handler = Assert.Single(rig.Messages.Handlers, row => row.MessageType == typeof(RoomCustomRulesChangeRequested));
        Assert.Equal("kr-1256/game", handler.Protocol);
        await rig.DispatchAsync(rig.Host, "gcpReqCreateSession", rig.CreateBody(1));
        var source = rig.Room.BuildSettings!;
        var hostStart = rig.Host.Opcodes.Count();
        await rig.DispatchAsync(rig.Host, "gcpCustomGameModeSetting_",
            Convert.FromHexString("000000000100000001000100000096000000"));
        var settingOpcode = rig.Table.S2C("gspRoomChangeGameSetting");
        var replyOpcode = rig.Table.S2C("gspCustomGameModeSetting");
        Assert.Equal(new[] { settingOpcode, replyOpcode }, rig.Host.Opcodes.Skip(hostStart));
        Assert.Equal("0000000000000000", Convert.ToHexString(rig.Host.Body(replyOpcode)));
        var updated = Map(PacketCodec.Read(rig.Tables, "kr-1256", "gspRoomChangeGameSetting", rig.Host.Body(settingOpcode))["game_setting"]);
        Assert.Equal((ushort)1, updated["mode"]);
        Assert.Equal(987654, updated["unknown_24"]);
        Assert.Equal(new uint[] { 150 }, Assert.Single(Rules(updated)).Values);
        Assert.Equal(source.Bytes.ToArray(), rig.Room.BuildSettings!.Bytes.ToArray());
        Assert.Equal(source.SessionPrefix.ToArray(), rig.Room.BuildSettings.SessionPrefix.ToArray());

        await rig.DispatchAsync(rig.Guest, "gcpReqJoinSession", rig.JoinBody());
        var install = PacketCodec.Read(rig.Tables, "kr-1256", "gspGotoRoom", rig.Guest.Body(rig.Table.S2C("gspGotoRoom")));
        var joinedGame = Map(Map(install["session_setting"])["game_setting"]);
        Assert.Equal(new uint[] { 150 }, Assert.Single(Rules(joinedGame)).Values);
        var guestStart = rig.Guest.Opcodes.Count();
        hostStart = rig.Host.Opcodes.Count();
        await rig.DispatchAsync(rig.Guest, "gcpCustomGameModeSetting_", Convert.FromHexString("0100000000000000"));
        Assert.Equal(new[] { replyOpcode }, rig.Guest.Opcodes.Skip(guestStart));
        Assert.Equal("0100000000000000", Convert.ToHexString(rig.Guest.Body(replyOpcode)));
        Assert.Empty(rig.Host.Opcodes.Skip(hostStart));
        Assert.Equal(new uint[] { 150 }, Assert.Single(rig.Room.BuildSettings.CustomRules!).Values);

        guestStart = rig.Guest.Opcodes.Count();
        hostStart = rig.Host.Opcodes.Count();
        await rig.DispatchAsync(rig.Host, "gcpCustomGameModeSetting_", Convert.FromHexString("0100000000000000"));
        Assert.Equal(new[] { settingOpcode, replyOpcode }, rig.Host.Opcodes.Skip(hostStart));
        Assert.Equal(new[] { settingOpcode, replyOpcode }, rig.Guest.Opcodes.Skip(guestStart));
        Assert.Equal("0400000000000000", Convert.ToHexString(rig.Guest.Body(replyOpcode)));
        var cleared = Map(PacketCodec.Read(rig.Tables, "kr-1256", "gspRoomChangeGameSetting", rig.Guest.Body(settingOpcode))["game_setting"]);
        Assert.Empty(Rules(cleared));
        Assert.Equal(987654, cleared["unknown_24"]);

        await rig.InGame!.UnwindAsync();
        Assert.DoesNotContain(rig.Messages.Handlers, row => row.MessageType == typeof(RoomCustomRulesChangeRequested));
        Assert.Empty(rig.Services.GetAll<IMatchCapabilityDirectory>());
        await rig.Dialect.UnwindAsync();
        Assert.Empty(rig.Services.GetAll<IInboundMessageBinding<RoomCustomRulesChangeRequested>>());
        Assert.Empty(rig.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
    }

    /// <summary>A newly created fixed-top-view mode installs only the two native FirstChanges rows.</summary>
    [Fact]
    public async Task CreationProjectsModeDefaultsIntoTheNativeInstall()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ActivateInGameAsync();
        await rig.DispatchAsync(rig.Host, "gcpReqCreateSession", rig.CreateBody(37));
        var install = PacketCodec.Read(rig.Tables, "kr-1256", "gspGotoRoom", rig.Host.Body(rig.Table.S2C("gspGotoRoom")));
        var rows = Rules(Map(Map(install["session_setting"])["game_setting"]));
        Assert.Equal(new ushort[] { 31, 32 }, rows.Select(row => row.ValueId));
        Assert.All(rows, row => Assert.Equal(new uint[] { 1 }, row.Values));
    }

    /// <summary>The configured option fails during activation when native dispatch or client configuration is missing.</summary>
    [Theory]
    [InlineData(false, true, "binding")]
    [InlineData(true, false, "client-data")]
    public async Task EnabledLogicRequiresTheNativeBindingAndWorkbook(bool nativeBinding, bool clientData, string diagnostic)
    {
        await using var rig = await Rig.CreateAsync(nativeBinding: nativeBinding, clientData: clientData);
        var failure = await Assert.ThrowsAsync<ConfigException>(() => rig.ActivateInGameAsync());
        Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
        await rig.InGame!.UnwindAsync();
        Assert.DoesNotContain(rig.Messages.Handlers, row => row.MessageType == typeof(RoomCustomRulesChangeRequested));
        Assert.Empty(rig.Services.GetAll<IMatchCapabilityDirectory>());
    }

    /// <summary>A disabled deployment needs no custom workbook and publishes neither custom dispatch nor feedback.</summary>
    [Fact]
    public async Task DisabledPluginsDoNotMountCustomRules()
    {
        await using var rig = await Rig.CreateAsync(nativeBinding: false, clientData: false);
        await rig.ActivateInGameAsync(enabled: false);
        Assert.Empty(rig.Services.GetAll<IInboundMessageBinding<RoomCustomRulesChangeRequested>>());
        Assert.Empty(rig.Services.GetAll<IMessageSender<RoomCustomRulesReplied>>());
        Assert.DoesNotContain(rig.Messages.Handlers, row => row.MessageType == typeof(RoomCustomRulesChangeRequested));
    }

    private static Dictionary<string, object?> Map(object? value) => Assert.IsType<Dictionary<string, object?>>(value);

    private static RoomCustomRuleValue[] Rules(Dictionary<string, object?> game) =>
        Assert.IsType<object?[]>(game["unknown_328"]).Select(value =>
        {
            var row = Map(value);
            return new RoomCustomRuleValue((ushort)row["unknown_0"]!,
                Assert.IsType<object?[]>(row["unknown_4"]).Select(word => unchecked((uint)(int)word!)).ToArray());
        }).ToArray();

    private sealed class Rig : IAsyncDisposable
    {
        private static readonly string Root = FindRoot();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<PluginContext> _contexts = [];
        internal ServiceRegistry Services { get; } = new();
        internal PacketTables Tables { get; } = PacketTables.Load(Path.Combine(Root, "packets"));
        internal IOpcodeTable Table { get; private set; } = null!;
        internal PluginContext Dialect { get; private set; } = null!;
        internal PluginContext? InGame { get; private set; }
        internal IMessageRouter Messages => Services.Get<IMessageRouter>();
        internal RecordingSession Host { get; } = RoomFlowTests.Rig.Player(211, "host");
        internal RecordingSession Guest { get; } = RoomFlowTests.Rig.Player(212, "guest");
        internal PlayRoom Room => ((RoomRegistry)Services.GetAll<IMatchCapabilityDirectory>().Single()).TryGet(Host.Id)!;

        internal static async Task<Rig> CreateAsync(bool remap = false, bool nativeBinding = true, bool clientData = true)
        {
            var rig = new Rig();
            try
            {
                var root = rig.Context("root");
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new MessageRouterPlugin().ActivateAsync(root, default);
                await new OpcodeTableRegistryPlugin().ActivateAsync(root, default);
                var profile = new ClientProfile("kr-1256", "kr", 1256, 0, 0, "kr-1256");
                var full = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", "kr-1256", "opcodes.yaml"), profile);
                var table = remap ? new OpcodeTable(profile, full.All.Select(row => row with
                    { Value = checked((ushort)(row.Value + 1000)) })) : full;
                root.Effect(rig.Services.Get<IOpcodeTableRegistry>().Register(table));
                rig.Table = table.ForConnection(OpcodeConnection.Game);
                if (clientData) root.Effect(rig.Services.Register<IClientDataStore>(new RoomCustomRulesTests.WorkbookData()));
                root.Effect(rig._events.Intercept<EquipmentViewRequested, bool>((_, _, _) => ValueTask.FromResult(true)));
                rig.Dialect = rig.Context("dialect.rooms", new()
                {
                    ["protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay",
                    ["packets_directory"] = Path.Combine(Root, "packets"), ["modern_rooms"] = true,
                    ["waiting_rooms_only"] = true, ["custom_room_rules"] = nativeBinding,
                });
                rig.Dialect.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
                await new RoomsPlugin().ActivateAsync(rig.Dialect, default);
                return rig;
            }
            catch { await rig.DisposeAsync(); throw; }
        }

        internal async Task ActivateInGameAsync(bool enabled = true)
        {
            InGame = Context("ingame", new()
            {
                ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay",
                ["custom_room_rules"] = enabled, ["max_players_per_match"] = 4,
            });
            InGame.BindServices(typeof(InGamePlugin).GetCustomAttribute<PluginAttribute>()!);
            await new InGamePlugin().ActivateAsync(InGame, default);
        }

        internal ValueTask DispatchAsync(RecordingSession session, string name, byte[] body) =>
            Services.Get<IPacketRouter>().DispatchAsync("kr-1256/game", session, new(Table.C2S(name), body), default);

        internal byte[] CreateBody(int mode) => PacketCodec.Write(Tables, "kr-1256", "RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "room", ["game_setting"] = new Dictionary<string, object?> { ["mode"] = mode, ["unknown_24"] = 987654 },
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        });

        internal byte[] JoinBody() => PacketCodec.Write(Tables, "kr-1256", "RoomJoinRequest", new Dictionary<string, object?>
        {
            ["room_id"] = Room.Id, ["password"] = "",
        });

        private PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow
            {
                Id = id, Plugin = id, Source = "test", ClientBuild = new("kr", 1256, 0), Config = config,
            }, Services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
            throw new InvalidOperationException("Repository root not found.");
        }
    }
}
