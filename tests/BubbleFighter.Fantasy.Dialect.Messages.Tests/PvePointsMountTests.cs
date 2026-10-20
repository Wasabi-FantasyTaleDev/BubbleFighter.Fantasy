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

/// <summary>Checks installed score-unit routes and independently audited live-rank policies.</summary>
public sealed class PvePointsMountTests
{
    /// <summary>Uses point deltas for local-sort clients and keeps rank snapshots for clients that install them.</summary>
    [Theory]
    [InlineData("kr-812", false, false)]
    [InlineData("cn-717", false, false)]
    [InlineData("cn-324", true, false)]
    [InlineData("id-400", true, false)]
    [InlineData("id-1809", true, false)]
    [InlineData("kr-812", false, true)]
    [InlineData("id-400", true, true)]
    public async Task PointRequestsAndRepliesFollowNativeLiveStatePolicy(string build, bool ranks, bool remap)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile).ForConnection(OpcodeConnection.Game);
        var table = remap ? new OpcodeTable(profile, full.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 1000)) })) : full;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = new PluginContext(new PluginRow
        {
            Id = "room", Plugin = "dialect.rooms", Source = "points-mount-test",
            ClientBuild = new(build[..2], int.Parse(build[3..]), 0),
            Config = new Dictionary<string, object?>
            {
                ["protocol"] = protocol, ["packets_directory"] = TestBuilds.PacketsDirectory(), ["pve_points"] = true,
            },
        }, fixture.Services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            async Task Request(byte[] body) => await fixture.Packets.DispatchAsync(protocol, session,
                new(table.C2S("gcpPvePangUnit"), body), default);
            await Request(Convert.FromHexString("0F000000"));
            Assert.Equal(new RoundPvePangReported(15), Assert.Single(fixture.Messages.Received));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[3]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(new byte[8]));
            fixture.Messages.HasHandler = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Request(new byte[4]));
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundPvePointsChanged>>());
            await sender.SendAsync(session, new(1, 0, [new(1, 0, 1)]), default);
            Assert.Equal(ranks ? 1 : 0, session.Sent.Count);
            if (ranks)
            {
                Assert.Equal(table.S2C("gspNotifyRankInfo"), session.Sent[0]);
                Assert.Equal(32, session.Bodies[0].Length);
            }
            session.Sent.Clear();
            session.Bodies.Clear();
            await sender.SendAsync(session, new(1, 11, [new(1, 11, 1)]), default);
            Assert.Equal(ranks ? 2 : 1, session.Sent.Count);
            Assert.Equal(table.S2C("gspNotifyGamePoint"), session.Sent[0]);
            Assert.Equal("01000000000000000B000000", Convert.ToHexString(session.Bodies[0]));
            if (ranks)
            {
                Assert.Equal(table.S2C("gspNotifyRankInfo"), session.Sent[1]);
                Assert.Equal("000000000000000001000000010000000B000000000000000000000001000000", Convert.ToHexString(session.Bodies[1]));
            }
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPvePointsChanged>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPvePangReported>>());
    }

    /// <summary>Refuses unreviewed native support before publishing a route or service.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-706")]
    public void UnreviewedPointSupportCannotBeEnabledByAConfigFlag(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        using var fixture = new MatchMessagesMountTests.Fixture(new OpcodeTable(profile, []));
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        Assert.Throws<ConfigException>(() => new PvePointsBinding(build + "/game", new OpcodeTable(profile, []),
            TestBuilds.All.Tables, build).Mount(context, fixture.Messages));
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundPvePointsChanged>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundPvePangReported>>());
    }
}
