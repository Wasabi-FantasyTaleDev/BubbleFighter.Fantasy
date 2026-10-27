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
        var requestOpcode = table.C2S(build == "kr-1256" ? "Unknown602" : "gcpRespawn");
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("infrastructure");
        var binding = Context("reset");
        var session = new RecordingSession(7, profile);
        var requests = new List<RoundResetRequested>();
        var observed = new List<PacketReceivedEvent>();
        try
        {
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            infrastructure.Effect(events.On<PacketReceivedEvent>(observed.Add));
            infrastructure.Effect(services.Get<IMessageRouter>().For(infrastructure).On<RoundResetRequested>(protocol, (_, request, _) =>
            {
                requests.Add(request);
                return ValueTask.CompletedTask;
            }));
            new RoundResetBinding(protocol, table, tables, build, NullLogger.Instance).Mount(binding);
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundResetGranted>>());
            Assert.Single(services.GetAll<IInboundMessageBinding<RoundResetRequested>>());
            var body = Convert.FromHexString(hex);
            await Dispatch(body);
            var request = Assert.Single(requests);
            Assert.Equal(1000000, request.Actor);
            Assert.Equal(body, request.Request.ToArray());
            await sender.SendAsync(session, new(request.Request), default);
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
            Assert.Empty(services.GetAll<IMessageSender<RoundResetGranted>>());
            Assert.Empty(services.GetAll<IInboundMessageBinding<RoundResetRequested>>());
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
        var values = RoundResetBinding.Read(tables, build, "RoundResetConfirmation", body);
        Assert.Equal(1000000, values["actor"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, "RoundResetConfirmation", values));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => RoundResetBinding.Read(tables, build, "RoundResetConfirmation", body.AsSpan(0, length)));
        Assert.Throws<PacketFormatException>(() => RoundResetBinding.Read(tables, build, "RoundResetConfirmation", [.. body, 0]));
    }
    /// <summary>A missing or mistyped confirmation actor is rejected before any feed service is published.</summary>
    [Theory]
    [InlineData("", "RoundResetConfirmation")]
    [InlineData("  RoundResetConfirmation:\n    - actor: u16\n", "actor")]
    public void FeedRejectsInvalidConfirmationBeforeRegistration(string structure, string diagnostic)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-reset-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), "version: 1\nbuilds:\n  kr-1256: {}\n");
            File.WriteAllText(Path.Combine(directory, "reset.yaml"), "version: 1\nstructs:\n  Unused:\n    - value: u32\n" + structure);
            var tables = PacketTables.Load(directory);
            var services = new ServiceRegistry();
            var context = new PluginContext(new PluginRow { Id = "feed", Plugin = "feed", Source = "test" },
                services, new EventBus(NullLogger.Instance), NullLogger.Instance);
            var opcodes = new OpcodeTable(TestWireDialects.Get("kr-1256").Profile, []);
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
