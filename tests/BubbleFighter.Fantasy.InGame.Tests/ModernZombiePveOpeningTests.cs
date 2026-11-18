using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks cooperative resource admission, both occupied seat records and the regional transition to play.</summary>
public sealed class ModernZombiePveOpeningTests
{
    private const string Circle = "rel_zombie03_Circle";
    private const string Explicit = "explicit_only";
    private const string AlternateStage = "stage_directory";

    /// <summary>A listed stage must have a PvE file and enough distinct, unambiguous, in-bounds human starts.</summary>
    [Theory]
    [InlineData("no-ini", false)]
    [InlineData("empty-ini", false)]
    [InlineData("no-world", false)]
    [InlineData("red-only", false)]
    [InlineData("no-second", true)]
    [InlineData("duplicate-name", true)]
    [InlineData("duplicate-position", true)]
    [InlineData("out-of-bounds", true)]
    public void MissingResourcesCannotEnterThroughConcreteOrRandomSelection(string fault, bool supportsOne)
    {
        var maps = Catalog(Circle, fault);
        foreach (var selection in new[] { Circle, "Random_Hot", "Random" })
        {
            Assert.Null(maps.ZombiePveMap(Circle, selection, 2, new Random(1)));
            Assert.Null(maps.ZombiePveMap(selection, selection, 2, new Random(1)));
            Assert.Equal(supportsOne ? Circle : null, maps.ZombiePveMap(selection, selection, 1, new Random(1)));
        }
    }

    /// <summary>A nonfinite helper transform fails data preflight instead of entering a draw.</summary>
    [Fact]
    public void NonfiniteAnchorFailsPreflight() =>
        Assert.Throws<InvalidDataException>(() => Catalog(Circle, "nonfinite"));

    /// <summary>An explicit released map can use its own directory without becoming an ordinary or hot fallback.</summary>
    [Fact]
    public void ExplicitOnlyKeyDoesNotLeakIntoEitherRandomPool()
    {
        const string hot = "rel_zombie05_Tear";
        var maps = Catalog(hot);
        Assert.Equal(Explicit, maps.ZombiePveMap(Explicit, "", 2, new Random(1)));
        Assert.Equal(AlternateStage, maps.StageKey(Explicit));
        Assert.Equal(Circle, maps.ZombiePveMap(Explicit, "Random", 2, new Random(1)));
        Assert.Equal(hot, maps.ZombiePveMap(Explicit, "Random_Hot", 2, new Random(1)));
        Assert.Null(maps.ZombiePveMap(AlternateStage, "", 2, new Random(1)));
        Assert.Null(maps.ZombiePveMap("unreleased", "", 2, new Random(1)));
        Assert.Null(maps.ZombiePveMap(Explicit, "", 0, new Random(1)));
    }

