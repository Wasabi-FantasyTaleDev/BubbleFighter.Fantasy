using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
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
    /// <summary>Before stage entry, the Psionic strategy's account lookup can find each player's own selected character.</summary>
    [Theory]
    [InlineData("kr-1256", 37)]
    [InlineData("cn-774", 37)]
    [InlineData("kr-1256", 3)]
    public async Task PsionicStartRestatesEachCharactersAccountEquipment(string build, int mode)
    {
        var host = new Session(703, build);
        var guest = new Session(904, build);
        RoomMemberView[] seats = [new(0, 703, "host", 1, 1, true, 300001, [300001, 203100]),
            new(3, 904, "guest", 1, 2, true, 300002, [300002, 203101])];
        var room = new RoomView(12, "two characters", mode, "arena", 8, 0, false, [], seats);
        var directory = SharedDialectRows.PacketsDirectory();
        var packets = PacketTables.Load(directory);
        var game = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(directory)!.FullName, "profiles", build,
            "opcodes.yaml"), host.Profile).ForConnection(OpcodeConnection.Game);
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var context = new PluginContext(new PluginRow { Id = "psionic-start", Plugin = "test", Source = "test" },
            services, events, NullLogger.Instance);
        try
        {
            await new PacketRouterPlugin().ActivateAsync(context, default);
            await new MessageRouterPlugin().ActivateAsync(context, default);
            var sender = new RoundStartBinding(build + "/game", game, packets, build, [1, 0, 1], NullLogger.Instance);
            sender.Mount(context);
            foreach (var recipient in new[] { host, guest })
            {
                await sender.SendAsync(recipient, new RoomMatchStarting(room, "arena"), default);
                Assert.Equal(new[] { game.S2C("gspRoomSetSlot"), game.S2C("gspRoomSetSlot"),
                    game.S2C("gspSessionSignatureData"), game.S2C("gspRoomEnterGame") }, recipient.Sent.Select(frame => frame.Opcode));
                foreach (var (seat, index) in seats.Select((seat, index) => (seat, index)))
                {
                    var frame = PacketCodec.Read(packets, build, "RoomSeatUpdate", recipient.Sent[index].Body);
                    Assert.Equal(seat.Slot, frame["slot"]);
                    if (build == "cn-774")
                    {
                        var profile = Record(frame["equipment"]);
                        Assert.Equal((seat.UserNum, seat.CharacterItemId), Identity(profile));
                        Assert.Equal(-1, profile["unknown_16"]);
                        continue;
                    }
                    var slotMap = Records(frame["profile_entries"]).Select(Identity).ToArray();
                    var accountMap = Records(frame["unknown_n3"]).Select(Identity).ToArray();
                    if (mode == 37)
                    {
                        Assert.Equal(seats.Select(s => (s.Slot, s.CharacterItemId)), slotMap);
                        Assert.Equal(seats.Select(s => (s.UserNum, s.CharacterItemId)), accountMap);
                    }
                    else
                    {
                        Assert.Equal(seats.Select(s => (s.UserNum, s.CharacterItemId)), slotMap);
                        Assert.Empty(accountMap);
                    }
                }
            }
        }
        finally { await context.UnwindAsync(); }

        static IReadOnlyDictionary<string, object?> Record(object? value) =>
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value);
        static IEnumerable<IReadOnlyDictionary<string, object?>> Records(object? value) =>
            Assert.IsAssignableFrom<IReadOnlyList<object?>>(value).Select(Record);
        static (int, int) Identity(IReadOnlyDictionary<string, object?> profile) =>
            (Assert.IsType<int>(profile["user_number"]), Assert.IsType<int>(profile["character_id"]));
    }

    /// <summary>A Psionic player can report their own direct item to the other seat, even when they are not the host.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    public async Task PsionicMemberDirectItemReachesOnlyTheOtherPlayer(string build)
    {
        await using var rig = new Rig(build, 20, fallback: true);
        await rig.StartAsync(37);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(member);
            await rig.Round.HandleMatchConnectionReportedAsync(member, new(false), default);
        }
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        var directory = SharedDialectRows.PacketsDirectory();
        var table = OpcodeTableLoader.Load(Path.Combine(Directory.GetParent(directory)!.FullName, "profiles", build,
            "opcodes.yaml"), rig.Guest.Profile);
        var game = table.ForConnection(OpcodeConnection.Game);
        var relay = table.ForConnection(OpcodeConnection.Relay);
        var packets = PacketTables.Load(directory);
        const string name = "gppUseItemDirect";
        var route = Assert.Single(RoundFeedRoute.Load(packets, build, game, relay), r => r.GameInbound == game.C2S(name));
        Assert.Equal(relay.C2S(name), route.RelayInbound);
        var guestActor = RoomRegistry.SceneEntity(rig.Room.Seats.Single(s => s.Game.Id == rig.Guest.Id).Slot);
        // kr-1256 sub_6FE730 / cn-774 sub_8BEC00: the two words are the using character and the item.
        var body = BitConverter.GetBytes(guestActor).Concat(BitConverter.GetBytes(300)).ToArray();
        var feed = route.Read(packets, build, body).Feed with { OnRelay = true };
        await rig.Round.HandleFeedAsync(rig.Guest, feed, default);
        var received = Assert.Single(rig.Feed.Sent);
        Assert.Equal(rig.Host.Id, received.Session);
        Assert.Equal(relay.S2C(name), received.Message.Code);
        Assert.Equal(body, received.Message.Payload.ToArray());
        await rig.Round.HandleFeedAsync(rig.Host, feed, default);
        Assert.Single(rig.Feed.Sent);
    }
}
