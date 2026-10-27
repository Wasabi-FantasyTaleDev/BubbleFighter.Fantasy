using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>All modern plaza dialects drive the same ownership flow while retaining their native containers.</summary>
public sealed class ModernPlazaCompatibilityTests
{
    private static readonly string Root = FindRoot();
    private static readonly PacketTables Tables = PacketTables.Load(Path.Combine(Root, "packets"));

    /// <summary>CN and TW enter, observe movement, leave and refill a seat through mounted shared handlers.</summary>
    [Theory]
    [InlineData("cn-774", 117, 116)]
    [InlineData("tw-454", 115, 114)]
    public async Task NativePlazaRoutesWorkWithoutUnauditedRoundBindings(string build, ushort installOpcode, ushort lobbyOpcode)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var contexts = new List<PluginContext>();
        PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test", Config = config },
                services, events, NullLogger.Instance);
            contexts.Add(context);
            return context;
        }
        var protocol = build + "/game";
        try
        {
            await new PacketRouterPlugin().ActivateAsync(Context("packets"), default);
            await new MessageRouterPlugin().ActivateAsync(Context("messages"), default);
            var table = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
            new PlazaSceneBinding(protocol, table, Tables, build, NullLogger.Instance).Mount(Context("plaza"));
            new RoomChatBinding(protocol, table, Tables, build, NullLogger.Instance).Mount(Context("chat"));
            await new InGamePlugin().ActivateAsync(Context("ingame", new()
            {
                ["game_protocol"] = protocol, ["relay_protocol"] = build + "/relay",
                ["plaza_enter_requires_finished_login"] = true,
                ["plaza_echo_positions"] = false, ["plaza_replay_positions_on_enter"] = false,
            }), default);
            var owner = Player(build, 101, 11, "owner");
            var guest = Player(build, 102, 22, "guest");
            var router = services.Get<IPacketRouter>();
            async Task Dispatch(RecordingSession player, string name, byte[] body) =>
                await router.DispatchAsync(protocol, player, new(table.C2S(name), body), default);
            await Dispatch(owner, "gcpReqGotoLobby", []);
            Assert.Equal(new ushort[] { (ushort)installOpcode, 201, (ushort)lobbyOpcode }, owner.Opcodes);
            Assert.Equal(SessionPhase.Plaza, owner.Features.Get<IPlayerSession>().Phase);
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(owner.Body(installOpcode).AsSpan(24)));
            Assert.Equal(4, owner.Body(lobbyOpcode).Length);
            var install = PacketCodec.Read(Tables, build, "gspGotoRoom", owner.Body(installOpcode));
            Assert.Equal(0, install["local_slot"]);
            Assert.False(install.ContainsKey("profile_entries"));
            var appearance = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["equipped_objects"])));
            Assert.Equal(11, appearance["user_number"]);
            Assert.Equal(300001, appearance["character_id"]);
            var party = PacketCodec.Read(Tables, build, "SoloPartySnapshot", owner.Body(201));
            Assert.Equal(2, party["state"]);
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(party["profile_entries"]));

            await Dispatch(guest, "gcpReqGotoLobby", []);
            var guestInstall = PacketCodec.Read(Tables, build, "gspGotoRoom", guest.Body(installOpcode));
            Assert.Equal(1, guestInstall["local_slot"]);
            var arrival = PacketCodec.Read(Tables, build, "RoomSeatLeave", owner.Body(511));
            Assert.Equal(1, arrival["slot"]);
            var arrivalUser = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(arrival["user"]);
            Assert.Equal(true, arrivalUser["occupied"]);
            Assert.Equal(22, arrivalUser["user_number"]);
            Assert.Equal(1, arrivalUser["character_type"]);
            Assert.Equal(-1, arrivalUser["party_marker_index"]);
            var outsider = Player(build, 103, 33, "outsider");
            await Dispatch(guest, "gcpRoomSendChatModuleMsg", Convert.FromHexString("00000000020000006800690000000000"));
            var chat = Convert.FromHexString("010005000000670075006500730074000000000000000000000000000200000068006900FFFFFFFF000000000000000000000000"
                + (build == "cn-774" ? "00000000" : ""));
            Assert.Equal(chat, owner.Body(509));
            Assert.Equal(chat, guest.Body(509));
            Assert.DoesNotContain((ushort)509, outsider.Opcodes);
            var upload = new byte[] { 77, 0, 0, 0, 0, 0, 192, 63, 0, 0, 32, 64, 0, 0, 96, 64, 19, 41, 0, 0, 240, 64 };
            Assert.Equal(22, upload.Length);
            await Dispatch(guest, "gcpSendPartyChrPos", upload);
            Assert.Equal(new byte[] { 22, 0, 0, 0 }.Concat(upload), owner.Body(301));
            Assert.Equal(26, owner.Body(301).Length);
            await Dispatch(owner, "gcpReqGotoMenu", []);
            Assert.Equal(SessionPhase.MainMenu, owner.Features.Get<IPlayerSession>().Phase);
            var departure = PacketCodec.Read(Tables, build, "RoomSeatLeave", guest.Body(511));
            Assert.Equal(0, departure["slot"]);
            Assert.Equal(false, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(departure["user"])["occupied"]);
            await Dispatch(owner, "gcpReqGotoLobby", []);
            var reentry = PacketCodec.Read(Tables, build, "gspGotoRoom", owner.Body(installOpcode));
            Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<object?>>(reentry["user_slots"]).Count);
        }
        finally
        {
            for (var index = contexts.Count - 1; index >= 0; index--)
                contexts[index].Unwind();
        }
    }

    /// <summary>Native unkeyed appearances preserve worn items and keep gaps distinct from occupied seats.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativePlazaInstallPreservesWornItemsAndSeatHoles(string build)
    {
        RoomMemberView[] members = [new(2, 11, "owner", 17, 0, false, 300025, [300025, 201001, 203003])];
        var body = PlazaRecords.Install(Tables, build, members, 2, 3, false);
        var decoded = PacketCodec.Read(Tables, build, "gspGotoRoom", body);
        Assert.Equal(2, decoded["local_slot"]);
        var seats = Assert.IsAssignableFrom<IReadOnlyList<object?>>(decoded["user_slots"]);
        Assert.Equal(3, seats.Count);
        Assert.Equal(false, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(seats[0])["occupied"]);
        var local = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(seats[2]);
        Assert.Equal(true, local["occupied"]);
        Assert.Equal(25, local["character_type"]);
        // cn-774 sub_894B80 hands seat +36 to the actor constructor; a zero there is not an actor kind.
        Assert.Equal(1, Convert.ToInt32(local["unknown_36"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(17, local["level"]);
        Assert.Equal(-1, local["party_marker_index"]);
        // cn-774 sub_52E090 / tw-454 sub_791C10 treat a controller above -1 as a bot seat owned by that account; a
        // player seat that carried 0 there left the local actor uncontrolled (it could turn but not act).
        Assert.Equal(-1, local["ai_controller_user_number"]);
        var menu =PacketCodec.Read(Tables, build, "gspGotoRoom", MainMenuInstall.Write(Tables, build, 11, "owner", 17, 300025));
        var menuSeat = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(menu["user_slots"])));
        Assert.Equal(-1, menuSeat["party_marker_index"]);
        var marked = PlazaRecords.Seat(Tables, build, members[0]);
        marked["party_marker_index"] = 0;
        var markedSeat = PacketCodec.Read(Tables, build, "UserSlot", PacketCodec.Write(Tables, build, "UserSlot", marked));
        Assert.Equal(0, markedSeat["party_marker_index"]);
        var appearance = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(decoded["equipped_objects"])));
        Assert.Equal(new object?[] { 201001, 203003 }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(appearance["equipped_items"]));
        Assert.Throws<ArgumentException>(() => PlazaRecords.Install(Tables, build, members, 0, 3, false));
    }

    /// <summary>The seat's social object is 48 bytes for cn-774 (sub_49DED0) and 40 for tw-454 (sub_48DBE0).</summary>
    [Theory]
    [InlineData("cn-774", 48)]
    [InlineData("tw-454", 40)]
    public void SeatSocialInfoMatchesEachBuildsReader(string build, int length) =>
        Assert.Equal(length, PacketCodec.Write(Tables, build, "SocialSystemInfo", new Dictionary<string, object?>()).Length);

    /// <summary>tw-454 sub_48BD60 consumes 204 bytes for this seat; surplus bytes shift the following equipment object.</summary>
    [Fact]
    public void TaiwanSeatHasTheReadersExactLength()
    {
        RoomMemberView seat = new(0, 12, "owner", 1, 1, false, 300001, [300001, 201001]);
        Assert.Equal(204, PacketCodec.Write(Tables, "tw-454", "UserSlot", PlazaRecords.Seat(Tables, "tw-454", seat)).Length);
    }

    private static RecordingSession Player(string build, long id, int user, string name)
    {
        var player = new RecordingSession(id, TestWireDialects.Get(build).Profile);
        player.SetLobbyPlayer(user, name);
        return player;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
