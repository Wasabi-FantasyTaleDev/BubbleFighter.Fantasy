using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises the enabled bomb exchange through the room plugin's declared services and reversible effects.</summary>
public sealed class BombModeActivationTests
{
    /// <summary>Native and remapped opcode tables deliver both reports, serialize the broadcast and release every registration.</summary>
    [Theory]
    [InlineData("kr-812", "kr", 812, false)]
    [InlineData("kr-812", "kr", 812, true)]
    [InlineData("cn-717", "cn", 717, false)]
    [InlineData("cn-717", "cn", 717, true)]
    public async Task EnabledExchangeDispatchesAndUnloads(string build, string region, int version, bool remap)
    {
        var protocol = build + "/game";
        var profile = new ClientProfile(build, region, version, 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol, "dialect.rooms", extra: new() { ["bomb_mission"] = true });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundBombStateReported>>());
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<RoundBombMissionOverReported>>());
            var state = Convert.FromHexString("04030201010000000000803F000020C00000C842");
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpBombState"), state), default);
            Assert.Equal(new RoundBombStateReported(RoundBombState.Planting, 0x01020304, 1, -2.5f, 100),
                Assert.Single(fixture.Messages.Received));
            var over = Convert.FromHexString("020000000500000009000000");
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpBombMissionOver"), over), default);
            Assert.Equal(new RoundBombMissionOverReported(2, 5, 9), fixture.Messages.Received[1]);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundBombStateChanged>>());
            await sender.SendAsync(session, new(RoundBombState.Planting, 0x01020304, 1, -2.5f, 100), default);
            Assert.Equal(table.S2C("gspBombState"), Assert.Single(session.Sent));
            Assert.Equal(Convert.ToHexString(state) + "00000000", Convert.ToHexString(Assert.Single(session.Bodies)));
            var observer = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundPlayerObserved>>());
            var supportedObservation = Assert.Single(fixture.Services.GetAll<IRoundObservationSender>());
            Assert.Same(observer, supportedObservation);
            Assert.True(supportedObservation.SupportsMode(19));
            Assert.False(supportedObservation.SupportsMode(1));
            Assert.False(supportedObservation.SupportsMode(20));
            await observer.SendAsync(session, new(1_100_000), default);
            Assert.Equal(table.S2C("gspObserver"), session.Sent[1]);
            Assert.Equal("E0C81000", Convert.ToHexString(session.Bodies[1]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpBombState"), state), default));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpBombMissionOver"), over), default));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBombStateReported>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundBombMissionOverReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundBombStateChanged>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPlayerObserved>>());
        Assert.Empty(fixture.Services.GetAll<IRoundObservationSender>());
    }

    /// <summary>Unsupported surfaces reject the option before any route or service is published, including modern early-return paths.</summary>
    [Theory]
    [InlineData("waiting_rooms_only")]
    [InlineData("menu_only")]
    [InlineData("browser_only")]
    [InlineData("typed_play_rooms")]
    public async Task UnsupportedSurfaceFailsBeforeMount(string option)
    {
        var profile = new ClientProfile("kr-812", "kr", 812, 0, 0, "kr-812");
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(profile, []));
        var context = fixture.Context("kr-812", "kr-812/game", "dialect.rooms", extra: new()
            { ["bomb_mission"] = true, [option] = option != "typed_play_rooms" });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(async () => await new RoomsPlugin().ActivateAsync(context, default));
            Assert.Contains("bomb_mission", error.Message);
            Assert.Empty(fixture.Packets.Handlers);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>A shared layout never implicitly authorizes an unaudited build's native bomb exchange.</summary>
    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", true)]
    [InlineData("cn-279", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-404", true)]
    [InlineData("cn-406", false)]
    [InlineData("cn-408", true)]
    [InlineData("cn-705", false)]
    [InlineData("cn-706", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-738", false)]
    [InlineData("cn-742", false)]
    [InlineData("id-400", true)]
    [InlineData("id-1809", true)]
    public void CapabilityRequiresIndependentEvidence(string build, bool supported) =>
        Assert.Equal(supported, TestBuilds.Packets.CapabilityOf(build, "bomb_mission"));
}
