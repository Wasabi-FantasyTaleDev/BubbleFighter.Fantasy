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
    /// <summary>Audited modern NPC operations use current match links, retain ordering and reject stale authority.</summary>
    [Theory]
    [InlineData("kr-1256", false, false, false, false)]
    [InlineData("kr-1256", false, false, false, true)]
    [InlineData("kr-1256", true, false, false, true)]
    [InlineData("kr-1256", true, true, false, false)]
    [InlineData("kr-1256", true, true, false, true)]
    [InlineData("kr-1256", true, true, true, true)]
    [InlineData("cn-774", false, false, false, false)]
    [InlineData("cn-774", false, false, false, true)]
    [InlineData("cn-774", true, false, false, true)]
    [InlineData("cn-774", true, true, false, false)]
    [InlineData("cn-774", true, true, false, true)]
    [InlineData("cn-774", true, true, true, true)]
    [InlineData("tw-454", false, false, false, false)]
    [InlineData("tw-454", false, false, false, true)]
    [InlineData("tw-454", true, false, false, true)]
    [InlineData("tw-454", true, true, false, false)]
    [InlineData("tw-454", true, true, false, true)]
    [InlineData("tw-454", true, true, true, true)]
    public async Task ModernAuditedPirateNpcsReachPeersOnce(string build, bool nativeRelay, bool relayInput, bool guestFallback, bool playing)
    {
        await using var rig = new Rig(build, 20, fallback: !nativeRelay || guestFallback);
        await rig.StartAsync(101);
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        var hostRelay = new Session(101, build);
        var guestRelay = new Session(102, build);
        if (nativeRelay)
        {
            await BindRelay(hostRelay, rig.Host);
            if (guestFallback)
                await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
            else
                await BindRelay(guestRelay, rig.Guest);
        }
        else
        {
            await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(false), default);
            await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(false), default);
            Assert.All(rig.Room.Seats, seat => Assert.Equal(rig.Room.StartGeneration, seat.GameLinkGeneration));
        }
        if (playing)
            lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var root = Path.GetDirectoryName(SharedDialectRows.PacketsDirectory())!;
        var packets = PacketTables.Load(Path.Combine(root, "packets"));
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), rig.Host.Profile);
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
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
            new NpcBinding(build + "/game", game, packets, build, NullLogger.Instance)
                { RelayProtocol = build + "/relay", RelayTable = relay }.Mount(Context("npc"));
            new RoundFeedBinding(build + "/game", build + "/relay", game, relay, packets, build).Mount(Context("feed"));
            var sender = Assert.Single(services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
            var subscriber = Context("round");
            var messages = services.Get<IMessageRouter>().For(subscriber);
            subscriber.Effect(messages.On<RoundNpcReported>(build + "/game",
                (session, report, ct) => rig.Round.HandleNpcAsync(session, report, sender, ct)));
            subscriber.Effect(messages.On<RoundFeedSent>(build + "/game", rig.Round.HandleFeedAsync));
            var router = services.Get<IPacketRouter>();

            // Native create, replay and action bodies are pinned per build by the dialect tests; identities stay byte-exact.
            const int actor = 2300042;
            var creations = new (string Name, byte[] Body)[]
            {
                ("gppCreateAIObject", build == "kr-1256" ? Words(7, 3, 0, 0, actor, 0, 0, 0, 0) : Words(7, 3, 0, 0, actor, 0, 0, 0)),
                ("gppCreateSpawnGroupMemberMob", [.. Words(3, 7, 1), 65, 0, .. Words(0, 0, 0, 0)]),
                ("gppHordeMobReplay", [.. Words(actor, 3, 1), 65, 0, .. Words(1)]),
                ("gppWaveSetMobSpawnGroup", [1, 3, .. Words(7)]),
            };
            var actions = new (string Name, byte[] Body)[]
            {
                ("gppIniPacket", [.. Words(1), 65, 0]),
                ("gppMobLuaMessage", [.. Words(0, 1), 65, 0]),
                ("gppAgentMoveTo", Words(actor, 4, 0, 0, 0)),
                ("gppDummyUnitMotion", [.. Words(actor, 1), 65, 0, .. Words(1)]),
                ("gppUseItemDirect", Words(actor, 1)),
                ("gppDeleteObject", Words(actor)),
            };
            var input = relayInput ? hostRelay : rig.Host;
            var inputOnRelay = relayInput;
            await Request(input, inputOnRelay, "gppCreateMobsFromSpawnGroup", Words(7));
            Assert.Empty(rig.Feed.Sent);
            foreach (var frame in creations)
            {
                await Request(rig.Guest, false, frame.Name, frame.Body);
                await Request(new Session(999, build), false, frame.Name, frame.Body);
                await Request(new Session(998, build), true, frame.Name, frame.Body);
            }
            Assert.Empty(rig.Feed.Sent);
            var originalMode = rig.Room.ModeId;
            lock (rig.Room.Gate) rig.Room.ModeId = 1;
            await Request(input, inputOnRelay, creations[0].Name, creations[0].Body);
            Assert.Empty(rig.Feed.Sent);
            lock (rig.Room.Gate) rig.Room.ModeId = originalMode;
            var target = nativeRelay && !guestFallback ? guestRelay : rig.Guest;
            await Sequence(input, inputOnRelay, target);
            foreach (var frame in creations.Concat(actions))
            {
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(input, inputOnRelay, frame.Name, frame.Body[..^1]));
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(input, inputOnRelay, frame.Name, [.. frame.Body, 0]));
            }
            Assert.Empty(rig.Feed.Sent);
            Assert.Empty(rig.Host.Sent);
            Assert.Empty(rig.Guest.Sent);

            if (nativeRelay)
            {
                var old = hostRelay;
                hostRelay = new Session(103, build);
                await BindRelay(hostRelay, rig.Host);
                Assert.True(old.Connection.IsClosed);
                foreach (var frame in creations)
                    await Request(old, true, frame.Name, frame.Body);
                Assert.Empty(rig.Feed.Sent);
                await Sequence(hostRelay, true, target);
            }
            lock (rig.Room.Gate)
                Assert.True(rig.Room.TransferMaster(rig.Room.Seats.Single(seat => seat.Game.Id == rig.Guest.Id).Slot));
            foreach (var frame in creations)
                await Request(rig.Host, false, frame.Name, frame.Body);
            Assert.Empty(rig.Feed.Sent);
            await Sequence(nativeRelay && !guestFallback ? guestRelay : rig.Guest,
                nativeRelay && !guestFallback, nativeRelay ? hostRelay : rig.Host);
            lock (rig.Room.Gate) rig.Room.MarkResult();
            foreach (var frame in creations.Concat(actions))
                await Request(rig.Guest, false, frame.Name, frame.Body);
            Assert.Empty(rig.Feed.Sent);

            async Task Sequence(Session source, bool onRelay, Session recipient)
            {
                var frames = creations.Concat(actions).ToArray();
                foreach (var frame in frames)
                    await Request(source, onRelay, frame.Name, frame.Body);
                Assert.Equal(frames.Length, rig.Feed.Sent.Count);
                for (var index = 0; index < frames.Length; ++index)
                {
                    var sent = rig.Feed.Sent[index];
                    Assert.Equal(recipient.Id, sent.Session);
                    Assert.Equal(relay.S2C(frames[index].Name), sent.Message.Code);
                    Assert.Equal(frames[index].Body, sent.Message.Payload.ToArray());
                }
                rig.Feed.Sent.Clear();
            }
            Task Request(ISession session, bool onRelay, string name, byte[] body) => router.DispatchAsync(
                build + (onRelay ? "/relay" : "/game"), session,
                new((onRelay ? relay : game).C2S(name), body), default).AsTask();
        }
        finally
        {
            foreach (var context in contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
        }
        async Task BindRelay(Session match, Session member)
        {
            var offer = rig.Offers.Sent.Single(entry => entry.Session == member.Id).Message;
            var binding = await rig.Registry.BindCapabilityAsync(match, new(offer.Ticket), default);
            rig.Registry.MarkInitialized(match, binding);
            await rig.Round.HandleMatchConnectionReportedAsync(member, new(true), default);
        }
        static byte[] Words(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    }
}
