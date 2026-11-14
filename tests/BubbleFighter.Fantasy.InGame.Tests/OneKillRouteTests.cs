using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins the native OneKill request and confirmation independently of room admission.</summary>
public sealed class OneKillRouteTests
{
    /// <summary>The two twenty-byte frames keep different actor orders and cannot dispatch a separate life event.</summary>
    [Theory]
    [InlineData(RoundOneKillKind.Request, 33272, "40420F00E0C810000000A03F000020C000007040")]
    [InlineData(RoundOneKillKind.Confirmation, 33273, "E0C810000000A03F000020C00000704040420F00")]
    public void NativeOneKillFramesKeepTheirOwnFieldOrder(RoundOneKillKind kind, int opcode, string hex)
    {
        var (packets, game, routes) = Load("kr-1256");
        var route = Assert.Single(routes, candidate => candidate.OneKill == kind);
        var body = Convert.FromHexString(hex);
        Assert.Equal(20, body.Length);
        Assert.Equal(opcode, route.GameInbound);
        Assert.Equal(opcode, route.RelayInbound);
        Assert.Equal(opcode, route.Outbound);
        Assert.Equal(opcode, game.S2C(Name(kind)));
        var decoded = route.Read(packets, "kr-1256", body);
        Assert.Equal(new(kind, 1000000, 1100000), decoded.Feed.OneKill);
        Assert.Equal(new[] { kind == RoundOneKillKind.Request ? 1000000 : 1100000 }, decoded.Feed.ReportedActors);
        Assert.False(decoded.Feed.IncludeSender);
        Assert.Null(decoded.Life);
        Assert.Equal(body, decoded.Feed.Payload.ToArray());
        foreach (var length in Enumerable.Range(0, body.Length))
            Assert.Throws<PacketFormatException>(() => route.Read(packets, "kr-1256", body[..length]));
        Assert.Throws<PacketFormatException>(() => route.Read(packets, "kr-1256", body.Concat(new byte[] { 0 }).ToArray()));
    }

