using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins modern portal position forwarding independently of the adjacent twelve-byte peer action.</summary>
public sealed class PortalTeleportTests
{
    /// <summary>The native sixteen-byte position frame retains actor validation, exact bytes and local queue ownership.</summary>
    [Theory]
    [InlineData("kr-1256", false)]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", false)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    [InlineData("tw-454", true)]
    public void NativePeerPositionPinsRouteIdentityAndNoSenderEcho(string build, bool remap)
    {
        var native = TestBuilds.Opcodes(build);
        Assert.Equal(32922, native.ForConnection(OpcodeConnection.Game).C2S("gppTeleportPortal"));
        var table = remap ? new OpcodeTable(native.Profile, native.All.Select(entry => entry with
            { Value = checked((ushort)(entry.Value + 1000)) })) : native;
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var route = Assert.Single(RoundFeedRoute.Load(TestBuilds.Packets, build, game, relay),
            value => value.Structure == "PortalTeleport");
        Assert.Equal(game.C2S("gppTeleportPortal"), route.GameInbound);
        Assert.Equal(relay.C2S("gppTeleportPortal"), route.RelayInbound);
        Assert.Equal(relay.S2C("gppTeleportPortal"), route.Outbound);
        Assert.NotEqual(game.C2S("Unknown32921"), route.GameInbound);
        var body = PacketCodec.Write(TestBuilds.Packets, build, "PortalTeleport", new Dictionary<string, object?>
        {
            ["actor"] = 101, ["position_x"] = 1f, ["position_y"] = -2.5f, ["position_z"] = 100f,
        });
        Assert.Equal(16, body.Length);
        Assert.Equal("650000000000803F000020C00000C842", Convert.ToHexString(body));
        var decoded = route.Read(TestBuilds.Packets, build, body);
        Assert.Equal(route.Outbound, decoded.Feed.Code);
        Assert.Equal(body, decoded.Feed.Payload.ToArray());
        Assert.Equal(new[] { 101 }, decoded.Feed.ReportedActors);
        Assert.False(decoded.Feed.IncludeSender);
        Assert.Null(decoded.Life);
        foreach (var malformed in new[] { body[..^1], body.Concat(new byte[1]).ToArray() })
            Assert.Throws<PacketFormatException>(() => route.Read(TestBuilds.Packets, build, malformed));
    }
}
