using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the Zombie Battle G strategy's role selection exchange and its ordinary result-frame delivery.</summary>
public sealed class ZombieOriginTests
{
    /// <summary>The game socket decodes the reported form and writes exactly one private normalized word.</summary>
    [Fact]
    public async Task PrototypeBindingRejectsMalformedRequestsAndOutOfRangeDraws()
    {
        const string build = "kr-1256", protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        try
        {
            new ZombieOriginBinding(protocol, table, TestBuilds.Packets, build).Mount(context);
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await fixture.Packets.DispatchAsync(protocol, session, new(722, Convert.FromHexString("CA000000")), default);
            Assert.Equal(202, Assert.IsType<RoundZombieOriginPrototypeRequested>(Assert.Single(fixture.Messages.Received)).CharacterKind);
            foreach (var invalid in new[] { new byte[3], new byte[5] })
                await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(protocol, session, new(722, invalid), default).AsTask());
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundZombieOriginPrototypeRoll>>());
            await sender.SendAsync(session, new(9999), default);
            Assert.Equal((ushort)722, Assert.Single(session.Sent));
            Assert.Equal(Convert.FromHexString("0F270000"), Assert.Single(session.Bodies));
            foreach (var outside in new[] { -1, 10_000 })
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sender.SendAsync(session, new(outside), default).AsTask());
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>Both native tags must survive decoding and the grant echoes them; CN/TW reset words cannot acquire KR form semantics.</summary>
    [Theory]
    [InlineData("kr-1256", "40420F00BA3AD300000000000000", "40420F00BA3AD30000000000000000", null, 211)]
    [InlineData("kr-1256", "40420F003A05000000BA00000000", "40420F003A05000000BA0000000000", 5, null)]
    [InlineData("kr-1256", "40420F00BABA00000000", "40420F00BABA0000000000", null, null)]
    [InlineData("cn-774", "40420F00D300000000000000", "40420F00D30000000000000000", null, null)]
    [InlineData("tw-454", "40420F00D300000000000000", "40420F00D30000000000000000", null, null)]
    public async Task ResetConversionRequiresTheBuildsActualTaggedFields(string build, string requestHex, string peerHex, int? first, int? kind)
    {
        var protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        try
        {
            var binding = new RoundRespawnBinding(protocol, table, TestBuilds.Packets, build);
            binding.Mount(context);
            var body = Convert.FromHexString(requestHex);
            var request = binding.Read(body);
            var expected = build == "kr-1256" ? new RoundTaggedReset(first, kind) : null;
            Assert.Equal(expected, request.TaggedReset);
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await binding.SendAsync(session, new(1, 0, request.Actor, 0) { Request = request.Request }, default);
            Assert.Equal(body.Concat(new byte[4]).ToArray(), Assert.Single(session.Bodies));
            var file = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
            var relay = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(file, table.Profile).ForConnection(OpcodeConnection.Relay);
            var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, build, table, relay), r => r.LifeEvent == RoundLifeEvent.ResetConfirmed);
            var decoded = route.Read(TestBuilds.Packets, build, Convert.FromHexString(peerHex));
            Assert.Equal(expected, decoded.Feed.TaggedReset);
            Assert.Equal(1_000_000, Assert.IsType<RoundLifeReported>(decoded.Life).Actor);
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The KR game request and reply share a number but carry different inputs to the role decision.</summary>
    [Fact]
    public void PrototypeRequestAndRollHaveIndependentGameLayouts()
    {
        var game = TestBuilds.Opcodes("kr-1256");
        Assert.Equal((ushort)722, game.C2S("gcpZombieOriginPrototypeRequest_"));
        Assert.Equal((ushort)722, game.S2C("gspZombieOriginPrototypeRoll_"));
        Assert.Equal(Convert.FromHexString("78563412"), PacketCodec.Write(TestBuilds.Packets, "kr-1256",
            "ZombieOriginPrototypeRequest", new Dictionary<string, object?> { ["character_kind"] = 0x12345678 }));
        Assert.Equal(Convert.FromHexString("0F270000"), PacketCodec.Write(TestBuilds.Packets, "kr-1256",
            "ZombieOriginPrototypeRoll", new Dictionary<string, object?> { ["roll"] = 9999 }));
    }

    /// <summary>A native role report is five bytes without a kind or nine with a kind, including its actor and tag.</summary>
    [Theory]
    [InlineData("40420F00BA", null)]
    [InlineData("40420F003AC9000000", 201)]
    [InlineData("40420F003ACB000000", 203)]
    [InlineData("40420F003AD4000000", 212)]
    public void PeerPrototypeRetainsItsActorAndOptionalKind(string hex, int? kind)
    {
        var table = TestBuilds.Opcodes("kr-1256");
        var file = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles",
            "kr-1256", "opcodes.yaml");
        var relay = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(file, table.Profile).ForConnection(OpcodeConnection.Relay);
        foreach (var connection in new[] { table, relay })
        {
            Assert.Equal((ushort)34277, connection.C2S("gppZombieOriginPrototype_"));
            Assert.Equal((ushort)34277, connection.S2C("gppZombieOriginPrototype_"));
        }
        var bytes = Convert.FromHexString(hex);
        var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, "kr-1256", table, relay), row => row.GameInbound == 34277);
        var feed = route.Read(TestBuilds.Packets, "kr-1256", bytes).Feed;
        Assert.Equal((1_000_000, kind), feed.ZombieOriginPrototype!.Value);
        Assert.False(feed.IncludeSender);
        Assert.Equal(34277, (int)route.RelayInbound);
        Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, "kr-1256", Convert.FromHexString("40420F0000")));
        var shape = PacketCodec.Read(TestBuilds.Packets, "kr-1256", "ZombieOriginPrototype", bytes);
        Assert.Equal(1_000_000, shape["actor"]);
        if (kind is { } expected)
            Assert.Equal(expected, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(shape["prototype"])["zombie_kind"]);
        else
            Assert.Equal((byte)0xBA, shape["prototype"]);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, "kr-1256", "ZombieOriginPrototype", shape));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "ZombieOriginPrototype", bytes[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, "kr-1256", "ZombieOriginPrototype", [.. bytes, 0]));
    }

    /// <summary>Only the profile with the native G strategy publishes its existing team scoreboard for mode 43.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("tw-454", false)]
    public async Task ResultFrameRequiresTheNativeMode(string build, bool supported)
    {
        var table = TestBuilds.Opcodes(build);
        if (!supported)
        {
            Assert.Null(table.Find(PacketDirection.ServerToClient, "gspZombieOriginPrototypeRoll_"));
            Assert.Empty(TestBuilds.Packets.Resolve(build, "ZombieOriginPrototype"));
            var file = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles", build, "opcodes.yaml");
            var relay = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(file, table.Profile).ForConnection(OpcodeConnection.Relay);
            Assert.DoesNotContain(RoundFeedRoute.Load(TestBuilds.Packets, build, table, relay), row => row.ZombieOriginPrototype);
        }
        var profiles = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles");
        var rows = new ProfileComposer(profiles, key =>
            key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null).Compose(build, []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        Assert.Equal(supported, options.ZombieOrigin);
        var modes = options.ResultScoreboardModes;
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var protocol = build + "/game";
        var context = fixture.Context(build, protocol);
        try
        {
            new RoundResultBinding(protocol, table, TestBuilds.Packets, build, modes, NullLogger.Instance).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScoreboard>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await sender.SendAsync(session, new RoundScoreboard(7, 1, [17, 12], [], WithRewards: true)
                { ModeId = (int)GameModeId.ZombieOrigin }, default);
            if (!supported) { Assert.Empty(session.Sent); return; }
            Assert.Equal((ushort)614, Assert.Single(session.Sent));
            var reader = new PacketReader(Assert.Single(session.Bodies));
            Assert.Equal((byte)1, PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreDataLead")["winner_slot"]);
            foreach (var total in new[] { 17, 12 })
                Assert.Equal(total, PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreHeadEntry")["team_total"]);
        }
        finally { await context.UnwindAsync(); }
    }
}