    /// <summary>Every position or direction coordinate rejects NaN and infinities before a feed can be published.</summary>
    [Theory]
    [InlineData(RoundOneKillKind.Request, 0)]
    [InlineData(RoundOneKillKind.Request, 1)]
    [InlineData(RoundOneKillKind.Request, 2)]
    [InlineData(RoundOneKillKind.Confirmation, 0)]
    [InlineData(RoundOneKillKind.Confirmation, 1)]
    [InlineData(RoundOneKillKind.Confirmation, 2)]
    public void OneKillVectorsRejectNonfiniteCoordinates(RoundOneKillKind kind, int coordinate)
    {
        var (packets, _, routes) = Load("kr-1256");
        var route = Assert.Single(routes, candidate => candidate.OneKill == kind);
        var body = ReadFeed(kind, 1000000, 1100000).Payload.ToArray();
        var offset = (kind == RoundOneKillKind.Request ? 8 : 4) + coordinate * 4;
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(offset), invalid);
            Assert.Throws<PacketFormatException>(() => route.Read(packets, "kr-1256", body));
        }
    }

    /// <summary>Both native inputs publish only the admission message, and removing the binding removes both handlers.</summary>
    [Theory]
    [InlineData(RoundOneKillKind.Request)]
    [InlineData(RoundOneKillKind.Confirmation)]
    public async Task OneKillBindingsCannotBypassAdmissionThroughGenericLife(RoundOneKillKind kind)
    {
        var (packets, game, _) = Load("kr-1256");
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("binding");
        var feeds = new List<RoundFeedSent>();
        var lives = new List<RoundLifeReported>();
        var session = new RecordingSession(7, game.Profile);
        var feed = ReadFeed(kind, 1000000, 1100000);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            var messages = services.Get<IMessageRouter>().For(infrastructure);
            infrastructure.Effect(messages.On<RoundFeedSent>("kr-1256/game", (_, message, _) =>
            {
                feeds.Add(message);
                return ValueTask.CompletedTask;
            }));
            infrastructure.Effect(messages.On<RoundLifeReported>("kr-1256/game", (_, message, _) =>
            {
                lives.Add(message);
                return ValueTask.CompletedTask;
            }));
            new RoundFeedBinding("kr-1256/game", "kr-1256/relay", game,
                game.ForConnection(OpcodeConnection.Relay), packets, "kr-1256").Mount(binding);
            var router = services.Get<IPacketRouter>();
            foreach (var protocol in new[] { "kr-1256/game", "kr-1256/relay" })
                await router.DispatchAsync(protocol, session, new(feed.Code, feed.Payload), default);
            Assert.Equal(2, feeds.Count);
            Assert.All(feeds, message =>
            {
                Assert.Equal(feed.OneKill, message.OneKill);
                Assert.False(message.IncludeSender);
                Assert.Equal(feed.Payload.ToArray(), message.Payload.ToArray());
            });
            Assert.Empty(lives);
            await binding.UnwindAsync();
            foreach (var protocol in new[] { "kr-1256/game", "kr-1256/relay" })
                await router.DispatchAsync(protocol, session, new(feed.Code, feed.Payload), default);
            Assert.Equal(2, feeds.Count);
            Assert.Empty(lives);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = "test", Source = "test" },
            services, events, NullLogger.Instance);
    }

    /// <summary>Unaudited descendants cannot inherit either Korean OneKill route.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OneKillRoutesRequireTheAuditedBuild(string build)
    {
        var (packets, _, routes) = Load(build);
        Assert.False(packets.CapabilityOf(build, "native_one_kill"));
        Assert.DoesNotContain(routes, route => route.OneKill is not null);
    }

    /// <summary>Invalid metadata cannot turn OneKill into a generic life route or change its native ownership and echo policy.</summary>
    [Theory]
    [InlineData("kind")]
    [InlineData("capability")]
    [InlineData("echo")]
    [InlineData("actors")]
    [InlineData("life")]
    [InlineData("nested")]
    [InlineData("width")]
    [InlineData("order")]
    public void InvalidOneKillRoutesFailBeforeRegistration(string fault)
    {
        var directory = Directory.CreateTempSubdirectory("one-kill-routes-");
        try
        {
            var route = "opcode: gppOneKillRequest_, structure: OneKillRequest, actors: [attacker], one_kill: request";
            route = fault switch
            {
                "kind" => route.Replace("one_kill: request", "one_kill: other", StringComparison.Ordinal),
                "actors" => route.Replace("actors: [attacker]", "actors: [attacker, victim]", StringComparison.Ordinal),
                "life" => route + ", life_event: died, life_actor: victim",
                "echo" => route + ", echo_sender: true",
                "nested" => route + ", nested: true",
                _ => route,
            };
            File.WriteAllText(Path.Combine(directory.FullName, "builds.yaml"),
                "version: 1\nbuilds:\n  kr-1256:\n    capabilities: { native_one_kill: "
                + (fault == "capability" ? "false" : "true") + " }\n    round_feed:\n      Request: { " + route + " }\n");
            var fields = "    - attacker: u32\n    - victim: u32\n    - position_x: f32\n    - position_y: f32\n    - position_z: f32\n";
            if (fault == "width") fields = fields.Replace("position_z: f32", "position_z: u16", StringComparison.Ordinal);
            if (fault == "order") fields = fields.Replace("attacker: u32\n    - victim: u32", "victim: u32\n    - attacker: u32", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory.FullName, "one-kill.yaml"), "version: 1\nstructs:\n  OneKillRequest:\n" + fields);
            var packets = PacketTables.Load(directory.FullName);
            var (_, game, _) = Load("kr-1256");
            Assert.Throws<ConfigException>(() => RoundFeedRoute.Load(packets, "kr-1256", game,
                game.ForConnection(OpcodeConnection.Relay)));
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static RoundFeedSent ReadFeed(RoundOneKillKind kind, int attacker, int victim)
    {
        var (packets, _, routes) = Load("kr-1256");
        var route = routes.Single(candidate => candidate.OneKill == kind);
        var body = new byte[20];
        var confirmed = kind == RoundOneKillKind.Confirmation;
        BinaryPrimitives.WriteInt32LittleEndian(body, confirmed ? victim : attacker);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(confirmed ? 16 : 4), confirmed ? attacker : victim);
        var vector = confirmed ? 4 : 8;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(vector), 1.25f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(vector + 4), -2.5f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(vector + 8), 3.75f);
        var decoded = route.Read(packets, "kr-1256", body);
        Assert.Null(decoded.Life);
        return decoded.Feed;
    }

    private static string Name(RoundOneKillKind kind) => kind == RoundOneKillKind.Request
        ? "gppOneKillRequest_" : "gppOneKillConfirmation_";

    private static (PacketTables Packets, IOpcodeTable Game, RoundFeedRoute[] Routes) Load(string build)
    {
        var packets = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        return (packets, game, RoundFeedRoute.Load(packets, build, game, game.ForConnection(OpcodeConnection.Relay)));
    }
}
