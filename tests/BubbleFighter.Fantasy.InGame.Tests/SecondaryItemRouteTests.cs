using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins secondary healing, projection-end and storm-hit bodies without replaying their local effects twice.</summary>
public sealed class SecondaryItemRouteTests
{
    /// <summary>Both protocol inputs preserve the full native body and admit the recipient independently from the source.</summary>
    [Theory]
    [InlineData("gppHitByItem", "ItemHealing", 32829, "40420F0081000000E0C81000", 12)]
    [InlineData("gppEndFreezeCannon", "FreezeCannonEnd", 32831, "40420F00", 4)]
    [InlineData("gppStormCloudHit_", "StormCloudHit", 32932, "40420F00E0C81000", 8)]
    public async Task SecondaryBodiesKeepNativeWordsWithoutSenderReplay(string name, string structure, int opcode, string hex, int length)
    {
        const string build = "kr-1256";
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        foreach (var connection in new[] { OpcodeConnection.Game, OpcodeConnection.Relay })
        {
            var scoped = game.ForConnection(connection);
            Assert.Equal(opcode, scoped.C2S(name));
            Assert.Equal(opcode, scoped.S2C(name));
        }
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        var values = PacketCodec.ReadScalars(tables, build, structure, body);
        Assert.Equal(1_000_000, values["actor"]);
        if (structure == "ItemHealing") Assert.Equal(129, values["item"]);
        if (structure != "FreezeCannonEnd") Assert.Equal(1_100_000, values["source"]);
        Assert.Equal(body, PacketCodec.Write(tables, build, structure,
            values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(
            build, opcode, hex, [1_000_000], null, null, false);
    }

    /// <summary>Audited packet-class clients retain two healing words and one projection-end word without new typed routes.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    public void LegacyBodiesKeepNativeWidthsWithoutActivation(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        Assert.Equal(new[] { "actor", "item" }, tables.Resolve(build, "ItemHealing").Select(field => field.Name));
        var values = new Dictionary<string, object?> { ["actor"] = 1_000_000, ["item"] = 129, ["source"] = 1_100_000 };
        Assert.Equal("40420F0081000000", Convert.ToHexString(PacketCodec.Write(tables, build, "ItemHealing", values)));
        Assert.Equal("40420F00", Convert.ToHexString(PacketCodec.Write(tables, build, "FreezeCannonEnd", values)));
        Assert.Empty(tables.Resolve(build, "StormCloudHit"));
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var routes = RoundFeedRoute.Load(tables, build, game, game.ForConnection(OpcodeConnection.Relay));
        Assert.DoesNotContain(routes, route => route.Structure is "ItemHealing" or "FreezeCannonEnd" or "StormCloudHit");
    }

    /// <summary>The existing direct-use route stays peer-only and unreviewed modern builds gain no secondary item routes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernScopeDoesNotDuplicatePrimaryUse(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var routes = RoundFeedRoute.Load(tables, build, game, game.ForConnection(OpcodeConnection.Relay));
        Assert.False(Assert.Single(routes, route => route.GameInbound == game.C2S("gppUseItemDirect")).EchoSender);
        foreach (var structure in new[] { "ItemHealing", "FreezeCannonEnd", "StormCloudHit" })
        {
            if (build == "kr-1256" || (build == "cn-774" && structure == "ItemHealing"))
            {
                var route = Assert.Single(routes, route => route.Structure == structure);
                Assert.False(route.EchoSender);
                Assert.Equal(new[] { "actor" }, route.Actors);
            }
            else
            {
                Assert.Empty(tables.Resolve(build, structure));
                Assert.DoesNotContain(routes, route => route.Structure == structure);
            }
        }
    }
}
