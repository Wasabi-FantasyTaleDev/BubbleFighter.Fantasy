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

/// <summary>Pins the native script selection, complete team panel, peer confirmation and stage result.</summary>
public sealed class ZombieEscapeWireTests
{
    private const string Build = "kr-1256";

    /// <summary>The qualified request preserves both native tags and leaves an ordinary team click unchanged.</summary>
    [Fact]
    public void QualifiedTeamRequestRequiresOptInAndRetainsOrdinaryToggle()
    {
        var body = Convert.FromHexString("3A010000003ACC000000D1AE0100");
        Assert.Equal(14, body.Length);
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, body));
        Assert.Equal(new ScriptZombieConversion(1, 204, 110289),
            RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, body, true).ZombieConversion);
        Assert.Null(RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, [0xBA, 0xBA], true).ZombieConversion);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray(), new byte[] { 0, 0xBA } })
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build, malformed, true));
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, Build,
            Convert.FromHexString("BA3ACC000000D1AE0100"), true));
    }

    /// <summary>A conversion includes both team rows and both trailing native panel vectors.</summary>
    [Fact]
    public void ConversionReplyIncludesTheEntireNativeTeamPanel()
    {
        RoomMemberView[] members = [new(0, 11, "human", 1, 1, false, 300001, []), new(1, 22, "zombie", 1, 34, false, 300001, [])];
        var body = ScriptZombieBinding.WriteTeam(TestBuilds.Packets, Build, new(new(1, 204, 110289), members));
        Assert.Equal(44, body.Length);
        Assert.Equal("01000000223ACC000000D1AE0100020000000101000000000000002201000000010000000000000000000000",
            Convert.ToHexString(body));
        var fields = PacketCodec.Read(TestBuilds.Packets, Build, "ScriptZombieTeamChange", body);
        var panel = Assert.IsType<Dictionary<string, object?>>(fields["team_info"]);
        Assert.Equal(2, Assert.IsType<object?[]>(panel["team_slots"]).Length);
    }

    /// <summary>Peer conversion preserves its byte team, optional character kind and exact body length.</summary>
    [Fact]
    public void PeerConversionPinsUnalignedFieldsAndOptionalKind()
    {
        var body = Convert.FromHexString("2201000000000000003ACC000000D1AE0100");
        Assert.Equal(18, body.Length);
        var report = new RoundScriptZombieConversionReported(1, 34, 0, 204, 110289);
        Assert.Equal(report, ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, body));
        Assert.Equal(body, ScriptZombieBinding.WritePeer(TestBuilds.Packets, Build, report));
        var absent = ScriptZombieBinding.WritePeer(TestBuilds.Packets, Build, report with { ZombieKind = null });
        Assert.Equal(14, absent.Length);
        Assert.Null(ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, absent).ZombieKind);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, malformed));
        absent[9] = 0;
        Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, Build, absent));
    }

    /// <summary>The result reader retains the raw outcome while requiring the audited canonical false tail.</summary>
    [Fact]
    public void StageResultConsumesTheCanonicalTailWithoutChangingOutcomeMeaning()
    {
        foreach (var outcome in new[] { 0, 1 })
        {
            var body = new byte[8];
            body[0] = (byte)outcome;
            Assert.Equal(new RoundStageResultReported(outcome), StageResultBinding.Read(TestBuilds.Packets, Build, body));
        }
        Assert.Throws<PacketFormatException>(() => StageResultBinding.Read(TestBuilds.Packets, Build, new byte[4]));
        Assert.Throws<PacketFormatException>(() => StageResultBinding.Read(TestBuilds.Packets, Build, new byte[9]));
        Assert.Throws<PacketFormatException>(() => StageResultBinding.Read(TestBuilds.Packets, Build, [0, 0, 0, 0, 2, 0, 0, 0]));
        Assert.Throws<NotSupportedException>(() => StageResultBinding.Read(TestBuilds.Packets, Build, [0, 0, 0, 0, 1, 0, 0, 0]));
        Assert.Throws<NotSupportedException>(() => StageResultBinding.Read(TestBuilds.Packets, Build, [2, 0, 0, 0, 0, 0, 0, 0]));
    }

    /// <summary>Each regional writer and consumer fixes its own tag-free request, reply, peer and result widths.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RegionalConversionsUseIndependentNativeLayouts(string build)
    {
        var request = Convert.FromHexString("01000000CC000000D1AE0100");
        Assert.Equal(12, request.Length);
        Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, build, request));
        Assert.Equal(new ScriptZombieConversion(1, 204, 110289),
            RoomRequests.ReadTeamChange(TestBuilds.Packets, build, request, true).ZombieConversion);
        Assert.Null(RoomRequests.ReadTeamChange(TestBuilds.Packets, build,
            Convert.FromHexString("FFFFFFFF0000000000000000"), true).ZombieConversion);
        var peer = Convert.FromHexString("220100000000000000CC000000D1AE0100");
        Assert.Equal(17, peer.Length);
        var message = new RoundScriptZombieConversionReported(1, 34, 0, 204, 110289);
        Assert.Equal(message, ScriptZombieBinding.ReadPeer(TestBuilds.Packets, build, peer));
        Assert.Equal(peer, ScriptZombieBinding.WritePeer(TestBuilds.Packets, build, message));
        RoomMemberView[] members = [new(0, 11, "human", 1, 1, false, 300001, []), new(1, 22, "zombie", 1, 34, false, 300001, [])];
        var team = ScriptZombieBinding.WriteTeam(TestBuilds.Packets, build, new(new(1, 204, 110289), members));
        Assert.Equal(43, team.Length);
        Assert.Equal("0100000022CC000000D1AE0100020000000101000000000000002201000000010000000000000000000000",
            Convert.ToHexString(team));
        Assert.Equal(new RoundStageResultReported(1), StageResultBinding.Read(TestBuilds.Packets, build, [1, 0, 0, 0]));
        Assert.Throws<PacketFormatException>(() => StageResultBinding.Read(TestBuilds.Packets, build, new byte[8]));
        foreach (var malformed in new[] { request[..^1], request.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(TestBuilds.Packets, build, malformed, true));
        foreach (var malformed in new[] { peer[..^1], peer.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<PacketFormatException>(() => ScriptZombieBinding.ReadPeer(TestBuilds.Packets, build, malformed));
    }

    /// <summary>Tutorial and Zombie Escape share a single result route and all native numbers resolve by name.</summary>
    [Theory]
    [InlineData("kr-1256", false, false, false)]
    [InlineData("kr-1256", true, false, false)]
    [InlineData("cn-774", false, false, false)]
    [InlineData("cn-774", true, false, false)]
    [InlineData("tw-454", false, false, false)]
    [InlineData("tw-454", true, false, false)]
    [InlineData("kr-1256", false, true, false)]
    [InlineData("kr-1256", true, true, false)]
    [InlineData("cn-774", false, true, false)]
    [InlineData("cn-774", true, true, false)]
    [InlineData("tw-454", false, true, false)]
    [InlineData("tw-454", true, true, false)]
    [InlineData("kr-1256", false, true, true)]
    [InlineData("kr-1256", true, true, true)]
    [InlineData("cn-774", false, true, true)]
    [InlineData("cn-774", true, true, true)]
    public async Task MountedExchangeUsesNamesAndOwnsEachStageRouteOnce(string build, bool remap, bool ordinary, bool ghost)
    {
        var native = TestBuilds.Opcodes(build);
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : native;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["relay_protocol"] = build + "/relay", ["modern_rooms"] = true, ["initial_tutorial"] = build == "kr-1256", ["zombie_escape"] = true,
            ["script_zombies"] = ordinary, ["ghost_zombies"] = ghost, ["result_scoreboard_modes"] = new[] { 1, 8, 28, 32 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        var session = new MatchMessagesMountTests.Session(native.Profile);
        try
        {
            await new RoomsPlugin().ActivateAsync(context, default);
            var modes = Assert.Single(fixture.Services.GetAll<IScriptZombieModes>());
            Assert.Equal(build + "/game", modes.Protocol);
            Assert.Equal(1 + (ordinary ? 1 : 0) + (ghost ? 1 : 0), modes.Modes.Count);
            Assert.Equal(ghost, modes.Modes.Contains(GameModeId.ZombieGhost));
            Assert.Contains(GameModeId.ZombieEscape, modes.Modes);
            Assert.Equal(ordinary, modes.Modes.Contains(GameModeId.Zombie));
            Assert.Equal(2, fixture.Services.GetAll<IInboundMessageBinding<RoundScriptZombieConversionReported>>().Count());
            await fixture.Packets.DispatchAsync(build + "/game", session,
                new(table.C2S("gcpReqEndStage"), new byte[build == "kr-1256" ? 8 : 4]), default);
            Assert.Equal(new RoundStageResultReported(0), Assert.Single(fixture.Messages.Received));
            foreach (var protocol in new[] { build + "/game", build + "/relay" })
            {
                await fixture.Packets.DispatchAsync(protocol, session,
                    new(table.C2S("gppScriptZombieConversion_"), ScriptZombieBinding.WritePeer(TestBuilds.Packets, build, new(1, 34, 0, 204, 110289))), default);
                Assert.IsType<RoundScriptZombieConversionReported>(fixture.Messages.Received[^1]);
            }
            await Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScriptZombieConversionReported>>())
                .SendAsync(session, new(1, 34, 0, 204, 110289), default);
            Assert.Equal(table.S2C("gppScriptZombieConversion_"), Assert.Single(session.Sent));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<RoundStageResultReported>>());
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundScriptZombieTeamChanged>>());
        Assert.Empty(fixture.Services.GetAll<IScriptZombieModes>());
    }

    /// <summary>Omission leaves the feature unmounted and unsupported builds reject activation.</summary>
    [Theory]
    [InlineData("kr-1256", false, true)]
    [InlineData("cn-774", false, true)]
    [InlineData("tw-454", false, true)]
    [InlineData("kr-812", true, false)]
    public async Task ExchangeIsOffByDefaultAndRequiresItsBuildAudit(string build, bool enabled, bool activates)
    {
        Assert.False(new RoomsOptions().ZombieEscape);
        Assert.Equal(build != "kr-812", TestBuilds.Packets.CapabilityOf(build, "zombie_escape"));
        using var fixture = new MatchMessagesMountTests.Fixture(TestBuilds.Opcodes(build));
        var context = fixture.Context(build, build + "/game", "dialect.rooms", extra: new()
        {
            ["modern_rooms"] = build != "kr-812", ["zombie_escape"] = enabled, ["result_scoreboard_modes"] = new[] { 1, 32 },
        });
        context.BindServices(typeof(RoomsPlugin).GetCustomAttribute<PluginAttribute>()!);
        try
        {
            if (activates) await new RoomsPlugin().ActivateAsync(context, default);
            else Assert.Contains("zombie_escape", (await Assert.ThrowsAsync<ConfigException>(async () =>
                await new RoomsPlugin().ActivateAsync(context, default))).Message);
            Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundScriptZombieTeamChanged>>());
        }
        finally { await context.UnwindAsync(); }
    }
}
