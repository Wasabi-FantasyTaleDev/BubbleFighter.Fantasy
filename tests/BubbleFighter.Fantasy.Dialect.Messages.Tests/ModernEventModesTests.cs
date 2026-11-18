using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the event-mode result configuration and both directions of the Control Point game exchange.</summary>
public sealed class ModernEventModesTests
{
    /// <summary>The effective profile keeps event result boards, including Psionic only where the client has the mode.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    public async Task ResultBoardIsEnabledForNativeEventModes(string build, bool psionic)
    {
        var profiles = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles");
        var rows = new ProfileComposer(profiles, key =>
            key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null).Compose(build, []);
        var modes = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().ResultScoreboardModes;
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol);
        try
        {
            new RoundResultBinding(protocol, table, TestBuilds.Packets, build, modes, NullLogger.Instance).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScoreboard>>());
            foreach (var mode in new[] { 27, 29, 37 })
            {
                var session = new MatchMessagesMountTests.Session(table.Profile);
                var row = new RoundScoreboardRow(0, "Winner", 2, 1, new(0, 1, 2, 3, 4), null);
                await sender.SendAsync(session, new RoundScoreboard(7, 2, [17, 51], [row], WithRewards: true)
                    { ModeId = mode }, default);
                if (mode == 37 && !psionic)
                {
                    Assert.Empty(session.Sent);
                    continue;
                }
                Assert.Equal((ushort)(build == "kr-1256" ? 614 : 627), Assert.Single(session.Sent));
                var reader = new PacketReader(Assert.Single(session.Bodies));
                var lead = PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreDataLead");
                Assert.Equal((byte)2, lead["winner_slot"]);
                foreach (var total in new[] { 17, 51 })
                    Assert.Equal(total, PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreHeadEntry")["team_total"]);
            }
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The node report and BFRunner's opposite-direction packet must remain independently addressable.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    public void NodeChangeKeepsSignedLeaveAndDirection(string build, bool initial)
    {
        var table = TestBuilds.Opcodes(build);
        Assert.Equal((ushort)763, table.S2C("gspControlPointSnapshot_"));
        Assert.Equal((ushort)763, table.C2S("gcpBFRunnerCheckPointValue"));
        Assert.Equal((ushort)766, table.C2S("gcpControlPointChange_"));
        var leave = Convert.FromHexString("FFFFFFFF");
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "ControlPointChange", leave);
        Assert.Equal(-1, decoded["node"]);
        Assert.Equal(leave, PacketCodec.Write(TestBuilds.Packets, build, "ControlPointChange", decoded));
        if (!initial)
        {
            Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpControlPointInitialChange_"));
            return;
        }
        Assert.Equal((ushort)767, table.C2S("gcpControlPointInitialChange_"));
        var body = Convert.FromHexString("0500000044332211");
        var change = PacketCodec.Read(TestBuilds.Packets, build, "ControlPointInitialChange", body);
        Assert.Equal(5, change["node"]);
        Assert.Equal(0x11223344, change["stage_max_node"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "ControlPointInitialChange", change));
    }

    /// <summary>The native HUD requires a whole snapshot and independently consumes both vectors and the two four-byte flags.</summary>
    [Theory]
    [InlineData("kr-1256", 5)]
    [InlineData("cn-774", 5)]
    [InlineData("tw-454", 3)]
    public async Task SnapshotPinsWireOrderAndHudCardinality(string build, int count)
    {
        const string first = "0200000003000000020000000B0000000C0000000100000016000000"
            + "04000000050000000100000000000000";
        var bytes = Convert.FromHexString((count == 5 ? "05000000" : "03000000") + first + new string('0', (count - 1) * 32 * 2));
        Assert.Equal(count == 5 ? 176 : 112, bytes.Length);
        var state = PacketCodec.Read(TestBuilds.Packets, build, "ControlPointSnapshot", bytes);
        var nodes = Assert.IsType<object?[]>(state["nodes"]);
        Assert.Equal(count, nodes.Length);
        var node = Assert.IsType<Dictionary<string, object?>>(nodes[0]);
        Assert.Equal(2, node["owner_team"]);
        Assert.Equal(3, node["capturing_team"]);
        Assert.Equal(new object?[] { 11, 12 }, Assert.IsType<object?[]>(node["team_two_seats"]));
        Assert.Equal(new object?[] { 22 }, Assert.IsType<object?[]>(node["team_one_seats"]));
        Assert.Equal(4, node["team_two_progress"]);
        Assert.Equal(5, node["team_one_progress"]);
        Assert.Equal(true, node["contested"]);
        Assert.Equal(false, node["owner_highlight"]);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, build, "ControlPointSnapshot", state));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "ControlPointSnapshot", bytes[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "ControlPointSnapshot", [.. bytes, 0]));
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var ctx = fixture.Context(build, protocol);
        try
        {
            var binding = new ControlPointBinding(protocol, table, TestBuilds.Packets, build);
            binding.Mount(ctx);
            Assert.Equal(count, binding.NodeCount);
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await fixture.Packets.DispatchAsync(protocol, session, new(table.C2S("gcpControlPointChange_"),
                Convert.FromHexString("FFFFFFFF")), default);
            Assert.Equal(new RoundControlPointChanged(-1), Assert.Single(fixture.Messages.Received));
            var nodeViews = Enumerable.Repeat(new ControlPointView(0, 0, [], [], 0, 0), count).ToArray();
            Assert.Throws<ArgumentException>(() => { _ = binding.SendAsync(session, new(nodeViews[..^1]), default); });
            nodeViews[0] = new(2, 1, [22], [11, 12], 5, 4);
            await binding.SendAsync(session, new(nodeViews), default);
            Assert.Equal(table.S2C("gspControlPointSnapshot_"), Assert.Single(session.Sent));
            bytes[8] = 1; // The first record now names team 1 as the attacker.
            Assert.Equal(bytes, Assert.Single(session.Bodies));
        }
        finally { await ctx.UnwindAsync(); }
    }
}
