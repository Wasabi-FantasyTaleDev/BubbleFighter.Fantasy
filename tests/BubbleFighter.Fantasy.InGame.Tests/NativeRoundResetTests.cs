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

/// <summary>Pins native reset requests, grants and relay confirmations without activating unrelated round families.</summary>
public sealed class NativeRoundResetTests
{
    /// <summary>Native plain-word and tagged resets reach the same typed request and preserve the native reply bytes.</summary>
    [Theory]
    [InlineData("kr-1256", 632, "40420F00BABA01000000")]
    [InlineData("kr-1256", 632, "40420F003A78563412BA00000000")]
    [InlineData("kr-1256", 632, "40420F00BA3A7856341201000000")]
    [InlineData("kr-1256", 632, "40420F003A785634123AF0DEBC9A01000000")]
    [InlineData("cn-774", 605, "40420F007856341200000000")]
    [InlineData("cn-774", 605, "40420F00FFFFFFFF7F000000")]
    [InlineData("tw-454", 605, "40420F007856341200000000")]
    [InlineData("tw-454", 605, "40420F00FFFFFFFF7F000000")]
    public async Task NativeResetUsesOwnRequestAndGrantLayouts(string build, int replyOpcode, string hex)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var profile = TestWireDialects.Get(build).Profile;
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var protocol = build + "/game";
        var requestOpcode = table.C2S("gcpRespawn");
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("reset");
        var session = new RecordingSession(7, profile);
        var requests = new List<RoundSpawnAccepted>();
        var observed = new List<PacketReceivedEvent>();
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            infrastructure.Effect(events.On<PacketReceivedEvent>(observed.Add));
            infrastructure.Effect(services.Get<IMessageRouter>().For(infrastructure).On<RoundSpawnAccepted>(protocol, (_, request, _) =>
            {
                requests.Add(request);
                return ValueTask.CompletedTask;
            }));
            var respawn = new RoundRespawnBinding(protocol, table, tables, build);
            respawn.Mount(binding);
            RoomRoutes.Bind(binding, protocol, table, NullLogger.Instance, "gcpRespawn", respawn.Read);
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundPlayerRespawned>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<RoundSpawnAccepted>>());
            var body = Convert.FromHexString(hex);
            await Dispatch(body);
            var request = Assert.Single(requests);
            Assert.Equal(1000000, request.Actor);
            Assert.Equal(body, request.Request.ToArray());
            Assert.Equal(new RoundRespawnRules(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation,
                RoundRespawnAdmission.CleanedDeath), request.Rules);
            await sender.SendAsync(session, new RoundPlayerRespawned(1, 0, request.Actor, 0) { Request = request.Request }, default);
            Assert.Equal((byte[])[.. body, 0, 0, 0, 0], session.Body((ushort)replyOpcode));
            for (var length = 0; length < body.Length; length++)
                await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch(body[..length]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch([.. body, 0]));
            if (build == "kr-1256")
            {
                var invalid = body.ToArray();
                invalid[4] = 0;
                await Assert.ThrowsAsync<PacketFormatException>(() => Dispatch(invalid));
            }
            Assert.Single(requests);
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IMessageSender<RoundPlayerRespawned>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundSpawnAccepted>>());
            await Dispatch(body);
            Assert.False(observed.Last().HasHandler);
            Assert.Single(requests);
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = id, Source = "test" },
            services, events, NullLogger.Instance);
        Task Dispatch(byte[] body) => services.Get<IPacketRouter>().DispatchAsync(protocol, session, new(requestOpcode, body), default).AsTask();
    }

    /// <summary>Relay confirmations use each native reader's complete body and reject every truncated prefix.</summary>
    [Theory]
    [InlineData("kr-1256", "40420F00BABA0100000000")]
    [InlineData("kr-1256", "40420F003A78563412BA0100000000")]
    [InlineData("kr-1256", "40420F00BA3A785634120100000000")]
    [InlineData("kr-1256", "40420F003A785634123AF0DEBC9A0100000000")]
    [InlineData("cn-774", "40420F00785634120101000000")]
    [InlineData("tw-454", "40420F00785634120101000000")]
    public void ConfirmationLayoutsKeepNativeWordsAndOptionalTags(string build, string hex)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var body = Convert.FromHexString(hex);
        var values = RoundRespawnBinding.ReadTagged(tables, build, "RoundResetConfirmation", body);
        Assert.Equal(1000000, values["actor"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, "RoundResetConfirmation", values));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RoundRespawnBinding.ReadTagged(tables, build, "RoundResetConfirmation", body.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => RoundRespawnBinding.ReadTagged(tables, build, "RoundResetConfirmation", [.. body, 0]));
    }
    /// <summary>An action-only feed mounts without an unrelated reset confirmation layout and removes its services on unload.</summary>
    [Fact]
    public async Task ActionFeedDoesNotRequireAnUnselectedResetLayout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-action-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("feed");
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-1256:\n    capabilities: { round_ping: false }\n    round_feed:\n      Dash: { opcode: gppDash, structure: Dash, actors: [actor] }\n");
            File.WriteAllText(Path.Combine(directory, "action.yaml"), "version: 1\nstructs:\n  Dash:\n    - actor: u32\n");
            var tables = PacketTables.Load(directory);
            var opcodes = OpcodeTableLoader.Load(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            new RoundFeedBinding("kr-1256/game", "kr-1256/relay", opcodes, opcodes.ForConnection(OpcodeConnection.Relay),
                tables, "kr-1256").Mount(binding);
            Assert.Single(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Single(services.GetAll<IMessageSender<RoundFeedReceived>>());
            await binding.UnwindAsync();
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
            Directory.Delete(directory, recursive: true);
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = "test", Source = "test" },
            services, events, NullLogger.Instance);
    }

    /// <summary>A selected reset route with a missing or mistyped actor is rejected before any feed service is published.</summary>
    [Theory]
    [InlineData("", "RoundResetConfirmation")]
    [InlineData("  RoundResetConfirmation:\n    - actor: u16\n", "actor")]
    public void FeedRejectsInvalidConfirmationBeforeRegistration(string structure, string diagnostic)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-reset-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-1256:\n    capabilities: { round_ping: false }\n    round_feed:\n      Reset: { opcode: Unknown32926, structure: RoundResetConfirmation, actors: [actor], nested: true, life_event: reset_confirmed, life_actor: actor }\n");
            File.WriteAllText(Path.Combine(directory, "reset.yaml"), "version: 1\nstructs:\n  Dash:\n    - actor: u32\n" + structure);
            var tables = PacketTables.Load(directory);
            var services = new ServiceRegistry();
            var context = new PluginContext(new PluginRow { Id = "feed", Plugin = "feed", Source = "test" },
                services, new EventBus(NullLogger.Instance), NullLogger.Instance);
            var opcodes = OpcodeTableLoader.Load(Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
            var error = Assert.Throws<ConfigException>(() =>
                new RoundFeedBinding("kr-1256/game", "kr-1256/relay", opcodes, opcodes.ForConnection(OpcodeConnection.Relay),
                    tables, "kr-1256").Mount(context));
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundFeedSent>>());
            Assert.Empty(services.GetAll<IMessageSender<RoundFeedReceived>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundLifeReported>>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
