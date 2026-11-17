using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins the actor and Boolean wall-healing permission and its round-trip routing.</summary>
public sealed class WallHealingRouteTests
{
    /// <summary>Both input connections decode each eight-byte permission value with the sender echo flag.</summary>
    [Theory]
    [InlineData("kr-1256", 38284)]
    [InlineData("cn-774", 38284)]
    public async Task WallHealingPermissionUsesNamedPacketsAndEchoesBothValues(string build, int opcode)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        Assert.Equal(new[] { "actor:u32", "allowed:bool" },
            tables.Resolve(build, "WallHealAllowed").Select(field => $"{field.Name}:{field.Type}"));
        foreach (var connection in new[] { OpcodeConnection.Game, OpcodeConnection.Relay })
        {
            var scoped = game.ForConnection(connection);
            Assert.Equal(opcode, scoped.C2S("gppSetWallHealAllowed_"));
            Assert.Equal(opcode, scoped.S2C("gppSetWallHealAllowed_"));
        }
        foreach (var allowed in new[] { false, true })
        {
            var hex = allowed ? "40420F0001000000" : "40420F0000000000";
            var body = PacketCodec.Write(tables, build, "WallHealAllowed", new Dictionary<string, object?>
            {
                ["actor"] = 1_000_000, ["allowed"] = allowed,
            });
            Assert.Equal(8, body.Length);
            Assert.Equal(hex, Convert.ToHexString(body));
            await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(
                build, opcode, hex, [1_000_000], null, null, true);
        }
    }

    /// <summary>The independently decoded night-vision state keeps its own identity and peer-only routing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NightVisionDoesNotInheritWallHealingPermission(bool enabled)
    {
        const string build = "tw-454";
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var relay = game.ForConnection(OpcodeConnection.Relay);
        Assert.Empty(tables.Resolve(build, "WallHealAllowed"));
        Assert.Equal(new[] { "actor:u32", "enabled:bool" },
            tables.Resolve(build, "NightVisionState").Select(field => $"{field.Name}:{field.Type}"));
        var routes = RoundFeedRoute.Load(tables, build, game, relay);
        Assert.DoesNotContain(routes, route => route.Structure == "WallHealAllowed");
        Assert.Equal("NightVisionState", Assert.Single(routes,
            route => route.GameInbound == game.C2S("gppSetNightVision_")).Structure);
        foreach (var connection in new[] { game, relay })
        {
            Assert.Equal(33274, connection.C2S("gppSetNightVision_"));
            Assert.Equal(33274, connection.S2C("gppSetNightVision_"));
        }
        var hex = enabled ? "40420F0001000000" : "40420F0000000000";
        var body = PacketCodec.Write(tables, build, "NightVisionState", new Dictionary<string, object?>
        {
            ["actor"] = 1_000_000, ["enabled"] = enabled,
        });
        Assert.Equal(8, body.Length);
        Assert.Equal(hex, Convert.ToHexString(body));
        await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(
            build, game.C2S("gppSetNightVision_"), hex, [1_000_000], null, null, false);
    }
}
