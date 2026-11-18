using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins character attribute bodies, native names and the reporting player's required returned copy.</summary>
public sealed class CharacterAttributeRouteTests
{
    /// <summary>Ghost, freeze and bazooka attributes retain their exact words on both connections, including an absent item.</summary>
    [Theory]
    [InlineData(7, 108, "40420F00070000006C00000067452381")]
    [InlineData(4, 115, "40420F00040000007300000067452381")]
    [InlineData(22, 138, "40420F00160000008A00000067452381")]
    [InlineData(7, -1, "40420F0007000000FFFFFFFF67452381")]
    [InlineData(4, -1, "40420F0004000000FFFFFFFF67452381")]
    [InlineData(22, -1, "40420F0016000000FFFFFFFF67452381")]
    public async Task AttributeBodiesKeepNativeWordsAndReturnOnBothConnections(int attribute, int item, string hex)
    {
        const string build = "kr-1256";
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        Assert.Equal(new[] { "actor:u32", "attribute:u32", "item:u32", "unknown_12:u32" },
            tables.Resolve(build, "CharacterAttributeSet").Select(field => $"{field.Name}:{field.Type}"));
        Assert.Equal(new[] { "actor:u32", "attribute:u32" },
            tables.Resolve(build, "CharacterAttributeReset").Select(field => $"{field.Name}:{field.Type}"));
        foreach (var connection in new[] { OpcodeConnection.Game, OpcodeConnection.Relay })
        {
            var scoped = game.ForConnection(connection);
            Assert.Equal(38279, scoped.C2S("gppSetAttribute"));
            Assert.Equal(38279, scoped.S2C("gppSetAttribute"));
            Assert.Equal(38278, scoped.C2S("gppResetChrAttr"));
            Assert.Equal(38278, scoped.S2C("gppResetChrAttr"));
        }
        var values = new Dictionary<string, object?>
        {
            ["actor"] = 1_000_000, ["attribute"] = attribute, ["item"] = item,
            ["unknown_12"] = unchecked((int)0x81234567),
        };
        var applied = PacketCodec.Write(tables, build, "CharacterAttributeSet", values);
        Assert.Equal(16, applied.Length);
        Assert.Equal(hex, Convert.ToHexString(applied));
        var removed = PacketCodec.Write(tables, build, "CharacterAttributeReset", values);
        Assert.Equal(8, removed.Length);
        Assert.Equal(hex[..16], Convert.ToHexString(removed));
        await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(
            build, game.C2S("gppSetAttribute"), hex, [1_000_000], null, null, true);
        await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(
            build, game.C2S("gppResetChrAttr"), hex[..16], [1_000_000], null, null, true);
    }

    /// <summary>The original packet-class fields and the modern stream encode the same four-word and two-word bodies.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("id-400")]
    public void BaselineAttributeBodiesKeepTheirNativeFieldOrder(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        Assert.Equal(new[] { "actor:u32", "attribute:u32", "item:u32", "unknown_16:u32" },
            tables.Resolve(build, "CharacterAttributeSet").Select(field => $"{field.Name}:{field.Type}"));
        var values = new Dictionary<string, object?>
        {
            ["actor"] = 1_000_000, ["attribute"] = 7, ["item"] = -1,
            ["unknown_16"] = unchecked((int)0x81234567),
        };
        Assert.Equal("40420F0007000000FFFFFFFF67452381",
            Convert.ToHexString(PacketCodec.Write(tables, build, "CharacterAttributeSet", values)));
        Assert.Equal("40420F0007000000",
            Convert.ToHexString(PacketCodec.Write(tables, build, "CharacterAttributeReset", values)));
    }

    /// <summary>The earlier three-word attribute body keeps its native tail offset without gaining a typed attribute route.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    public void EarlyAttributeBodiesKeepThreeWordsWithoutNewTypedRoutes(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var fields = tables.Resolve(build, "CharacterAttributeSet");
        Assert.Equal(new[] { "actor:u32", "attribute:u32", "unknown_12:u32" },
            fields.Select(field => $"{field.Name}:{field.Type}"));
        Assert.Equal(new int?[] { 4, 8, 12 }, fields.Select(field => field.Offset));
        var body = PacketCodec.Write(tables, build, "CharacterAttributeSet", new Dictionary<string, object?>
        {
            ["actor"] = 1_000_000, ["attribute"] = 11, ["unknown_12"] = 8_000, ["item"] = 138,
        });
        Assert.Equal(12, body.Length);
        Assert.Equal("40420F000B000000401F0000", Convert.ToHexString(body));
        var values = PacketCodec.ReadScalars(tables, build, "CharacterAttributeSet", body);
        Assert.False(values.ContainsKey("item"));
        Assert.Equal(8_000, values["unknown_12"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(tables, build,
            "CharacterAttributeSet", body.Concat(new byte[4]).ToArray()));
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var routes = RoundFeedRoute.Load(tables, build, game, game.ForConnection(OpcodeConnection.Relay));
        Assert.DoesNotContain(routes, route => route.Structure is "CharacterAttributeSet" or "CharacterAttributeReset");
        Assert.Equal("true", tables.ValuesOf(build).GetValueOrDefault("round_feed_passthrough"));
    }

    /// <summary>Unreviewed modern builds gain no attribute handler, while the primary direct item action remains peer-only.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void AttributeRoutesAreLimitedToTheAuditedBuildsAndLeaveDirectUsePeerOnly(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var relay = game.ForConnection(OpcodeConnection.Relay);
        var routes = RoundFeedRoute.Load(tables, build, game, relay);
        Assert.False(Assert.Single(routes, route => route.GameInbound == game.C2S("gppUseItemDirect")).EchoSender);
        foreach (var (name, structure) in new[]
        {
            ("gppSetAttribute", "CharacterAttributeSet"), ("gppResetChrAttr", "CharacterAttributeReset"),
        })
        {
            if (build is "kr-1256" or "cn-774")
            {
                var route = Assert.Single(routes, candidate => candidate.Structure == structure);
                Assert.Equal(game.C2S(name), route.GameInbound);
                Assert.Equal(new[] { "actor" }, route.Actors);
            }
            else
            {
                Assert.Empty(tables.Resolve(build, structure));
                Assert.DoesNotContain(routes, route => route.Structure == structure);
                foreach (var scoped in new[] { game, relay })
                {
                    Assert.Null(scoped.Find(PacketDirection.ClientToServer, name));
                    Assert.Null(scoped.Find(PacketDirection.ServerToClient, name));
                }
            }
        }
    }
}
