using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks native rescue routes, progress bounds and owned sender cleanup through the room plugin.</summary>
public sealed class RescueMountTests
{
    /// <summary>Mounts each reviewed rescue dialect with its own runtime table and preserves index semantics.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task NativeRescueMountDispatchesAndUnloads(string build, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        if (remap) table = new OpcodeTable(profile, table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) }));
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = new PluginContext(new PluginRow
        {
            Id = "room", Plugin = "dialect.rooms", Source = "rescue-mount-test",
            ClientBuild = new(build[..2], int.Parse(build[3..]), 0),
            Config = new Dictionary<string, object?>
            {
                ["protocol"] = protocol, ["packets_directory"] = TestBuilds.PacketsDirectory(), ["rescue_missions"] = true,
            },
        }, fixture.Services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            async Task Request(byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpMissionProcess"), body), default);
            await Request(Convert.FromHexString("020000006500000000000000"));
            Assert.Equal(new RoundRescueProgressReported(2, 101), Assert.Single(fixture.Messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[11]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[16]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(Convert.FromHexString("020000006500000001000000")));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(new byte[12]));
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundRescueMissionsPrepared>>())
                .SendAsync(session, new([new(1, 24000, 25000, ["killmonster", "1", "2", "ms_citizen"])]), default);
            Assert.Equal(table.S2C("gspMapMissionInfo"), Assert.Single(session.Sent));
            Assert.True(session.Bodies[0].Length > 32);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundRescueMissionStarted>>()).SendAsync(session, new(2), default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundRescueMissionFailed>>()).SendAsync(session, new(3), default);
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundRescueMissionSucceeded>>()).SendAsync(session, new(2), default);
            Assert.Equal(new[] { table.S2C("gspResMissionStart"), table.S2C("gspResMissionTimeOut"), table.S2C("gspResMissionOK") }, session.Sent.Skip(1));
            Assert.Equal("02000000", Convert.ToHexString(session.Bodies[1]));
            Assert.Equal("03000000", Convert.ToHexString(session.Bodies[2]));
            Assert.Equal("0000000002000000", Convert.ToHexString(session.Bodies[3]));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundRescueProgressReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundRescueMissionsPrepared>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundRescueMissionStarted>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundRescueMissionFailed>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundRescueMissionSucceeded>>());
    }

    /// <summary>A configuration flag cannot enable an unreviewed mission surface or publish partial effects.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-706")]
    public void UnreviewedMissionResourcesFailBeforeEffects(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var table = new OpcodeTable(profile, []);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        Assert.Throws<ConfigException>(() => new RescueBinding(build + "/game", table,
            TestBuilds.All.Tables, build).Mount(context, fixture.Messages));
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundRescueMissionsPrepared>>());
    }
}
