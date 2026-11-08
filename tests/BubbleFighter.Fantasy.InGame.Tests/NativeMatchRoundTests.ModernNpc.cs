using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>Native host requests release both wave latches on game sessions while preserving room authority and standalone play.</summary>
    [Theory]
    [InlineData("cn-774", 101, false, false)]
    [InlineData("cn-774", 101, false, true)]
    [InlineData("cn-774", 101, true, false)]
    [InlineData("cn-774", 101, true, true)]
    [InlineData("cn-774", 106, true, true)]
    [InlineData("tw-454", 101, false, false)]
    [InlineData("tw-454", 101, false, true)]
    [InlineData("tw-454", 101, true, false)]
    [InlineData("tw-454", 101, true, true)]
    [InlineData("tw-454", 106, true, true)]
    [InlineData("kr-1256", 101, true, true)]
    public async Task ModernPirateRequestsReachTheSharedRoundOnGameConnections(
        string build, int mode, bool nativeRelay, bool playing)
    {
        await using var rig = new Rig(build, 20, enabled: nativeRelay);
        await rig.StartAsync(mode);
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        var relays = new List<Session>();
        if (nativeRelay)
        {
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var relay = new Session(member.Id + 100, build);
                var offer = rig.Offers.Sent.Single(entry => entry.Session == member.Id).Message;
                var binding = await rig.Registry.BindCapabilityAsync(relay, new(offer.Ticket), default);
                rig.Registry.MarkInitialized(relay, binding);
                await rig.Round.HandleMatchConnectionReportedAsync(member, new(true), default);
                relays.Add(relay);
            }
        }
        if (playing)
            lock (rig.Room.Gate) rig.Room.MarkPlaying();
        Assert.Equal(playing ? RoomPhase.Playing : RoomPhase.Starting, rig.Room.Phase);
        Assert.Null(rig.Room.Tutorial);
        var root = Path.GetDirectoryName(SharedDialectRows.PacketsDirectory())!;
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), rig.Host.Profile)
            .ForConnection(OpcodeConnection.Game);
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
            new NpcBinding(build + "/game", table, packets, build, NullLogger.Instance).Mount(Context("npc"));
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
            var subscriber = Context("round");
            subscriber.Effect(services.Get<IMessageRouter>().For(subscriber).On<RoundNpcReported>(build + "/game",
                (session, report, ct) => rig.Round.HandleNpcAsync(session, report, sender, ct)));
            byte[] wave = [1, 0, 0, 0];
            var spawnTable = Convert.FromHexString(
                "0100000001000300000001000000F4010000040000002E00440061006F0000000000010000000102");
            Assert.Equal(40, spawnTable.Length);
            await Request(rig.Guest, "gcpReqStartNewWave", wave);
            await Request(rig.Guest, "gcpReqWaveAllSpawnMobs", spawnTable);
            await Request(new Session(999, build), "gcpReqStartNewWave", wave);
            lock (rig.Room.Gate) rig.Room.ModeId = 1;
            await Request(rig.Host, "gcpReqStartNewWave", wave);
            Assert.Empty(rig.Host.Sent);
            Assert.Empty(rig.Guest.Sent);
            lock (rig.Room.Gate) rig.Room.ModeId = checked((ushort)mode);

            await Request(rig.Host, "gcpReqStartNewWave", wave);
            await Request(rig.Host, "gcpReqWaveAllSpawnMobs", spawnTable);
            await Request(rig.Host, "gcpReqEndWave", wave);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Collection(member.Sent,
                    frame => { Assert.Equal(table.S2C("gspRepStartNewWave"), frame.Opcode); Assert.Equal(wave, frame.Body); },
                    frame => { Assert.Equal(table.S2C("gspResetWaveMobs_"), frame.Opcode); Assert.Empty(frame.Body); },
                    frame => { Assert.Equal(table.S2C("gspRepWaveAllSpawnMobs"), frame.Opcode); Assert.Equal(spawnTable, frame.Body); },
                    frame => { Assert.Equal(table.S2C("gspRepEndWave"), frame.Opcode); Assert.Equal(wave, frame.Body); });
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(rig.Host, "gcpReqWaveAllSpawnMobs", spawnTable[..^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(rig.Host, "gcpReqWaveAllSpawnMobs", [.. spawnTable, 0]));
            var excessiveCount = spawnTable[..14];
            Array.Fill(excessiveCount, byte.MaxValue, 10, 4);
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(rig.Host, "gcpReqWaveAllSpawnMobs", excessiveCount));
            Assert.Equal(4, rig.Host.Sent.Count);
            Assert.Equal(4, rig.Guest.Sent.Count);

            await Request(rig.Guest, "gcpReqEndAllWave", []);
            Assert.Equal(4, rig.Host.Sent.Count);
            await Request(rig.Host, "gcpReqEndAllWave", []);
            Assert.Equal(table.S2C("gspRepEndAllWave"), rig.Host.Sent[^1].Opcode);
            Assert.Empty(rig.Host.Sent[^1].Body);
            Assert.Equal(4, rig.Guest.Sent.Count);
            Assert.All(relays, relay => Assert.Empty(relay.Sent));

            rig.Host.Sent.Clear();
            rig.Guest.Sent.Clear();
            lock (rig.Room.Gate)
                Assert.True(rig.Room.TransferMaster(rig.Room.Seats.Single(seat => seat.Game.Id == rig.Guest.Id).Slot));
            await Request(rig.Host, "gcpReqStartNewWave", wave);
            Assert.Empty(rig.Host.Sent);
            Assert.Empty(rig.Guest.Sent);
            await Request(rig.Guest, "gcpReqStartNewWave", wave);
            Assert.Equal(table.S2C("gspRepStartNewWave"), Assert.Single(rig.Host.Sent).Opcode);
            Assert.Equal(table.S2C("gspRepStartNewWave"), Assert.Single(rig.Guest.Sent).Opcode);

            Task Request(ISession session, string name, byte[] body) => services.Get<IPacketRouter>()
                .DispatchAsync(build + "/game", session, new(table.C2S(name), body), default).AsTask();
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoundNpcReported>>());
        Assert.Empty(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
    }
}
