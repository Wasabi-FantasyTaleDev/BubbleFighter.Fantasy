using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A permission reported on either connection reaches both current relays without accepting another player's actor.</summary>
    [Theory]
    [InlineData("kr-1256", false, false)]
    [InlineData("kr-1256", false, true)]
    [InlineData("kr-1256", true, false)]
    [InlineData("kr-1256", true, true)]
    [InlineData("cn-774", false, false)]
    [InlineData("cn-774", false, true)]
    [InlineData("cn-774", true, false)]
    [InlineData("cn-774", true, true)]
    public async Task WallHealingPermissionReturnsToItsOwnerAndKeepsActorAdmission(string build, bool guestReports, bool relayInput)
    {
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var relayOpcodes = game.ForConnection(OpcodeConnection.Relay);
        var route = Assert.Single(RoundFeedRoute.Load(tables, build, game, relayOpcodes),
            candidate => candidate.GameInbound == game.C2S("gppSetWallHealAllowed_"));
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostRelay = new RecordingSession(91, game.Profile);
            var guestRelay = new RecordingSession(92, game.Profile);
            lock (room.Gate)
                room.ModeId = (ushort)GameModeId.Horde;
            foreach (var (member, relay) in new[] { (rig.Host, hostRelay), (rig.Guest, guestRelay) })
            {
                var seat = room.FindByGame(member.Id)!;
                Assert.Same(seat, rig.Registry.BindRelay(relay, seat.Key));
                Assert.True(rig.Registry.IsMatchBound(relay));
                Assert.Same(room, rig.Registry.TryGet(relay.Id));
                Assert.Equal(room.StartGeneration, seat.RelayGeneration);
            }
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var owner = guestReports ? rig.Guest : rig.Host;
            var other = guestReports ? rig.Host : rig.Guest;
            var ownerRelay = guestReports ? guestRelay : hostRelay;
            var input = relayInput ? ownerRelay : owner;
            var actor = RoomRegistry.SceneEntity(room.FindByGame(owner.Id)!.Slot);
            var foreignActor = RoomRegistry.SceneEntity(room.FindByGame(other.Id)!.Slot);
            var count = 0;
            foreach (var allowed in new[] { true, false })
            {
                var body = PacketCodec.Write(tables, build, "WallHealAllowed", new Dictionary<string, object?>
                {
                    ["actor"] = actor, ["allowed"] = allowed,
                });
                var decoded = route.Read(tables, build, body);
                Assert.Null(decoded.Life);
                await rig.Round.HandleFeedAsync(input, decoded.Feed, default);
                count++;
                foreach (var relay in new[] { hostRelay, guestRelay })
                {
                    var received = rig.Feed.To(relay);
                    Assert.Equal(count, received.Length);
                    Assert.Equal(relayOpcodes.S2C("gppSetWallHealAllowed_"), received[^1].Code);
                    Assert.Equal(body, received[^1].Payload.ToArray());
                }
            }
            var foreign = PacketCodec.Write(tables, build, "WallHealAllowed", new Dictionary<string, object?>
            {
                ["actor"] = foreignActor, ["allowed"] = true,
            });
            await rig.Round.HandleFeedAsync(input, route.Read(tables, build, foreign).Feed, default);
            Assert.Equal(count, rig.Feed.To(hostRelay).Length);
            Assert.Equal(count, rig.Feed.To(guestRelay).Length);
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