    /// <summary>Each regional binding sends both humans before the stage; both must load before countdown and play.</summary>
    [Theory]
    [InlineData("kr-1256", "rel_zombie05_Tear", 521, 607, 608, 8)]
    [InlineData("cn-774", "rel_zombie02_Twobridge", 522, 601, 602, 8)]
    [InlineData("tw-454", "rel_zombie05_Tear", 522, 601, 602, 12)]
    public async Task MasterAndMemberReceiveTheSameCooperativeOpening(string build, string hot, int signature,
        int countdown, int play, int playBytes)
    {
        var options = new InGameOptions { ServerMapDraw = true, ZombiePveOpening = true, StartRequiresTeamBalance = true,
            CountedItemModeId = 16, CountedItemKinds = [229, 146, 136], GameProtocol = build + "/game",
            RelayProtocol = build + "/relay", NativeMatchRelay = true, MatchTransportFallback = true };
        var registry = new RoomRegistry(options);
        var profile = TestWireDialects.Get(build).Profile;
        var master = new RecordingSession(11, profile);
        var member = new RecordingSession(22, profile);
        master.SetLobbyPlayer(11, "master", level: 3);
        member.SetLobbyPlayer(22, "member", level: 3);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        PluginContext Context(string id) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "test" }, services, events, NullLogger.Instance);
        var router = Context("router");
        var messages = Context("messages");
        var binding = Context("binding");
        using var lifetime = new CancellationTokenSource();
        try
        {
            await new PacketRouterPlugin().ActivateAsync(router, default);
            await new MessageRouterPlugin().ActivateAsync(messages, default);
            var root = RepositoryRoot();
            var tables = PacketTables.Load(Path.Combine(root, "packets"));
            var opcodes = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
            var wire = new RoundStartBinding(build + "/game", opcodes, tables, build, [1, 0, 1], NullLogger.Instance);
            wire.Mount(binding);
            var items = new CountedItemsBinding(build + "/game", opcodes, tables, build);
            var flow = new RoomFlow(registry, new(new RoomFlowTests.Sink<RoomEntered>(), new RoomFlowTests.Sink<RoomSeatUpdated>(),
                new RoomFlowTests.Sink<RoomSeatVacated>(), new RoomFlowTests.Sink<RoomMasterChanged>(),
                new RoomFlowTests.Sink<RoomJoinRefused>(), MatchStarting: wire, CountedItemsPrepared: items),
                NullLogger.Instance, options: options, lifetime: lifetime.Token);
            await flow.HandleCreateAsync(master, new("room", "", 16, Circle), default);
            var room = registry.TryGet(master.Id)!;
            await flow.HandleJoinAsync(member, new(room.Id, ""), default);
            room.FindByGame(master.Id)!.Team = room.FindByGame(member.Id)!.Team = 1;
            var request = new RoomStartRequested(hot) { MapSelection = "Random_Hot", MapName = Explicit, TimeLimits = new(180000, 180000) };
            flow.Maps = Catalog(hot);
            await flow.HandleStartAsync(master, request, default);
            await flow.HandleReadyAsync(member, new(true), default);
            await flow.HandleStartAsync(member, request with { Force = true }, default);
            // Even a valid display-string key is not a resource fallback for a broken selected stage.
            flow.Maps = Catalog(hot, "no-second");
            await flow.HandleStartAsync(master, request, default);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Empty(master.Opcodes);
            Assert.Empty(member.Opcodes);
            Assert.All(room.Seats, seat => Assert.Equal(1, seat.Team));
            flow.Maps = Catalog(hot);
            await flow.HandleStartAsync(master, request, default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.Equal(0, room.MasterSlot);
            Assert.Equal(hot, room.RoundMapKey);
            Assert.Equal(hot, room.RoundStageKey);
            foreach (var recipient in new[] { master, member })
            {
                Assert.Equal(new ushort[] { 927, 503, 503, (ushort)signature, 510 }, recipient.Opcodes);
                Assert.Equal(52, recipient.Body(927).Length);
                var bodies = recipient.Bodies(503);
                for (var slot = 0; slot < 2; slot++)
                {
                    var record = PacketCodec.Read(tables, build, "RoomSeatUpdate", bodies[slot]);
                    var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(record["user"]);
                    Assert.Equal(slot, Convert.ToInt32(record["slot"]));
                    Assert.True(Convert.ToBoolean(user["occupied"]));
                    Assert.Equal(slot == 0 ? 11 : 22, Convert.ToInt32(user["user_number"]));
                    Assert.Equal(2, Convert.ToInt32(user["team"]));
                }
                Assert.Equal(new byte[] { 16, 0, 0, 0, 0, 0, 0 }, recipient.Body((ushort)signature));
                var entry = recipient.Body(510);
                Assert.Equal(39 + 6 * hot.Length, entry.Length);
                var fields = PacketCodec.Read(tables, build, "RoomEnterGame", entry);
                Assert.Equal(hot, fields["map_key"]);
                Assert.Equal(hot, fields["stage_key"]);
                Assert.Equal(hot, fields["map_selection"]);
                Assert.Equal(3, Convert.ToInt32(fields["room_state"]));
            }

            var offers = new ProbeOffers(options.GameProtocol, PacketCodec.Write(tables, build, "RelaySessionKey", new Dictionary<string, object?>()).Length);
            var round = new RoundFlow(registry, options, new(null, wire, wire, ObjectAdded: wire, CountedItemUsed: items),
                NullLogger.Instance, lifetime.Token, flow.ReturnToRoomAsync, capabilityOffers: offers)
                { CountdownDelay = static (_, _) => Task.CompletedTask };
            await round.HandleObjectAddRequestedAsync(member, new(1000000), default);
            Assert.DoesNotContain((ushort)countdown, member.Opcodes);
            await round.HandleObjectAddRequestedAsync(master, new(1000000), default);
            Assert.DoesNotContain((ushort)countdown, member.Opcodes);
            await round.HandleObjectAddRequestedAsync(member, new(1100000), default);
            await round.HandleObjectAddRequestedAsync(member, new(1100000), default);
            foreach (var recipient in new[] { master, member })
                Assert.Equal(new byte[4], Assert.Single(recipient.Bodies((ushort)countdown)));
            await round.HandleLoadReportedAsync(master, new(), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.DoesNotContain((ushort)585, master.Opcodes);
            Assert.DoesNotContain((ushort)585, member.Opcodes);
            await round.HandleLoadReportedAsync(member, new(), default);
            Assert.Equal(new long[] { master.Id, member.Id }, offers.Recipients);
            await round.HandleMatchConnectionReportedAsync(master, new(false), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            Assert.DoesNotContain((ushort)585, master.Opcodes);
            Assert.DoesNotContain((ushort)585, member.Opcodes);
            await round.HandleMatchConnectionReportedAsync(member, new(false), default);
            await room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            foreach (var recipient in new[] { master, member })
            {
                Assert.Equal(new ushort[] { 585, (ushort)countdown, (ushort)play }, recipient.Opcodes.Skip(6));
                Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, recipient.Body(585));
                Assert.Equal(4, recipient.Body((ushort)countdown).Length);
                Assert.Equal(playBytes, recipient.Body((ushort)play).Length);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await registry.StopAllAsync();
            await binding.UnwindAsync();
            await messages.UnwindAsync();
            await router.UnwindAsync();
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static MapCatalog Catalog(string hot, string fault = "")
    {
        string[] keys = [.. new[] { Circle, hot, Explicit, "unreleased" }.Distinct()];
        string DirectoryFor(string key) => key == Explicit ? AlternateStage : key;
        string Row(string field, IEnumerable<string> cells) => "<Row>" + string.Concat(
            new[] { field, "description" }.Concat(cells).Select(s => $"<Cell><Data>{s}</Data></Cell>")) + "</Row>";
        var xml = "<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"><Worksheet><Table>"
            + Row("[Field]", keys) + Row("Directory", keys.Select(DirectoryFor))
            + Row("isRelease", keys.Select(key => key == "unreleased" ? "0" : "1"))
            + Row("isZombiePveModeMap", keys.Select(key => key == Circle ? "1" : "0")) + "</Table></Worksheet></Workbook>";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [MapMetaTable.Path] = Encoding.UTF8.GetBytes(xml),
            [MapCatalog.HotRandomListPath] = Encoding.Unicode.GetBytes("Mode\tMapKey\tWeight\tUsable\nwgtZombiePVE\t" + hot + "\t1\t사용함\n"),
            [MapCatalog.DifficultyListPath] = Encoding.Unicode.GetBytes("Mode\tDifficulty\tMapKey\tUsable\n"),
        };
        foreach (var key in keys)
        {
            var stage = DirectoryFor(key);
            var defect = key == hot ? fault : "";
            if (defect != "no-ini") files[$"Maps/{stage}/{stage}_zombiePVE.ini"] = defect == "empty-ini" ? [] : "[Stage]\n"u8.ToArray();
            List<(string Name, Vector3 Position)> nodes = [("#mapMinPoint", Vector3.Zero), ("#mapMaxPoint", new(10, 10, 10)), ("#spawn_T11", new(1, 3, 3))];
            if (defect != "red-only") nodes.Add(("#spawn_T21", new(1, 2, 3)));
            if (defect != "no-second") nodes.Add(("#spawn_T22", defect switch
            {
                "nonfinite" => new(float.NaN, 5, 6), "duplicate-position" => new(1, 2, 3),
                "out-of-bounds" => new(4, 5, 11), _ => new(4, 5, 6),
            }));
            if (defect == "duplicate-name") nodes.Add(("#spawn_T22", new(2, 3, 4)));
            if (defect != "no-world") files[$"Maps/{stage}/track.wb"] = WorldBlockFixture.Create([.. nodes]);
        }
        return MapCatalog.Load(new Store(files), (8 * 1024 * 1024, 4096));
    }

    private sealed class Store(Dictionary<string, byte[]> files) : IClientDataStore
    {
        public bool Exists(string path) => files.ContainsKey(path);
        public byte[] Read(string path) => files[path];
    }

    private sealed class ProbeOffers(string protocol, int ticketLength) : IMatchCapabilityOfferSender
    {
        public string Protocol => protocol;
        public int TicketLength => ticketLength;
        internal List<long> Recipients { get; } = [];
        public ValueTask SendAsync(ISession session, MatchCapabilityOffered message, CancellationToken ct)
        {
            Recipients.Add(session.Id);
            return ValueTask.CompletedTask;
        }
    }
}
