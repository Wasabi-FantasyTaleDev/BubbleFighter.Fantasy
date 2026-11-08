using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>Real native match links never receive game-only wave tables, and guests cannot replace host groups.</summary>
    [Theory]
    [InlineData("kr-812", 20, 101)]
    [InlineData("kr-812", 20, 102)]
    [InlineData("kr-812", 20, 9)]
    [InlineData("cn-717", 20, 101)]
    [InlineData("cn-324", 18, 101)]
    [InlineData("cn-279", 12, 101)]
    [InlineData("cn-406", 20, 101)]
    [InlineData("id-400", 20, 101)]
    [InlineData("id-1809", 20, 101)]
    [InlineData("cn-705", 20, 101)]
    [InlineData("cn-706", 20, 101)]
    public async Task LegacyWaveTablesUseGameConnectionsWithNativeRelays(string build, int keyLength, int mode)
    {
        await using var rig = new Rig(build, keyLength);
        await rig.StartAsync();
        lock (rig.Room.Gate) rig.Room.ModeId = checked((ushort)mode);
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        var hostRelay = new Session(101, build);
        var guestRelay = new Session(102, build);
        foreach (var (relay, player) in new[] { (hostRelay, rig.Host), (guestRelay, rig.Guest) })
        {
            var offer = rig.Offers.Sent.Single(entry => entry.Session == player.Id).Message;
            var binding = await rig.Registry.BindCapabilityAsync(relay, new(offer.Ticket), default);
            rig.Registry.MarkInitialized(relay, binding);
            await rig.Round.HandleMatchConnectionReportedAsync(player, new(true), default);
        }

        var root = Path.GetDirectoryName(SharedDialectRows.PacketsDirectory())!;
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), rig.Host.Profile);
        var (schema, play) = RoomTables.Load(packets).Of(build);
        var services = new ServiceRegistry();
        var contexts = new List<PluginContext>();
        PluginContext Context(string id)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" }, services,
                new EventBus(NullLogger.Instance), NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("packets"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var tables = new NpcTables(table);
            new PlayRoomCodec(build + "/game", build, schema, play, tables, NullLogger.Instance, packets)
                { SelectMatchRelay = true }.Mount(Context("dialect"));
            Assert.Single(services.GetAll<IInboundMessageBinding<RoundNpcReported>>());
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
            var subscriber = Context("round");
            subscriber.Effect(services.Get<IMessageRouter>().For(subscriber).On<RoundNpcReported>(build + "/game",
                (session, report, ct) => rig.Round.HandleNpcAsync(session, report, sender, ct)));
            using var tutorial = new TutorialHandler(packets, build, schema, play.Tutorial, play.MatchStart,
                play.Wire, tables, NullLogger.Instance, oneSeatWaves: play.OneSeatWaves,
                messages: services.Get<IMessageRouter>());
            var route = Context("npc-route");
            var request = table.C2S("gcpReqWaveAllSpawnMobs");
            route.Effect(services.Get<IPacketRouter>().Register(build + "/game", request, tutorial.HandleWaveAllSpawnMobsAsync));
            var body = Convert.FromHexString("010000000100030000000400000000000000040000002E00440061006F0000000000000000000101F4010000040000002E00440061006F0000000000000000000102E8030000040000002E00440061006F0000000000000000000103DC050000040000002E00440061006F0000000000000000000104");
            if (mode is 9 or 102)
            {
                await services.Get<IMessageRouter>().DispatchAsync(build + "/game", rig.Host,
                    new RoundNpcReported(RoundNpcOperation.StartWave, new byte[4]), default);
                Assert.Empty(rig.Host.Sent);
                Assert.Empty(rig.Guest.Sent);
            }
            await services.Get<IPacketRouter>().DispatchAsync(build + "/game", rig.Guest, new(request, body), default);
            Assert.Empty(rig.Host.Sent);
            Assert.Empty(rig.Guest.Sent);
            await services.Get<IPacketRouter>().DispatchAsync(build + "/game", rig.Host, new(request, body), default);
            foreach (var player in new[] { rig.Host, rig.Guest })
            {
                Assert.Collection(player.Sent,
                    frame => { Assert.Equal(table.S2C("Unknown908"), frame.Opcode); Assert.Empty(frame.Body); },
                    frame => { Assert.Equal(table.S2C("gspRepWaveAllSpawnMobs"), frame.Opcode); Assert.Equal(body, frame.Body); });
            }
            Assert.Empty(hostRelay.Sent);
            Assert.Empty(guestRelay.Sent);
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoundNpcReported>>());
        Assert.Empty(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
    }

    private sealed class NpcTables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }
}
