using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins native route selection independently of Korean numbers, reporting offsets and body widths.</summary>
public sealed class NativeRoundFeedRouteTests
{
    /// <summary>The deployed profile data must resolve every inherited action in all three opcode spaces.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeProfileResolvesItsAuditedActionSet(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"),
            TestWireDialects.Get(build).Profile);
        var relay = game.ForConnection(OpcodeConnection.Relay);
        var routes = RoundFeedRoute.Load(tables, build, game, relay);
        Assert.Equal(39, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.Equal(route.GameInbound, route.RelayInbound);
            Assert.Equal(route.GameInbound, route.Outbound);
        });
        Assert.Equal("RoundRescueReleaseNative", routes.Single(route => route.GameInbound == (build == "tw-454" ? 33282 : 38273)).Structure);
        Assert.Equal("RoundRescuePairNative", routes.Single(route => route.GameInbound == (build == "tw-454" ? 33281 : 38280)).Structure);
        Assert.Equal("RoundDamagerContactNative", routes.Single(route => route.GameInbound == 33301).Structure);
        foreach (var name in new[] { "gppSplashDamager", "gppMobDropBombOnDead", "gppDropHumilPrevItem",
            "gppFireBullet3", "gppStormDamager", "gppSplashDamagerByDamager", "gppStormDamagerItemId", "gppNearDamager" })
        {
            Assert.Equal(game.C2S(name), relay.C2S(name));
            Assert.Equal(game.S2C(name), relay.S2C(name));
        }
        if (build == "tw-454")
        {
            Assert.DoesNotContain(routes, route => route.GameInbound >= 38271);
            Assert.DoesNotContain(routes, route => route.GameInbound == 32776);
            Assert.Equal("Unknown32796", routes.Single(route => route.GameInbound == 32796).Structure);
            foreach (var (code, structure) in new[] { (32794, "Unknown38271"), (32806, "Unknown38272"),
                (32805, "Unknown38274"), (32795, "Unknown38277"), (32789, "Unknown38281"),
                (32787, "Unknown38282"), (33274, "Unknown38284") })
                Assert.Equal(structure, routes.Single(route => route.GameInbound == code).Structure);
        }
    }

    /// <summary>Actual native profiles preserve combat bytes, ownership and shared life events on both listeners.</summary>
    [Theory]
    [InlineData("cn-774", 33301, "785634120000A03F000020C000007040EFBEADDEE0C81000", null, null, null, false)]
    [InlineData("cn-774", 32925, "40420F00A5010002000300040005000600EFBEADDEE778563412EFCDAB89BC9A", new[] { 1000000 }, null, null, false)]
    [InlineData("cn-774", 38273, "40420F00E0C81000EFBEADDEE7", new[] { 1000000, 1100000 }, RoundLifeEvent.Released, null, true)]
    [InlineData("cn-774", 38280, "E0C8100040420F00E7", new[] { 1100000, 1000000 }, null, null, true)]
    [InlineData("cn-774", 38282, "40420F00E0C810000000A03F000020C000007040EFBEADDE00000000010000007856341200000000", new[] { 1000000 }, RoundLifeEvent.Bubbled, null, true)]
    [InlineData("cn-774", 38282, "40420F00E0C810000000A03F000020C000007040EFBEADDE01000000010000007856341200000000", new[] { 1000000 }, RoundLifeEvent.Died, null, true)]
    [InlineData("cn-774", 38277, "0100000040420F00E0C810000000A03F000020C00000704000000000EFBEADDE", new[] { 1100000, 1000000 }, RoundLifeEvent.Finished, 1100000, true)]
    [InlineData("cn-774", 38277, "0000000040420F00E0C810000000A03F000020C00000704000000000EFBEADDE", new[] { 1100000, 1000000 }, null, null, true)]
    [InlineData("tw-454", 33301, "785634120000A03F000020C000007040E0C81000", null, null, null, false)]
    [InlineData("tw-454", 32925, "40420F00A5010002000300040005000600EFBEADDEE778563412EFCDAB89BC9A", new[] { 1000000 }, null, null, false)]
    [InlineData("tw-454", 33282, "40420F00E0C81000EFBEADDEE7", new[] { 1000000, 1100000 }, RoundLifeEvent.Released, null, true)]
    [InlineData("tw-454", 33281, "E0C8100040420F00E7", new[] { 1100000, 1000000 }, null, null, true)]
    [InlineData("tw-454", 32787, "40420F00E0C810000000A03F000020C000007040EFBEADDE00000000010000007856341200000000", new[] { 1000000 }, RoundLifeEvent.Bubbled, null, true)]
    [InlineData("tw-454", 32787, "40420F00E0C810000000A03F000020C000007040EFBEADDE01000000010000007856341200000000", new[] { 1000000 }, RoundLifeEvent.Died, null, true)]
    [InlineData("tw-454", 32795, "0100000040420F00E0C810000000A03F000020C00000704000000000EFBEADDE", new[] { 1100000, 1000000 }, RoundLifeEvent.Finished, 1100000, true)]
    [InlineData("tw-454", 32795, "0000000040420F00E0C810000000A03F000020C00000704000000000EFBEADDE", new[] { 1100000, 1000000 }, null, null, true)]
    [InlineData("tw-454", 32796, "40420F00E0C810000000A03F000020C000007040EFBEADDE01000000", new[] { 1000000, 1100000 }, null, null, true)]
    [InlineData("tw-454", 32806, "40420F00FFFFFFFF", new[] { 1000000 }, RoundLifeEvent.Released, null, true)]
    [InlineData("tw-454", 32805, "40420F00", new[] { 1000000 }, RoundLifeEvent.BubbleExpired, null, true)]
    [InlineData("tw-454", 33274, "40420F0001000000", new[] { 1000000 }, null, null, false)]
    public async Task NativeCombatUsesTheActualProfile(string build, int code, string hex, int[]? actors,
        RoundLifeEvent? kind, int? initiator, bool echo)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"),
            TestWireDialects.Get(build).Profile);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        var feed = new List<RoundFeedSent>();
        var life = new List<RoundLifeReported>();
        var body = Convert.FromHexString(hex);
        var session = new RecordingSession(7, game.Profile);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            var messages = services.Get<IMessageRouter>().For(infrastructure);
            infrastructure.Effect(messages.On<RoundFeedSent>(build + "/game", (_, message, _) =>
            {
                feed.Add(message);
                return ValueTask.CompletedTask;
            }));
            infrastructure.Effect(messages.On<RoundLifeReported>(build + "/game", (_, message, _) =>
            {
                life.Add(message);
                return ValueTask.CompletedTask;
            }));
            new RoundFeedBinding(build + "/game", build + "/relay", game,
                game.ForConnection(OpcodeConnection.Relay), tables, build).Mount(binding);
            foreach (var protocol in new[] { build + "/game", build + "/relay" })
            {
                var count = feed.Count;
                var lifeCount = life.Count;
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new((ushort)code, body), default);
                Assert.Equal(count + 1, feed.Count);
                Assert.Equal(code, feed[^1].Code);
                Assert.Equal(body, feed[^1].Payload.ToArray());
                Assert.Equal(actors, feed[^1].ReportedActors);
                Assert.True(feed[^1].OnRelay);
                Assert.Equal(echo, feed[^1].IncludeSender);
                Assert.Equal(lifeCount + (kind is null ? 0 : 1), life.Count);
                if (kind is not null)
                    Assert.Equal(new RoundLifeReported(kind.Value, 1000000) { Initiator = initiator }, life[^1]);
                foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
                    await Assert.ThrowsAsync<PacketFormatException>(() => services.Get<IPacketRouter>()
                        .DispatchAsync(protocol, session, new((ushort)code, malformed), default).AsTask());
                Assert.Equal(count + 1, feed.Count);
                Assert.Equal(lifeCount + (kind is null ? 0 : 1), life.Count);
            }
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundLifeReported>>());
            foreach (var protocol in new[] { build + "/game", build + "/relay" })
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new((ushort)code, body), default);
            Assert.Equal(2, feed.Count);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" },
            services, events, NullLogger.Instance);
    }

    private const string Routes = """
      Contact: { opcode: gppContact_, structure: RoundDamagerContactNative, actors: [], echo_sender: true }
      Rescue: { opcode: gppRescue_, structure: RoundRescueNative, actors: [victim, rescuer], echo_sender: true, life_event: released, life_actor: victim }
""";

    /// <summary>Both listeners use native bodies and independently resolved outbound numbers, and unwind removes both.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task NativeBodiesUseNamedRoutesAndKeepTheirReportingParticipants(int shift)
    {
        using var fixture = new Tables(Routes);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        var feed = new List<RoundFeedSent>();
        var life = new List<RoundLifeReported>();
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            var messages = services.Get<IMessageRouter>().For(infrastructure);
            infrastructure.Effect(messages.On<RoundFeedSent>("native/game", (_, message, _) =>
            {
                feed.Add(message);
                return ValueTask.CompletedTask;
            }));
            infrastructure.Effect(messages.On<RoundLifeReported>("native/game", (_, message, _) =>
            {
                life.Add(message);
                return ValueTask.CompletedTask;
            }));
            var table = Opcodes(shift);
            new RoundFeedBinding("native/game", "native/relay", table, table.ForConnection(OpcodeConnection.Relay), fixture.Packets, "tw-454").Mount(binding);
            var session = new RecordingSession(7, table.Profile);
            var contact = Convert.FromHexString("785634120000A03F000020C000007040E0C81000");
            var rescue = Convert.FromHexString("E0C8100040420F0001000000");
            foreach (var protocol in new[] { "native/game", "native/relay" })
            {
                var contactCode = (ushort)((protocol == "native/game" ? 33301 : 34301) + shift);
                var rescueCode = (ushort)((protocol == "native/game" ? 32807 : 33807) + shift);
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(contactCode, contact), default);
                var report = feed[^1];
                Assert.Equal(40001 + shift, report.Code);
                Assert.Equal(contact, report.Payload.ToArray());
                Assert.Null(report.ReportedActors);
                Assert.True(report.OnRelay);
                Assert.True(report.IncludeSender);
                var lifeCount = life.Count;
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(rescueCode, rescue), default);
                report = feed[^1];
                Assert.Equal(40002 + shift, report.Code);
                Assert.Equal(rescue, report.Payload.ToArray());
                Assert.Equal(new[] { 1000000, 1100000 }, report.ReportedActors);
                Assert.Equal(lifeCount + 1, life.Count);
                Assert.Equal(new RoundLifeReported(RoundLifeEvent.Released, 1000000), life[^1]);
                var count = feed.Count;
                foreach (var body in new[] { contact[..^1], new byte[21] })
                    await Assert.ThrowsAsync<PacketFormatException>(() => services.Get<IPacketRouter>()
                        .DispatchAsync(protocol, session, new(contactCode, body), default).AsTask());
                foreach (var body in new[] { rescue[..^1], new byte[13] })
                    await Assert.ThrowsAsync<PacketFormatException>(() => services.Get<IPacketRouter>()
                        .DispatchAsync(protocol, session, new(rescueCode, body), default).AsTask());
                Assert.Equal(count, feed.Count);
                Assert.Equal(lifeCount + 1, life.Count);
            }
            Assert.Equal(4, feed.Count);
            Assert.Equal(2, life.Count);
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundLifeReported>>());
            foreach (var protocol in new[] { "native/game", "native/relay" })
                await services.Get<IPacketRouter>().DispatchAsync(protocol, session,
                    new((ushort)((protocol == "native/game" ? 33301 : 34301) + shift), contact), default);
            Assert.Equal(4, feed.Count);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" },
            services, events, NullLogger.Instance);
    }

    /// <summary>Malformed metadata fails before publishing any feed service or installing a partial route set.</summary>
    [Theory]
    [InlineData("opcode: gppMissing_, structure: RoundRescueNative, actors: [victim]", "C2S")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [unknown_8]", "as u32")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim, victim]", "distinct")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim], life_event: invented", "unsupported")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim], life_event: released", "life_actor")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim], life_actor: victim", "incompatible")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim], enabled: maybe", "true or false")]
    [InlineData("opcode: gppRescue_, structure: RoundRescueNative, actors: [victim], surprise: true", "unknown key")]
    public void InvalidRoutesPublishNothing(string route, string diagnostic)
    {
        using var fixture = new Tables("      Rescue: { " + route + " }");
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "feed", Plugin = "feed", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var error = Assert.Throws<ConfigException>(() => new RoundFeedBinding("native/game", "native/relay",
                Opcodes(0), Opcodes(0).ForConnection(OpcodeConnection.Relay), fixture.Packets, "tw-454").Mount(context));
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundLifeReported>>());
        }
        finally { context.Unwind(); }
    }

    /// <summary>A second action cannot consume the first action's native opcode, even under another key.</summary>
    [Fact]
    public void DuplicateNativeRoutesFailBeforePublication()
    {
        using var fixture = new Tables(Routes + "\n      Duplicate: { opcode: gppRescue_, structure: RoundRescueNative, actors: [victim] }");
        var error = Assert.Throws<ConfigException>(() => RoundFeedRoute.Load(fixture.Packets, "tw-454", Opcodes(0), Opcodes(0).ForConnection(OpcodeConnection.Relay)));
        Assert.Contains("distinct native C2S and S2C", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Damager inputs use separate connection and direction spaces; outputs never add inbound handlers.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagerRoutesPreserveDirectionAndConnectionSpaces(bool reuseAcrossSpaces)
    {
        using var fixture = new Tables(Routes);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        var feed = new List<RoundFeedSent>();
        var table = new OpcodeTable(TestWireDialects.Get("tw-454").Profile,
            BaseEntries().Concat(DamagerEntries(reuseAcrossSpaces)));
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            infrastructure.Effect(services.Get<IMessageRouter>().For(infrastructure)
                .On<RoundFeedSent>("native/game", (_, message, _) =>
                {
                    feed.Add(message);
                    return ValueTask.CompletedTask;
                }));
            new RoundFeedBinding("native/game", "native/relay", table,
                table.ForConnection(OpcodeConnection.Relay), fixture.Packets, "tw-454").Mount(binding);
            var session = new RecordingSession(7, table.Profile);
            var router = services.Get<IPacketRouter>();
            foreach (var entry in DamagerEntries(reuseAcrossSpaces).Where(entry => entry.Direction == PacketDirection.ClientToServer))
            {
                var onRelay = entry.Connection == OpcodeConnection.Relay;
                var protocol = onRelay ? "native/relay" : "native/game";
                var body = new byte[] { 7, 0, 255, 19 };
                var count = feed.Count;
                await router.DispatchAsync(protocol, session, new(entry.Value, body), default);
                var message = Assert.Single(feed.Skip(count));
                Assert.Equal(table.ForConnection(OpcodeConnection.Relay).S2C(entry.Name), message.Code);
                Assert.Equal(body, message.Payload.ToArray());
                Assert.True(message.OnRelay);
                body[0] = 99;
                Assert.Equal(7, message.Payload.Span[0]);
            }
            foreach (var protocol in new[] { "native/game", "native/relay" })
                foreach (ushort udpEnvelope in new ushort[] { 20003, 20004 })
                    await router.DispatchAsync(protocol, session, new(udpEnvelope, new byte[] { 1 }), default);
            Assert.Equal(16, feed.Count);
            if (!reuseAcrossSpaces)
            {
                foreach (var output in DamagerEntries(false).Where(entry => entry.Direction == PacketDirection.ServerToClient))
                    foreach (var protocol in new[] { "native/game", "native/relay" })
                        await router.DispatchAsync(protocol, session, new(output.Value, new byte[] { 1 }), default);
                Assert.Equal(16, feed.Count);
            }
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
            foreach (var entry in DamagerEntries(reuseAcrossSpaces).Where(entry => entry.Direction == PacketDirection.ClientToServer))
                await router.DispatchAsync(entry.Connection == OpcodeConnection.Relay ? "native/relay" : "native/game",
                    session, new(entry.Value, new byte[] { 1 }), default);
            Assert.Equal(16, feed.Count);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" },
            services, events, NullLogger.Instance);
    }

    /// <summary>Each required direction must exist, and collisions are rejected before any feed service is published.</summary>
    [Theory]
    [InlineData("partial")]
    [InlineData("missing-game")]
    [InlineData("missing-relay")]
    [InlineData("missing-output")]
    [InlineData("game-action")]
    [InlineData("relay-action")]
    [InlineData("output-action")]
    public void InvalidDamagerRoutesPublishNothing(string fault)
    {
        using var fixture = new Tables(Routes);
        var entries = DamagerEntries(false).ToList();
        var first = entries[0].Name;
        entries = fault switch
        {
            "partial" => entries.Where(entry => entry.Name == first).ToList(),
            "missing-game" => entries.Where(entry => !(entry.Name == first && entry.Direction == PacketDirection.ClientToServer
                && entry.Connection == OpcodeConnection.Game)).ToList(),
            "missing-relay" => entries.Where(entry => !(entry.Name == first && entry.Direction == PacketDirection.ClientToServer
                && entry.Connection == OpcodeConnection.Relay)).ToList(),
            "missing-output" => entries.Where(entry => !(entry.Name == first && entry.Direction == PacketDirection.ServerToClient)).ToList(),
            _ => entries,
        };
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Name != first)
                continue;
            ushort? collision = fault switch
            {
                "game-action" when entry.Direction == PacketDirection.ClientToServer && entry.Connection == OpcodeConnection.Game => 33301,
                "relay-action" when entry.Direction == PacketDirection.ClientToServer && entry.Connection == OpcodeConnection.Relay => 34301,
                "output-action" when entry.Direction == PacketDirection.ServerToClient => 40001,
                _ => null,
            };
            if (collision is { } value)
                entries[index] = entry with { Value = value };
        }
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "feed", Plugin = "feed", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            Assert.Throws<ConfigException>(() =>
            {
                var table = new OpcodeTable(TestWireDialects.Get("tw-454").Profile, BaseEntries().Concat(entries));
                new RoundFeedBinding("native/game", "native/relay", table,
                    table.ForConnection(OpcodeConnection.Relay), fixture.Packets, "tw-454").Mount(context);
            });
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundLifeReported>>());
        }
        finally { context.Unwind(); }
    }

    private static IEnumerable<OpcodeEntry> DamagerEntries(bool reuseAcrossSpaces)
    {
        string[] names = ["gppSplashDamager", "gppMobDropBombOnDead", "gppDropHumilPrevItem", "gppFireBullet3",
            "gppStormDamager", "gppSplashDamagerByDamager", "gppStormDamagerItemId", "gppNearDamager"];
        for (var index = 0; index < names.Length; index++)
        {
            yield return new((ushort)(reuseAcrossSpaces ? 300 + (index + 1) % names.Length : 100 + index),
                PacketDirection.ClientToServer, names[index], Evidence.Verified, null, null, Connection: OpcodeConnection.Game);
            yield return new((ushort)(reuseAcrossSpaces ? 300 + (index + 2) % names.Length : 200 + index), PacketDirection.ClientToServer, names[index], Evidence.Verified,
                null, null, Connection: OpcodeConnection.Relay);
            yield return new((ushort)(300 + index), PacketDirection.ServerToClient, names[index], Evidence.Verified,
                null, null, Connection: OpcodeConnection.Relay);
        }
    }

    private static IOpcodeTable Opcodes(int shift) => new OpcodeTable(TestWireDialects.Get("tw-454").Profile, BaseEntries(shift));

    private static IEnumerable<OpcodeEntry> BaseEntries(int shift = 0) =>
    [
        new((ushort)(33301 + shift), PacketDirection.ClientToServer, "gppContact_", Evidence.Verified, null, null),
        new((ushort)(34301 + shift), PacketDirection.ClientToServer, "gppContact_", Evidence.Verified, null, null, Connection: OpcodeConnection.Relay),
        new((ushort)(40001 + shift), PacketDirection.ServerToClient, "gppContact_", Evidence.Verified, null, null, Connection: OpcodeConnection.Relay),
        new((ushort)(32807 + shift), PacketDirection.ClientToServer, "gppRescue_", Evidence.Verified, null, null),
        new((ushort)(33807 + shift), PacketDirection.ClientToServer, "gppRescue_", Evidence.Verified, null, null, Connection: OpcodeConnection.Relay),
        new((ushort)(40002 + shift), PacketDirection.ServerToClient, "gppRescue_", Evidence.Verified, null, null, Connection: OpcodeConnection.Relay),
    ];

    private sealed class Tables : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-native-feed-" + Guid.NewGuid().ToString("N"));
        internal PacketTables Packets { get; }

        internal Tables(string routes)
        {
            Directory.CreateDirectory(_directory);
            // This isolated two-action fixture does not claim a complete native round or reset route.
            File.WriteAllText(Path.Combine(_directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-812: {}\n  cn-774:\n    base: kr-812\n  tw-454:\n    base: cn-774\n    round_feed:\n" + routes + "\n");
            File.Copy(Path.Combine(SharedDialectRows.PacketsDirectory(), "round-feed-native.yaml"), Path.Combine(_directory, "native.yaml"));
            File.WriteAllText(Path.Combine(_directory, "reset.yaml"), "version: 1\nstructs:\n  RoundResetConfirmation:\n    - actor: u32\n");
            Packets = PacketTables.Load(_directory);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
