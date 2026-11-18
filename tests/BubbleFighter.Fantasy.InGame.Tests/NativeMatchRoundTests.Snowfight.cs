using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>A slot-three extended shot reaches the other Snowfight player on their selected transport, with precisely that build's suffix.</summary>
    [Theory]
    [InlineData("kr-1256", "01000000020000001122334455667788", 48)]
    [InlineData("kr-1256", "0100000000000000", 40)]
    [InlineData("cn-774", "00000000020000001122334455667788", 48)]
    [InlineData("tw-454", "01000000", 36)]
    public async Task SnowfightExtendedSlotShotReachesOnlyItsPeer(string build, string suffix, int length)
    {
        const string name = "gppExtendedSlotProjectile_";
        await using var rig = new Rig(build, 20, fallback: true);
        await rig.StartAsync(27);
        await rig.LoadAsync(rig.Host);
        var hostRelay = new Session(101, build);
        var binding = await rig.Registry.BindCapabilityAsync(hostRelay, new(rig.Offers.Sent[0].Message.Ticket), default);
        rig.Registry.MarkInitialized(hostRelay, binding);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
        await rig.LoadAsync(rig.Guest);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
        lock (rig.Room.Gate) rig.Room.MarkPlaying();

        var directory = SharedDialectRows.PacketsDirectory();
        var table = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(directory)!.FullName, "profiles", build,
            "opcodes.yaml"), rig.Guest.Profile);
        var packets = PacketTables.Load(directory);
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var routes = RoundFeedRoute.Load(packets, build, game, relay);
        var route = Assert.Single(routes, r => r.GameInbound == 33295);
        Assert.Equal(game.C2S(name), route.GameInbound);
        Assert.Equal(relay.C2S(name), route.RelayInbound);
        Assert.Equal(relay.S2C(name), route.Outbound);
        var guestActor = RoomRegistry.SceneEntity(rig.Room.Seats.Single(s => s.Game.Id == rig.Guest.Id).Slot);
        // The short form ends after BC9A. Each client uses the same slot byte (03) and its own extended suffix.
        var body = Convert.FromHexString("0000000003010002000300040005000600EFBEADDEE778563412EFCDAB89BC9A" + suffix);
        BinaryPrimitives.WriteInt32LittleEndian(body, guestActor);
        Assert.Equal(length, body.Length);
        var (feed, life) = route.Read(packets, build, body);
        Assert.Null(life);
        Assert.Equal(new[] { guestActor }, feed.ReportedActors);
        await rig.Round.HandleFeedAsync(rig.Guest, feed, default);
        var received = Assert.Single(rig.Feed.Sent);
        Assert.Equal(hostRelay.Id, received.Session);
        Assert.Equal((ushort)33295, received.Message.Code);
        Assert.Equal(body, received.Message.Payload.ToArray());
        await rig.Round.HandleFeedAsync(hostRelay, feed, default);
        Assert.Single(rig.Feed.Sent);

        // Keep the same damager key across the launch and the later contact (TW omits the middle word).
        var hostActor = RoomRegistry.SceneEntity(rig.Room.Seats.Single(s => s.Game.Id == rig.Host.Id).Slot);
        var contact = new byte[build == "tw-454" ? 20 : 24];
        body.AsSpan(22, 4).CopyTo(contact);
        if (contact.Length == 24) BinaryPrimitives.WriteInt32LittleEndian(contact.AsSpan(16), guestActor);
        BinaryPrimitives.WriteInt32LittleEndian(contact.AsSpan(contact.Length - 4), hostActor);
        var impact = Assert.Single(routes, r => r.GameInbound == 33301).Read(packets, build, contact);
        await rig.Round.HandleFeedAsync(rig.Guest, impact.Feed, default);
        Assert.Equal(new ushort[] { 33295, 33301 }, rig.Feed.Sent.Select(entry => entry.Message.Code));
        Assert.All(rig.Feed.Sent, entry => Assert.Equal(hostRelay.Id, entry.Session));
        Assert.Equal(contact, rig.Feed.Sent[1].Message.Payload.ToArray());

        var slipRoute = Assert.Single(routes, r => r.GameInbound == 32833);
        Assert.Equal(relay.S2C("gppStepOnBanana"), slipRoute.Outbound);
        var slip = Convert.FromHexString("FFFFFFFF0000000001000000D51300000000803F0000004000004040"
            + (build == "kr-1256" ? "010000005D010000" : ""));
        Assert.Equal(build == "kr-1256" ? 36 : 28, slip.Length);
        BinaryPrimitives.WriteInt32LittleEndian(slip.AsSpan(4), hostActor);
        var reaction = slipRoute.Read(packets, build, slip);
        Assert.Null(reaction.Life);
        await rig.Round.HandleFeedAsync(rig.Guest, reaction.Feed, default);
        BinaryPrimitives.WriteInt32LittleEndian(slip, guestActor);
        await rig.Round.HandleFeedAsync(hostRelay, slipRoute.Read(packets, build, slip).Feed, default);
        Assert.Equal(2, rig.Feed.Sent.Count);
        BinaryPrimitives.WriteInt32LittleEndian(slip, -1);
        await rig.Round.HandleFeedAsync(hostRelay, reaction.Feed, default);
        Assert.Equal(new ushort[] { 33295, 33301, 32833 }, rig.Feed.Sent.Select(entry => entry.Message.Code));
        Assert.Equal(rig.Guest.Id, rig.Feed.Sent[2].Session);
        Assert.Equal(slip, rig.Feed.Sent[2].Message.Payload.ToArray());
        Assert.Throws<PacketFormatException>(() => slipRoute.Read(packets, build, slip[..^1]));
        Assert.Throws<PacketFormatException>(() => slipRoute.Read(packets, build, [.. slip, 0, 0, 0, 0]));

        Assert.Throws<PacketFormatException>(() => route.Read(packets, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => route.Read(packets, build, [.. body, 0, 0, 0, 0]));
        if (build != "tw-454")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(36), uint.MaxValue);
            Assert.Throws<PacketFormatException>(() => route.Read(packets, build, body));
        }
    }

    /// <summary>The direct-hit slip is published only while playing Snowfight; other modes and loading keep their own reactions.</summary>
    [Theory]
    [InlineData(27, false)]
    [InlineData(1, true)]
    public async Task SnowfightSlipRequiresPlayingSnowfight(int mode, bool playing)
    {
        await using var rig = new Rig("kr-1256", 20, fallback: true);
        await rig.StartAsync(mode);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(member);
            await rig.Round.HandleMatchConnectionReportedAsync(member, new(false), default);
        }
        if (playing) lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var actor = RoomRegistry.SceneEntity(rig.Room.Seats.Single(s => s.Game.Id == rig.Guest.Id).Slot);
        await rig.Round.HandleFeedAsync(rig.Guest, new(32833, new byte[32]) { SnowfightSlip = (-1, actor) }, default);
        Assert.Empty(rig.Feed.Sent);
    }
}
