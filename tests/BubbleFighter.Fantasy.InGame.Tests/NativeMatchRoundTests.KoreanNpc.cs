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
    /// <summary>Concrete Korean NPC frames retain order and identity across both transports and a master change.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KoreanPirateNpcFeedDoesNotDuplicateGroupExpansion(bool nativeRelay, bool relayInput)
    {
        const string build = "kr-812";
        await using var rig = new Rig(build, 20, enabled: nativeRelay);
        await rig.StartAsync(101);
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        var hostRelay = new Session(101, build);
        var guestRelay = new Session(102, build);
        if (nativeRelay)
        {
            foreach (var (relay, game) in new[] { (hostRelay, rig.Host), (guestRelay, rig.Guest) })
            {
                var offer = rig.Offers.Sent.Single(entry => entry.Session == game.Id).Message;
                var binding = await rig.Registry.BindCapabilityAsync(relay, new(offer.Ticket), default);
                rig.Registry.MarkInitialized(relay, binding);
                await rig.Round.HandleMatchConnectionReportedAsync(game, new(true), default);
            }
        }
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var root = Path.GetDirectoryName(SharedDialectRows.PacketsDirectory())!;
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), rig.Host.Profile);
        var (schema, play) = RoomTables.Load(packets).Of(build);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test" }, services,
                events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("packets"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var tables = new NpcTables(table);
            var codec = new PlayRoomCodec(build + "/game", build, schema, play, tables, NullLogger.Instance, packets)
                { SelectMatchRelay = nativeRelay };
            codec.Mount(Context("dialect"));
            var subscriber = Context("round");
            var messages = services.Get<IMessageRouter>().For(subscriber);
            subscriber.Effect(messages.On<RoundFeedSent>(build + "/game", rig.Round.HandleFeedAsync));
            subscriber.Effect(messages.On<RoundNpcReported>(build + "/game",
                (session, report, ct) => rig.Round.HandleNpcAsync(session, report, codec, ct)));
            using var tutorial = new TutorialHandler(packets, build, schema, play.Tutorial, play.MatchStart,
                play.Wire, tables, NullLogger.Instance, messages: services.Get<IMessageRouter>());
            var route = Context("npc-route");
            var router = services.Get<IPacketRouter>();
            Register("gppCreateMobsFromSpawnGroup", tutorial.HandleCreateMobsFromSpawnGroupAsync);
            Register("gppCreateSpawnGroupMemberMob", tutorial.HandleCreateSpawnGroupMemberMobAsync);
            Register("gppCreateAIObject", tutorial.HandleCreateAIObjectAsync);
            Register("gppLifePoint", tutorial.HandleLifePointAsync);
            Register("gcpReqWaveAllSpawnMobs", tutorial.HandleWaveAllSpawnMobsAsync);

            var wave = Convert.FromHexString("010000000100030000000400000000000000040000002E00440061006F0000000000000000000101F4010000040000002E00440061006F0000000000000000000102E8030000040000002E00440061006F0000000000000000000103DC050000040000002E00440061006F0000000000000000000104");
            await router.DispatchAsync(build + "/game", rig.Host, new(table.C2S("gcpReqWaveAllSpawnMobs"), wave), default);
            Assert.Equal(2, rig.Host.Sent.Count);
            Assert.Equal(2, rig.Guest.Sent.Count);
            Assert.Empty(hostRelay.Sent);
            Assert.Empty(guestRelay.Sent);
            rig.Host.Sent.Clear();
            rig.Guest.Sent.Clear();

            // kr-812 sub_647CD0 / kr-812 sub_6E1930 create; kr-812 sub_5103B0 sets life; kr-812 sub_A225A0 replays.
            // kr-812 sub_5285E0 / kr-812 sub_524F90 act; kr-812 sub_511380 / kr-812 sub_50EB50 clean and delete.
            const int actor = 2300042;
            var frames = new (string Name, byte[] Body)[]
            {
                ("gppCreateAIObject", Words(7, 3, 0, 0, actor, 0, 0, 0, 0)),
                ("gppCreateSpawnGroupMemberMob", [.. Words(3, 7, 1), 65, 0, .. Words(0, 0, 0, 0)]),
                ("gppLifePoint", Words(actor, 2, 1)),
                ("gppHordeMobReplay", [.. Words(actor, 3, 1), 65, 0, .. Words(1)]),
                ("gppMobLuaMessage", [.. Words(actor, 1), 65, 0]),
                ("gppAgentMoveTo", Words(actor, 4, 0, 0, 0)),
                ("gppDummyUnitMotion", [.. Words(actor, 1), 65, 0, .. Words(1)]),
                ("gppDead", Words(actor)),
                ("gppDeleteObject", Words(actor)),
            };
            await SendSequence(rig.Host, hostRelay, nativeRelay ? guestRelay.Id : rig.Guest.Id);
            lock (rig.Room.Gate)
                Assert.True(rig.Room.TransferMaster(rig.Room.Seats.Single(seat => seat.Game.Id == rig.Guest.Id).Slot));
            await SendSequence(rig.Guest, guestRelay, nativeRelay ? hostRelay.Id : rig.Host.Id);

            // An NPC identity belongs to no player seat: legitimate non-master reports are still relayed.
            rig.Feed.Sent.Clear();
            await router.DispatchAsync(build + "/game", rig.Host, new(table.C2S("gppDead"), Words(actor)), default);
            Assert.Equal(nativeRelay ? guestRelay.Id : rig.Guest.Id, Assert.Single(rig.Feed.Sent).Session);
            rig.Feed.Sent.Clear();
            lock (rig.Room.Gate) rig.Room.MarkResult();
            await router.DispatchAsync(build + "/game", rig.Host, new(table.C2S("gppDead"), Words(actor)), default);
            Assert.Empty(rig.Feed.Sent);

            async Task SendSequence(Session game, Session relay, long recipient)
            {
                rig.Feed.Sent.Clear();
                var protocol = build + (relayInput ? "/relay" : "/game");
                var input = relayInput ? relay : game;
                await router.DispatchAsync(protocol, input, new(table.C2S("gppCreateMobsFromSpawnGroup"), Words(7)), default);
                Assert.Empty(rig.Feed.Sent);
                Assert.Empty(rig.Host.Sent);
                Assert.Empty(rig.Guest.Sent);
                foreach (var frame in frames)
                    await router.DispatchAsync(protocol, input, new(table.C2S(frame.Name), frame.Body), default);
                Assert.Equal(frames.Length, rig.Feed.Sent.Count);
                for (var index = 0; index < frames.Length; ++index)
                {
                    var sent = rig.Feed.Sent[index];
                    Assert.Equal(recipient, sent.Session);
                    Assert.Equal(table.S2C(frames[index].Name), sent.Message.Code);
                    Assert.Equal(frames[index].Body, sent.Message.Payload.ToArray());
                }
            }

            void Register(string name, PacketHandler handler) =>
                route.Effect(router.Register(build + "/game", table.C2S(name), handler));
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
        static byte[] Words(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    }
}
