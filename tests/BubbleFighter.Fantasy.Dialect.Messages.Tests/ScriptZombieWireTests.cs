using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native mode-8 selection, the complete team panel and owned peer confirmation.</summary>
public sealed class ScriptZombieWireTests
{
    private const string Build = "kr-1256";

    /// <summary>The qualified request preserves both native tags and leaves an ordinary team click unchanged.</summary>
    [Fact]
    public void QualifiedTeamRequestRequiresOptInAndRetainsOrdinaryToggle()
    {
        var body = Convert.FromHexString("3A010000003ACC000000D0AE0100");
        Assert.Equal(14, body.Length);
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, body));
        Assert.Equal(new ScriptZombieConversion(1, 204, 110288),
            RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, body, true).ZombieConversion);
        Assert.Null(RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, [0xBA, 0xBA], true).ZombieConversion);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray(), new byte[] { 0, 0xBA } })
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, malformed, true));
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build,
            Convert.FromHexString("BA3ACC000000D0AE0100"), true));
    }

    /// <summary>A conversion includes both team rows and both trailing native panel vectors.</summary>
    [Fact]
    public void ConversionReplyIncludesTheEntireNativeTeamPanel()
    {
        RoomMemberView[] members = [new(0, 11, "human", 1, 1, false, 300001, []), new(1, 22, "zombie", 1, 34, false, 300001, [])];
        var body = ScriptZombieBinding.WriteTeam(TestBuilds.Packets, Build, new(new(1, 204, 110288), members));
        Assert.Equal(44, body.Length);
        Assert.Equal("01000000223ACC000000D0AE0100020000000101000000000000002201000000010000000000000000000000",
            Convert.ToHexString(body));
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScriptZombieTeamChange", body);
        var panel = Assert.IsType<Dictionary<string, object?>>(fields["team_info"]);
        Assert.Equal(2, Assert.IsType<object?[]>(panel["team_slots"]).Length);
    }

    /// <summary>Peer conversion preserves its byte team, optional character kind and exact body length.</summary>
    [Fact]
    public void PeerConversionPinsUnalignedFieldsAndOptionalKind()
    {
        var body = Convert.FromHexString("2201000000000000003ACC000000D0AE0100");
        Assert.Equal(18, body.Length);
        var report = new RoundScriptZombieConversionReported(1, 34, 0, 204, 110288);
        Assert.Equal(report, ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, body));
        Assert.Equal(body, ScriptZombieBinding.WritePeer(TestBuilds.Packets, Build, report));
        var absent = ScriptZombieBinding.WritePeer(TestBuilds.Packets, Build, report with { ZombieKind = null });
        Assert.Equal(14, absent.Length);
        Assert.Null(ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, absent).ZombieKind);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, malformed));
        absent[9] = 0;
        Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, absent));
        body[9] = 0;
        Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, body));
    }

    /// <summary>Each regional writer and consumer fixes its own tag-free request, reply, peer and result widths.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RegionalConversionsUseIndependentNativeLayouts(string build)
    {
        var request = Convert.FromHexString("01000000CC000000D0AE0100");
        Assert.Equal(12, request.Length);
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, build, request));
        Assert.Equal(new ScriptZombieConversion(1, 204, 110288),
            RoomRequests.ReadTeamChange(TestBuilds.Packets, build, request, true).ZombieConversion);
        Assert.Null(RoomRequests.ReadTeamChange(TestBuilds.Packets, build,
            Convert.FromHexString("FFFFFFFF0000000000000000"), true).ZombieConversion);
        var peer = Convert.FromHexString("220100000000000000CC000000D0AE0100");
        Assert.Equal(17, peer.Length);
        var message = new RoundScriptZombieConversionReported(1, 34, 0, 204, 110288);
        Assert.Equal(message, ScriptZombieBinding.ReadPeer(TestBuilds.Packets, build, peer));
        Assert.Equal(peer, ScriptZombieBinding.WritePeer(TestBuilds.Packets, build, message));
        RoomMemberView[] members = [new(0, 11, "human", 1, 1, false, 300001, []), new(1, 22, "zombie", 1, 34, false, 300001, [])];
        var team = ScriptZombieBinding.WriteTeam(TestBuilds.Packets, build, new(new(1, 204, 110288), members));
        Assert.Equal(43, team.Length);
        Assert.Equal("0100000022CC000000D0AE0100020000000101000000000000002201000000010000000000000000000000",
            Convert.ToHexString(team));
        foreach (var malformed in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, build, malformed, true));
        foreach (var malformed in new[] { peer[..^1], peer.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, build, malformed));
    }

    /// <summary>Native peer routes resolve by name on both game and relay protocols and unload with their owner.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public async Task MountedExchangeUsesNamesAndUnloadsRoutes(string build, bool remap)
    {
        var native = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["relay_protocol"] = build + "/relay", ["modern_rooms"] = true, ["initial_tutorial"] = build == "kr-1256", ["script_zombies"] = true,
            ["result_scoreboard_modes"] = new[] { 1, 8 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(native.Profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            var body = ScriptZombieBinding.WritePeer(TestBuilds.Packets, build, new(1, 34, 0, 204, 110288));
            foreach (var protocol in new[] { build + "/game", build + "/relay" })
            {
                await fixture.Packets.DispatchAsync(protocol, session,
                    new(table.C2S("gppScriptZombieConversion_"), body), default);
                var report = Assert.IsType<RoundScriptZombieConversionReported>(fixture.Messages.Received[^1]);
                Assert.Equal("false", TestBuilds.Packets.ValuesOf(build)["round_feed_passthrough"]);
                Assert.Null(report.OtherModeFeed);
            }
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScriptZombieConversionReported>>())
                .SendAsync(session, new(1, 34, 0, 204, 110288), default);
            Assert.Equal(table.S2C("gppScriptZombieConversion_"), Assert.Single(session.Sent));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundScriptZombieTeamChanged>>());
    }

    /// <summary>Omission leaves the feature unmounted and unsupported builds reject activation.</summary>
    [Theory]
    [InlineData("kr-1256", false, true)]
    [InlineData("cn-774", false, true)]
    [InlineData("tw-454", false, true)]
    [InlineData("kr-812", true, false)]
    public async Task ExchangeIsOffByDefaultAndRequiresItsBuildAudit(string build, bool enabled, bool activates)
    {
        Assert.False(new RoomsOptions().ScriptZombies);
        Assert.Equal(build != "kr-812", TestBuilds.Packets.CapabilityOf(build, "script_zombies"));
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = build != "kr-812", ["script_zombies"] = enabled, ["result_scoreboard_modes"] = new[] { 1, 8 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            if (activates) await new RoomsPlugin().ActivateAsync(context, default);
            else Assert.Contains("script_zombies", (await Assert.ThrowsAsync<ConfigException>(async () =>
                await new RoomsPlugin().ActivateAsync(context, default))).Message);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundScriptZombieTeamChanged>>());
            if (activates)
            {
                var opcode = TestBuilds.Opcodes(build).C2S("gppScriptZombieConversion_");
                Assert.DoesNotContain((build + "/game", opcode), fixture.Packets.Handlers.Keys);
                Assert.DoesNotContain((build + "/relay", opcode), fixture.Packets.Handlers.Keys);
            }
        }
        finally { await context.UnwindAsync(); }
    }
}
