using System.Buffers;
using System.Reflection;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Runs numbered tutorial admission, loading, play and exit through the regional room and game plugins.</summary>
public sealed class StageMainTutorialTests
{
    private const string Map = "sin_mission_wall_lv_03", DirectoryKey = "rel_mission_wall";

    /// <summary>A valid wire selection starts the exact stage, plays and returns to menu without completing first-run onboarding.</summary>
    [Theory]
    [InlineData("kr-1256", "unknown_140", "unknown_92", 0x10)]
    [InlineData("cn-774", "unknown_172", "unknown_124", 0)]
    [InlineData("tw-454", "unknown_156", "unknown_108", 0)]
    public async Task StageMainTutorialRequestPlaysSelectedSceneAndReturnsToMenu(string build, string difficultyField, string sceneField, int flags)
    {
        var packetsDirectory = (string)TestWireDialects.ModernRoomsConfig()["packets_directory"]!;
        var tables = PacketTables.Load(packetsDirectory);
        var profile = TestWireDialects.Get(build).Profile;
        var table = OpcodeTableLoader.Load(Path.Combine(packetsDirectory, "..", "profiles", build, "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var protocol = build + "/game";
        PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "test",
                ClientBuild = new(profile.Region, profile.ClientVersionMajor, profile.ResourceVersion),
                Lineage = tables.LineageOf(build).ToArray(), Config = config }, services, events, NullLogger.Instance);
        var context = Context("root");
        var dialect = Context("dialect.rooms", TestWireDialects.ModernRoomsConfig(new()
        {
            ["protocol"] = protocol, ["relay_protocol"] = build + "/relay", ["initial_tutorial"] = false, ["zombie_escape"] = false,
        }));
        var ingame = Context("ingame", new() { ["game_protocol"] = protocol, ["relay_protocol"] = build + "/relay",
            ["start_matches"] = true, ["start_timer_ms"] = 5, ["max_players_per_match"] = 4, ["plaza_games"] = false });
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            await new OpcodeTableRegistryPlugin().ActivateAsync(context, default);
            context.Effect(services.Get<IOpcodeTableRegistry>().Register(table));
            context.Effect(services.Register<IClientDataStore>(new MapStore()));
            context.Effect(events.Intercept<EquipmentViewRequested, bool>((_, _, _) => ValueTask.FromResult(true)));
            dialect.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
            await new RoomsPlugin().ActivateAsync(dialect, default);
            ingame.BindServices(typeof(InGamePlugin).GetCustomAttribute<PluginAttribute>()!);
            await new InGamePlugin().ActivateAsync(ingame, default);
            Assert.True(tables.CapabilityOf(build, "stage_main_tutorial"));
            Assert.Equal((ushort)107, table.C2S("gcpReqPlayTutorial"));
            var registry = Assert.IsType<RoomRegistry>(Assert.Single(services.GetAll<IMatchCapabilityDirectory>()));
            var session = new RecordingSession(7, profile);
            var players = new MemoryPlayerRepository(new MemoryPlayerTable(2));
            var record = await players.CreateCharacterAsync("stage-main", "WallPlayer", StarterKit.Default, default);
            Assert.NotNull(record);
            var player = session.SetLobbyPlayer(record.UserNum);
            context.Effect(services.Get<IMessageRouter>().For(context).On<InitialTutorialCompleted>(protocol,
                (_, _, ct) => players.CompleteTutorialAsync(record.UserNum, ct)));
            Assert.True(await players.NeedsTutorialAsync(record.UserNum, default));
            var router = services.Get<IPacketRouter>();
            foreach (var invalid in new[]
            {
                Request("sin_mission_missing_lv_03", DirectoryKey), Request(Map, Map), Request(Map, DirectoryKey, clock: 1),
                Request(Map, DirectoryKey, difficulty: 1), Request("rel_Village07_Easy02", "rel_Village07_Easy02", bits: 16),
            })
                await router.DispatchAsync(protocol, session, new(107, invalid), default);
            Assert.Null(registry.TryGet(session.Id));
            Assert.Empty(session.Opcodes);
            Assert.Equal(SessionPhase.MainMenu, player.Phase);

            var body = Request(Map, DirectoryKey, bits: (byte)flags);
            Assert.Equal(69 + 2 * (DirectoryKey.Length + "Wall".Length + Map.Length), body.Length);
            await router.DispatchAsync(protocol, session, new(107, body), default);
            Assert.Equal(SessionPhase.Match, player.Phase);
            var room = registry.TryGet(session.Id)!;
            var solo = room.Tutorial!;
            Assert.True(solo.GameConnectionOnly);
            Assert.Equal(15, solo.ModeId);
            Assert.Equal(50_000, solo.TimeLimitMs);
            Assert.Equal(new[] { table.S2C("gspGotoRoom"), table.S2C("gspRoomSetSlot"), table.S2C("gspSessionSignatureData"),
                table.S2C("gspRoomEnterGame") }, session.Opcodes);
            var install = PacketCodec.Read(tables, build, "gspGotoRoom", session.Body(table.S2C("gspGotoRoom")));
            var setting = Assert.IsType<Dictionary<string, object?>>(install["session_setting"]);
            var game = Assert.IsType<Dictionary<string, object?>>(setting["game_setting"]);
            Assert.Equal(15, RoomProjectionFields.Load(tables, build).ReadMode(game));
            Assert.Equal(3, game[difficultyField]);
            Assert.Equal(DirectoryKey, game[sceneField]);
            var enter = PacketCodec.Read(tables, build, "RoomEnterGame", session.Body(table.S2C("gspRoomEnterGame")));
            Assert.Equal(Map, enter["map_key"]);
            Assert.Equal(Map, enter["map_selection"]);
            Assert.Equal(DirectoryKey, enter["stage_key"]);

            Assert.Equal(RoomPhase.Starting, room.Phase);
            await router.DispatchAsync(protocol, session, new(table.C2S("gcpReqAddObject"),
                PacketCodec.Write(tables, build, "RoundObjectAddRequest", new Dictionary<string, object?> { ["object_id"] = 1_000_000 })), default);
            Assert.True(Assert.Single(room.Seats).ObjectAdded);
            await router.DispatchAsync(protocol, session, new(table.C2S("gcpGameReportMapLoadingComplete"), new byte[16]), default);
            await room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.True(Assert.Single(room.Seats).SceneReady);
            var loaded = PacketCodec.ReadScalars(tables, build, "RoundEverybodyLoadingComplete", session.Body(table.S2C("gspGameEverybodyLoadingComplete")));
            Assert.Equal(false, loaded["unknown_4"]);
            Assert.Equal(false, loaded["unknown_5"]);
            Assert.Equal(true, loaded["unknown_6"]);
            Assert.Equal(5, PacketCodec.ReadScalars(tables, build, "RoundCountdown", session.Body(table.S2C("gspGameCountDown")))["milliseconds"]);
            Assert.Equal(50_000, PacketCodec.ReadScalars(tables, build, "RoundStartGame", session.Body(table.S2C("gspGameStartGame")))["unknown_4"]);

            Assert.Equal((ushort)624, table.C2S("gcpReqEndStage"));
            await router.DispatchAsync(protocol, session, new(624, PacketCodec.Write(tables, build, "ScriptStageResult",
                new Dictionary<string, object?> { ["outcome"] = 1 })), default);
            Assert.Null(registry.TryGet(session.Id));
            Assert.Equal(SessionPhase.MainMenu, player.Phase);
            Assert.Equal(2, session.Bodies(table.S2C("gspGotoRoom")).Length);
            Assert.True(await players.NeedsTutorialAsync(record.UserNum, default));

            // Re-entry after a completed stage also allows leaving before loading the next one.
            await router.DispatchAsync(protocol, session, new(107, body), default);
            Assert.Equal(RoomPhase.Starting, registry.TryGet(session.Id)!.Phase);
            await router.DispatchAsync(protocol, session, new(table.C2S("gcpReqGotoMenu"), Array.Empty<byte>()), default);
            Assert.Null(registry.TryGet(session.Id));
            Assert.Equal(SessionPhase.MainMenu, player.Phase);
            Assert.True(await players.NeedsTutorialAsync(record.UserNum, default));
        }
        finally
        {
            await ingame.UnwindAsync();
            await dialect.UnwindAsync();
            await context.UnwindAsync();
        }
    }

    private sealed class MapStore : IClientDataStore
    {
        public bool Exists(string path) => path == MapMetaTable.Path;
        public byte[] Read(string path) => Exists(path) ? Workbook() : throw new FileNotFoundException(path);
    }

    private static byte[] Request(string key, string directory, int difficulty = 3, int clock = 50_000, byte bits = 0)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(output);
        for (var i = 0; i < 13; i++) writer.WriteInt32(i == 8 ? clock : 0);
        writer.WriteByte(bits);
        writer.WriteWideStringPrefixed(directory);
        writer.WriteWideStringPrefixed("Wall");
        writer.WriteWideStringPrefixed(key);
        writer.WriteInt32(difficulty);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Workbook()
    {
        static string Row(string key, string value) =>
            $"<Row><Cell><Data>{key}</Data></Cell><Cell/><Cell><Data>{value}</Data></Cell></Row>";
        return Encoding.UTF8.GetBytes("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet><Table>"
            + Row("[Field]", Map) + Row("Directory", DirectoryKey) + Row("stageNum", "26") + Row("isSinglePlayTutorial", "1")
            + Row("Difficulty", "3") + Row("TimeAttackTimeLimit", "50") + "</Table></Worksheet></Workbook>");
    }
}
