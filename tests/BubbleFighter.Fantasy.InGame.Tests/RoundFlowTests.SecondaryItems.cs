using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Secondary item reports reach only peers while preserving source identity and rejecting a foreign recipient.</summary>
    [Theory]
    [InlineData("gppHitByItem", "40420F0081000000E0C81000", 8, false, false)]
    [InlineData("gppHitByItem", "40420F0081000000E0C81000", 8, false, true)]
    [InlineData("gppHitByItem", "40420F0081000000E0C81000", 8, true, false)]
    [InlineData("gppHitByItem", "40420F0081000000E0C81000", 8, true, true)]
    [InlineData("gppEndFreezeCannon", "40420F00", -1, false, false)]
    [InlineData("gppEndFreezeCannon", "40420F00", -1, false, true)]
    [InlineData("gppEndFreezeCannon", "40420F00", -1, true, false)]
    [InlineData("gppEndFreezeCannon", "40420F00", -1, true, true)]
    [InlineData("gppStormCloudHit_", "40420F00E0C81000", 4, false, false)]
    [InlineData("gppStormCloudHit_", "40420F00E0C81000", 4, false, true)]
    [InlineData("gppStormCloudHit_", "40420F00E0C81000", 4, true, false)]
    [InlineData("gppStormCloudHit_", "40420F00E0C81000", 4, true, true)]
    public async Task SecondaryItemEffectsReachPeersWithoutReplayingTheSender(
        string name, string hex, int sourceOffset, bool guestReports, bool relayInput)
    {
        const string build = "kr-1256";
        var rig = new Rig();
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        var root = Directory.GetParent(SharedDialectRows.PacketsDirectory())!.FullName;
        var game = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var infrastructure = Context("item-infrastructure");
        var binding = Context("item-feed");
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.ModeId = (ushort)GameModeId.Item;
            var hostRelay = new RecordingSession(91, game.Profile);
            var guestRelay = new RecordingSession(92, game.Profile);
            foreach (var (member, relay) in new[] { (rig.Host, hostRelay), (rig.Guest, guestRelay) })
            {
                var seat = room.FindByGame(member.Id)!;
                Assert.Same(seat, rig.Registry.BindRelay(relay, seat.Key));
                Assert.Equal(room.StartGeneration, seat.RelayGeneration);
            }
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await new PacketRouterPlugin().ActivateAsync(infrastructure, default);
            await new MessageRouterPlugin().ActivateAsync(infrastructure, default);
            var messages = services.Get<IMessageRouter>().For(infrastructure);
            infrastructure.Effect(messages.On<RoundFeedSent>(build + "/game", rig.Round.HandleFeedAsync));
            var lifeReports = 0;
            infrastructure.Effect(messages.On<RoundLifeReported>(build + "/game", (_, _, _) =>
            {
                lifeReports++;
                return ValueTask.CompletedTask;
            }));
            new RoundFeedBinding(build + "/game", build + "/relay", game,
                game.ForConnection(OpcodeConnection.Relay), tables, build).Mount(binding);
            var owner = guestReports ? rig.Guest : rig.Host;
            var other = guestReports ? rig.Host : rig.Guest;
            var ownerRelay = guestReports ? guestRelay : hostRelay;
            var otherRelay = guestReports ? hostRelay : guestRelay;
            var input = relayInput ? ownerRelay : owner;
            var protocol = build + (relayInput ? "/relay" : "/game");
            var actor = RoomRegistry.SceneEntity(room.FindByGame(owner.Id)!.Slot);
            var foreignActor = RoomRegistry.SceneEntity(room.FindByGame(other.Id)!.Slot);
            var router = services.Get<IPacketRouter>();
            var ownerCount = 0;
            var otherCount = 0;
            {
                var code = game.C2S(name);
                var body = Body(actor, foreignActor);
                await router.DispatchAsync(protocol, input, new(code, body), default);
                otherCount++;
                AssertCounts();
                Assert.Equal(body, rig.Feed.To(otherRelay)[^1].Payload.ToArray());
                Assert.Equal(game.ForConnection(OpcodeConnection.Relay).S2C(name), rig.Feed.To(otherRelay)[^1].Code);
                await router.DispatchAsync(protocol, input, new(code, Body(foreignActor, actor)), default);
                AssertCounts();
                await router.DispatchAsync(protocol, new RecordingSession(77, game.Profile), new(code, body), default);
                AssertCounts();
                foreach (var malformed in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
                    await Assert.ThrowsAsync<PacketFormatException>(() => router.DispatchAsync(protocol, input, new(code, malformed), default).AsTask());
                AssertCounts();

                byte[] Body(int recipient, int source)
                {
                    var payload = Convert.FromHexString(hex);
                    BinaryPrimitives.WriteInt32LittleEndian(payload, recipient);
                    if (sourceOffset >= 0) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sourceOffset), source);
                    return payload;
                }
            }
            lock (room.Gate) room.MarkResult();
            var late = Convert.FromHexString(hex);
            BinaryPrimitives.WriteInt32LittleEndian(late, actor);
            if (sourceOffset >= 0) BinaryPrimitives.WriteInt32LittleEndian(late.AsSpan(sourceOffset), foreignActor);
            await router.DispatchAsync(protocol, input, new(game.C2S(name), late), default);
            AssertCounts();
            Assert.Equal(0, lifeReports);
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));

            void AssertCounts()
            {
                Assert.Equal(ownerCount, rig.Feed.To(ownerRelay).Length);
                Assert.Equal(otherCount, rig.Feed.To(otherRelay).Length);
            }
        }
        finally
        {
            await binding.UnwindAsync();
            await infrastructure.UnwindAsync();
            await rig.Registry.StopAllAsync();
        }

        PluginContext Context(string id) => new(new PluginRow { Id = id, Plugin = "test", Source = "test" },
            services, events, NullLogger.Instance);
    }
}
