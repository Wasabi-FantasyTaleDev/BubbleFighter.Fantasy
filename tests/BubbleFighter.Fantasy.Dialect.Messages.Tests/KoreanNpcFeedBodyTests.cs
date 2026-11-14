using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the independently read NPC action bodies and their separate transport opcode views.</summary>
public sealed class KoreanNpcFeedBodyTests
{
    /// <summary>Audited builds retain identical action bytes; each modern route validates the whole body.</summary>
    [Theory]
    [InlineData("gppDeleteObject", "NpcObjectDelete", 32774, 4, "2A192300", true)]
    [InlineData("gppIniPacket", "NpcIni", 32841, 6, "010000004100", false)]
    [InlineData("gppMobLuaMessage", "NpcLuaMessage", 32865, 10, "00000000010000004100", false)]
    [InlineData("gppDummyUnitMotion", "NpcDummyMotion", 32891, 14, "2A19230001000000410001000000", true)]
    [InlineData("gppAgentMoveTo", "NpcAgentMove", 32893, 20, "2A192300040000000000803F0000004000004040", true)]
    [InlineData("gppUseItemDirect", "NpcDirectItem", 32894, 8, "2A19230001000000", true)]
    [InlineData("gppKillObject", "NpcObjectKill", 32778, 8, "2A19230001000000", true)]
    [InlineData("gppChangeTurretState", "NpcTurretState", 32895, 8, "2A19230003000000", true)]
    public void NativeNpcFeedBodiesArePinned(string name, string structure, int opcode, int length, string hex, bool actor)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        foreach (var build in new[] { "kr-812", "kr-1256", "cn-774", "tw-454" })
        {
            var values = PacketCodec.Read(TestBuilds.Packets, build, structure, body);
            Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, structure, values));
        }
        foreach (var modern in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            var profile = new ClientProfile(modern, modern[..2], int.Parse(modern[3..]), 0, 0, modern);
            var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
            var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", modern, "opcodes.yaml"), profile);
            var game = table.ForConnection(OpcodeConnection.Game);
            var relay = table.ForConnection(OpcodeConnection.Relay);
            Assert.Equal(opcode, game.C2S(name));
            Assert.Equal(opcode, game.S2C(name));
            Assert.Equal(opcode, relay.C2S(name));
            Assert.Equal(opcode, relay.S2C(name));
            var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, modern, game, relay), entry => entry.Structure == structure);
            var decoded = route.Read(TestBuilds.Packets, modern, body);
            Assert.Null(decoded.Life);
            Assert.Equal(body, decoded.Feed.Payload.ToArray());
            Assert.Equal(opcode, decoded.Feed.Code);
            Assert.False(decoded.Feed.IncludeSender);
            Assert.True(decoded.Feed.OnRelay);
            Assert.Equal(actor, decoded.Feed.ReportedActors is not null);
            if (actor) Assert.Equal(BitConverter.ToInt32(body), Assert.Single(decoded.Feed.ReportedActors!));
            Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, modern, body[..^1]));
            Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, modern, [.. body, 0]));
        }
    }

    /// <summary>Modern NPC routes do not widen the existing legacy pass-through contracts.</summary>
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    public void ModernNpcActionRoutesDoNotExtendUnauditedBuilds(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var routes = RoundFeedRoute.Load(TestBuilds.Packets, build,
            table.ForConnection(OpcodeConnection.Game), table.ForConnection(OpcodeConnection.Relay));
        Assert.DoesNotContain(routes, route => route.Structure.StartsWith("Npc", StringComparison.Ordinal));
    }
}
