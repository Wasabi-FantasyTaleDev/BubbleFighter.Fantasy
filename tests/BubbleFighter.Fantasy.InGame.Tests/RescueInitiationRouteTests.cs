using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Pins the native rescue precursor independently from the victim's later release report.</summary>
public sealed class RescueInitiationRouteTests
{
    /// <summary>The two actor words and final byte survive both inputs without creating a completed rescue.</summary>
    [Theory]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("03")]
    [InlineData("04")]
    [InlineData("E7")]
    public async Task RescueInitiationPreservesNineBytesAndDoesNotEchoOrRelease(string tail)
    {
        var body = "E0C8100040420F00" + tail;
        Assert.Equal(9, Convert.FromHexString(body).Length);
        await new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile("kr-1256", 33280,
            body, [1100000, 1000000], null, null, false);
    }

    /// <summary>The reviewed name identifies the same native frame on both connections and in both directions.</summary>
    [Fact]
    public void RescueInitiationHasItsOwnReviewedIdentity()
    {
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"),
            TestWireDialects.Get("kr-1256").Profile);
        foreach (var connection in new[] { OpcodeConnection.Game, OpcodeConnection.Relay })
        {
            var scoped = table.ForConnection(connection);
            Assert.Equal(33280, scoped.C2S("gppTryRescued_"));
            Assert.Equal(33280, scoped.S2C("gppTryRescued_"));
        }
    }

    /// <summary>A matching catalog number does not activate the precursor on unaudited modern builds.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void RescueInitiationDoesNotEnableUnauditedBuilds(string build)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"),
            TestWireDialects.Get(build).Profile);
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gppTryRescued_"));
        var routes = RoundFeedRoute.Load(tables, build, table, table.ForConnection(OpcodeConnection.Relay));
        Assert.DoesNotContain(routes, route => route.GameInbound == 33280 || route.RelayInbound == 33280);
    }
}
