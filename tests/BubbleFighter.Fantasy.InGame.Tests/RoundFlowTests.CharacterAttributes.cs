using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
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
    /// <summary>Each controlled recipient's attribute application and removal reaches owner and peer once, without admitting a foreign character.</summary>
    [Theory]
    [InlineData(7, 108, false, false)]
    [InlineData(7, 108, false, true)]
    [InlineData(7, 108, true, false)]
    [InlineData(7, 108, true, true)]
    [InlineData(4, 115, false, false)]
    [InlineData(4, 115, false, true)]
    [InlineData(4, 115, true, false)]
    [InlineData(4, 115, true, true)]
    [InlineData(22, 138, false, false)]
    [InlineData(22, 138, false, true)]
    [InlineData(22, 138, true, false)]
    [InlineData(22, 138, true, true)]
    public async Task CharacterAttributesReturnToTheirOwnerAndKeepForeignCharactersOut(
        int attribute, int item, bool guestReports, bool relayInput)
    {
        const string build = "kr-1256";
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var relayOpcodes = game.ForConnection(OpcodeConnection.Relay);
        var routes = RoundFeedRoute.Load(tables, build, game, relayOpcodes);
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var hostRelay = new RecordingSession(91, game.Profile);
            var guestRelay = new RecordingSession(92, game.Profile);
            lock (room.Gate)
                room.ModeId = (ushort)GameModeId.Item;
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
            var values = new Dictionary<string, object?>
            {
                ["actor"] = actor, ["attribute"] = attribute, ["item"] = item,
                ["unknown_12"] = unchecked((int)0x81234567),
            };
            var count = 0;
            foreach (var (name, structure) in new[]
            {
                ("gppSetAttribute", "CharacterAttributeSet"), ("gppResetChrAttr", "CharacterAttributeReset"),
            })
            {
                var route = Assert.Single(routes, candidate => candidate.GameInbound == game.C2S(name));
                var body = PacketCodec.Write(tables, build, structure, values);
                var foreign = (byte[])body.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(foreign, foreignActor);
                await rig.Round.HandleFeedAsync(input, route.Read(tables, build, foreign).Feed, default);
                Assert.Equal(count, rig.Feed.To(hostRelay).Length);
                Assert.Equal(count, rig.Feed.To(guestRelay).Length);
                foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
                    Assert.Throws<PacketFormatException>(() => route.Read(tables, build, malformed));
                var decoded = route.Read(tables, build, body);
                Assert.Null(decoded.Life);
                await rig.Round.HandleFeedAsync(input, decoded.Feed, default);
                count++;
                foreach (var relay in new[] { hostRelay, guestRelay })
                {
                    var received = rig.Feed.To(relay);
                    Assert.Equal(count, received.Length);
                    Assert.Equal(relayOpcodes.S2C(name), received[^1].Code);
                    Assert.Equal(body, received[^1].Payload.ToArray());
                }
            }
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
