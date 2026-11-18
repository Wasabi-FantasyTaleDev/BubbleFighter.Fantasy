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
    /// <summary>Each added native action is mounted on both listeners with its own echo policy and exact shape.</summary>
    [Theory]
    [InlineData("kr-1256", 32930, "40420F00890000000000C03F000000C000005040", false)]
    [InlineData("cn-774", 32930, "40420F00890000000000C03F000000C000005040", false)]
    [InlineData("kr-1256", 34274, "40420F000000C03F000000C00000504000000000", false)]
    [InlineData("cn-774", 34274, "40420F000000C03F000000C000005040", false)]
    [InlineData("cn-774", 32829, "40420F0069000000E0C81000", false)]
    [InlineData("cn-774", 38279, "40420F00160000003701000067452381", true)]
    [InlineData("cn-774", 38278, "40420F0016000000", true)]
    public Task PsionicSkillFramesAreMountedOnBothListeners(string build, int opcode, string body, bool echo) =>
        new NativeRoundFeedRouteTests().NativeCombatUsesTheActualProfile(build, opcode, body, [1_000_000], null, null, echo);

    /// <summary>The local skill and its resulting effect can have different reporters and different recipients.</summary>
    [Theory]
    [InlineData("kr-1256", 103)]
    [InlineData("cn-774", 103)]
    [InlineData("kr-1256", 105)]
    [InlineData("cn-774", 105)]
    [InlineData("kr-1256", 300)]
    [InlineData("cn-774", 300)]
    [InlineData("kr-1256", 309)]
    [InlineData("cn-774", 309)]
    [InlineData("kr-1256", 310)]
    [InlineData("cn-774", 310)]
    [InlineData("kr-1256", 311)]
    [InlineData("cn-774", 311)]
    public async Task PsionicSkillEffectsReachTheNativeRecipients(string build, int item)
    {
        await using var rig = new Rig(build, 20, fallback: true);
        await rig.StartAsync(37);
        foreach (var seat in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(seat);
            await rig.Round.HandleMatchConnectionReportedAsync(seat, new(false), default);
        }
        var hostActor = RoomRegistry.SceneEntity(rig.Room.FindByGame(rig.Host.Id)!.Slot);
        var guestActor = RoomRegistry.SceneEntity(rig.Room.FindByGame(rig.Guest.Id)!.Slot);
        var directory = SharedDialectRows.PacketsDirectory();
        var packets = PacketTables.Load(directory);
        var table = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(directory)!.FullName, "profiles", build,
            "opcodes.yaml"), rig.Guest.Profile);
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var routes = RoundFeedRoute.Load(packets, build, game, relay);

        var turret = item == 300;
        var placementName = turret ? "gppUseItemDirectAtPos" : "gppSpawnBlackHole_";
        if (item is 300 or 309)
        {
            var route = Assert.Single(routes, r => r.GameInbound == game.C2S(placementName));
            await rig.Round.HandleFeedAsync(rig.Guest, route.Read(packets, build, PlacementBody()).Feed, default);
            Assert.Empty(rig.Feed.Sent);
        }
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        await Check(rig.Guest, rig.Host, "gppUseItemDirect", Words(guestActor, item), rig.Host.Id);
        if (item == 103) return; // Every viewer constructs the shield from the direct use itself.
        if (item is 300 or 309)
        {
            var route = Assert.Single(routes, r => r.GameInbound == game.C2S(placementName));
            Assert.Equal(turret ? 32930 : 34274, route.GameInbound);
            var placement = PlacementBody();
            await Check(rig.Guest, rig.Host, placementName, placement, rig.Host.Id);
            var invalidPosition = (byte[])placement.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(invalidPosition.AsSpan(turret ? 8 : 4), float.NaN);
            Assert.Throws<PacketFormatException>(() => route.Read(packets, build, invalidPosition));
            var start = rig.Feed.Sent.Count;
            if (turret)
            {
                var otherItem = (byte[])placement.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(otherItem.AsSpan(4), 237);
                await rig.Round.HandleFeedAsync(rig.Guest, route.Read(packets, build, otherItem).Feed, default);
            }
            else if (build == "kr-1256")
            {
                var badCount = (byte[])placement.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(badCount.AsSpan(16), int.MaxValue);
                Assert.Throws<PacketFormatException>(() => route.Read(packets, build, badCount));
            }
            var effect = route.Read(packets, build, placement).Feed;
            var nonFighter = (byte[])placement.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(nonFighter, guestActor + 1);
            await rig.Round.HandleFeedAsync(rig.Guest, route.Read(packets, build, nonFighter).Feed, default);
            lock (rig.Room.Gate) rig.Room.ModeId = 3;
            await rig.Round.HandleFeedAsync(rig.Guest, effect, default);
            Assert.Equal(start, rig.Feed.Sent.Count);
        }
        else if (item == 105)
        {
            // The controlled recipient reports the pack's effect; the source is the guest who used it.
            await Check(rig.Host, rig.Guest, "gppHitByItem", Words(hostActor, item, guestActor), rig.Guest.Id);
        }
        else
        {
            // Both gun items select character attribute 22, but the item word chooses which weapon to apply.
            await Check(rig.Guest, rig.Host, "gppSetAttribute", Words(guestActor, 22, item, unchecked((int)0x81234567)),
                rig.Host.Id, rig.Guest.Id);
            await Check(rig.Guest, rig.Host, "gppResetChrAttr", Words(guestActor, 22), rig.Host.Id, rig.Guest.Id);
        }

        async Task Check(Session reporter, Session foreign, string name, byte[] body, params long[] recipients)
        {
            var route = Assert.Single(routes, r => r.GameInbound == game.C2S(name));
            Assert.Equal(relay.C2S(name), route.RelayInbound);
            Assert.Throws<PacketFormatException>(() => route.Read(packets, build, body[..^1]));
            Assert.Throws<PacketFormatException>(() => route.Read(packets, build, [.. body, 0]));
            var feed = route.Read(packets, build, body).Feed with { OnRelay = true };
            var start = rig.Feed.Sent.Count;
            await rig.Round.HandleFeedAsync(foreign, feed, default);
            Assert.Equal(start, rig.Feed.Sent.Count);
            await rig.Round.HandleFeedAsync(reporter, feed, default);
            var sent = rig.Feed.Sent.Skip(start).ToArray();
            Assert.Equal(recipients.Order(), sent.Select(frame => frame.Session).Order());
            Assert.All(sent, frame =>
            {
                Assert.Equal(relay.S2C(name), frame.Message.Code);
                Assert.Equal(body, frame.Message.Payload.ToArray());
            });
        }

        // (1.5, -2, 3.25); only KR's field carries a counted list after the coordinates.
        byte[] PlacementBody() =>
            [.. (turret ? Words(guestActor, 137) : Words(guestActor)), .. Words(0x3fc00000, unchecked((int)0xc0000000), 0x40500000),
             .. (!turret && build == "kr-1256" ? Words(2, -1, 7) : Array.Empty<byte>())];

        static byte[] Words(params int[] values)
        {
            var bytes = new byte[values.Length * sizeof(int)];
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), values[i]);
            return bytes;
        }
    }
}
