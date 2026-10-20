using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Mounted modern requests must reach shared room ownership and retain native recipient sequences.</summary>
public sealed class ModernRoomBindingTests
{
    /// <summary>The native round-map selector uses shared authority and broadcasts only the updated settings block.</summary>
    [Fact]
    public async Task RoundMapSelectionUpdatesItsPairWithoutReinstallingTheRoom()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var count = harness.Host.Opcodes.Count();
        var guestCount = harness.Guest.Opcodes.Count();
        var body = Raw(writer => { writer.WriteInt32(1); writer.WriteWideStringPrefixed("first"); writer.WriteWideStringPrefixed("second"); });
        await harness.Dispatch(harness.Guest, 514, body);
        Assert.Equal(count, harness.Host.Opcodes.Count());
        await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 514, body[..^1]));
        await harness.Dispatch(harness.Host, 514, body);
        Assert.Equal(new ushort[] { 505 }, harness.Host.Opcodes.Skip(count));
        Assert.Equal(new ushort[] { 505 }, harness.Guest.Opcodes.Skip(guestCount));
        var expected = Source().Map with { Unknown0 = 0, Unknown8 = 0, Unknown20 = 0, Unknown192 = "second", Unknown216 = "first" };
        Assert.Equal(Raw(expected.Write), harness.Host.Body(505));
        Assert.Equal(harness.Host.Body(505), harness.Guest.Body(505));
        var selection = Assert.Single(harness.Room.BuildSettings!.RoundSelections);
        Assert.Equal(1, selection.Index);
        Assert.Equal(body, selection.Bytes.ToArray());
    }

    /// <summary>Every scene keeps the ordered party while refreshing only the recipient's own appearance.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task SceneInstallsPreservePartyOrderAndRecipientAppearance(int scene, bool follower)
    {
        using var harness = await Harness.CreateAsync();
        var recipient = follower ? harness.Guest : harness.Host;
        var self = new RoomMemberView(2, follower ? 22 : 11, follower ? "guest" : "host", 4, 1, false,
            300012, [300012, 202001]);
        var party = new PartySnapshot([new(11, "host"), new(22, "guest")]);
        using var registration = recipient.RegisterFeature<IPartyMembership>(new Membership(party));
        await SendSceneAsync();
        var joined = PacketCodec.Read(Tables, "kr-1256", "SoloPartySnapshot", recipient.Body(201));
        var members = Assert.IsAssignableFrom<IReadOnlyList<object?>>(joined["members"]);
        Assert.Equal(new[] { 11, 22 }, members.Select(row => Convert.ToInt32(((IReadOnlyDictionary<string, object?>)row!)["user_number"])));
        var profiles = Assert.IsAssignableFrom<IReadOnlyList<object?>>(joined["profile_entries"]);
        var profile = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(profiles));
        Assert.Equal(self.UserNum, Convert.ToInt32(profile["user_number"]));
        Assert.Equal(300012, Convert.ToInt32(profile["character_id"]));

        registration.Dispose();
        await SendSceneAsync();
        var solo = PacketCodec.Read(Tables, "kr-1256", "SoloPartySnapshot", recipient.Body(201));
        var soloMembers = Assert.IsAssignableFrom<IReadOnlyList<object?>>(solo["members"]);
        var remaining = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(soloMembers));
        Assert.Equal(self.UserNum, Convert.ToInt32(remaining["user_number"]));
        joined["members"] = solo["members"];
        Assert.Equal(PacketCodec.Write(Tables, "kr-1256", "SoloPartySnapshot", joined), recipient.Body(201));

        async ValueTask SendSceneAsync()
        {
            var member = new PlazaMember(self.Slot, self.UserNum, self.Nickname, self.Level,
                self.CharacterItemId, self.EquippedItemIds);
            if (scene == 1)
                await harness.Sender<PlazaMenuReturnRequested>().SendAsync(recipient, new() { You = member }, default);
            else if (scene == 3)
                await harness.Sender<PlazaEntered>().SendAsync(recipient, new(member, []), default);
            else
            {
                var settings = ModernRoomProjection.Create(Tables, "kr-1256", CreateBody()).BuildSettings;
                var room = new RoomView(1, "party", 3, "rel_Map", 4, self.Slot, true, [], [self], settings);
                await harness.Sender<RoomEntered>().SendAsync(recipient, new(room, self.Slot), default);
            }
        }
    }

    /// <summary>The native delegation request reaches shared ownership, acknowledges, then announces the new master.</summary>
    [Fact]
    public async Task MasterTransferUsesNativeReplyAndExistingMasterAnnouncement()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest",
            new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var hostCount = harness.Host.Opcodes.Count();
        var guestCount = harness.Guest.Opcodes.Count();
        var request = Raw(writer => writer.WriteWideStringPrefixed("guest"));
        await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 508, request[..^1]));
        Assert.Equal(0, harness.Room.MasterSlot);
        await harness.Dispatch(harness.Host, 508, request);
        Assert.Equal(1, harness.Room.MasterSlot);
        Assert.Equal(new ushort[] { 507, 502 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 502 }, harness.Guest.Opcodes.Skip(guestCount));
        Assert.Equal(Raw(writer => { writer.WriteWideStringPrefixed("guest"); writer.WriteInt32(0); }), harness.Host.Body(507));
        Assert.Equal(GameToClient.MasterChanged(1), harness.Guest.Body(502));

        hostCount = harness.Host.Opcodes.Count();
        guestCount = harness.Guest.Opcodes.Count();
        await harness.Dispatch(harness.Host, 508, Raw(writer => writer.WriteWideStringPrefixed("host")));
        Assert.Equal(new ushort[] { 507 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(guestCount, harness.Guest.Opcodes.Count());
        Assert.Equal(Raw(writer => { writer.WriteWideStringPrefixed("host"); writer.WriteInt32(2); }), harness.Host.Body(507));
        Assert.Equal(1, harness.Room.MasterSlot);
    }

    private sealed record Membership(PartySnapshot Party) : IPartyMembership;

    /// <summary>A committed mode refresh uses the native settings notification without reinstalling any scene or member.</summary>
    [Fact]
    public async Task SettingsNotificationPreservesCreatorOptionsAndUsesCommittedMode()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var source = ModernRoomProjection.Create(Tables, "kr-1256", CreateBody()).BuildSettings!;
        var original = source.Bytes.ToArray();
        var room = new RoomView(harness.Room.Id, "renamed", 4, "rel_Map", 4, 0, true, [], [], source);
        var count = harness.Host.Opcodes.Count();
        await harness.Sender<RoomSettingsChanged>().SendAsync(harness.Host, new(room), default);
        Assert.Equal(new ushort[] { 505 }, harness.Host.Opcodes.Skip(count));
        var expected = Unchosen(Source().Map with { Unknown0 = 0, Unknown4 = 4, Unknown8 = 0, Unknown20 = 0 });
        Assert.Equal(Raw(expected.Write), harness.Host.Body(505));
        Assert.Equal(original, source.Bytes.ToArray());
        Assert.Equal(Raw(Source().Write), source.SessionPrefix.ToArray());

        count = harness.Host.Opcodes.Count();
        var malformed = room with { BuildSettings = source with { Change = new byte[] { 1 } } };
        await Assert.ThrowsAsync<PacketFormatException>(() =>
            harness.Sender<RoomSettingsChanged>().SendAsync(harness.Host, new(malformed), default).AsTask());
        Assert.Equal(count, harness.Host.Opcodes.Count());
    }

    /// <summary>
    /// The master's map change reaches every member as the room's GameSetting: a pirate mission stays in every map
    /// string, the display name goes beside it, and the room keeps the mission as its map.
    /// </summary>
    [Fact]
    public async Task MapChangeShowsTheMissionToEveryMember()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest",
            new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var (hostCount, guestCount) = (harness.Host.Opcodes.Count(), harness.Guest.Opcodes.Count());
        var change = Request("RoomGameSettingChangeRequest", new()
        {
            ["unknown_0"] = "riverside", ["unknown_24"] = "rel_Village07_Easy02", ["unknown_48"] = "npc_Village07_Easy01",
            ["mode"] = 101,
        });
        await harness.Dispatch(harness.Host, 513, change);
        Assert.Equal(new ushort[] { 505 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 505 }, harness.Guest.Opcodes.Skip(guestCount));
        var shown = PacketCodec.Read(Tables, "kr-1256", "GameSetting", harness.Guest.Body(505));
        Assert.Equal(("npc_Village07_Easy01", "npc_Village07_Easy01", "npc_Village07_Easy01", "riverside"),
            ((string)shown["unknown_44"]!, (string)shown["unknown_68"]!, (string)shown["unknown_92"]!, (string)shown["unknown_116"]!));
        Assert.Equal(101, Convert.ToInt32(shown["mode"]));
        Assert.Equal(("npc_Village07_Easy01", 101), (harness.Room.MapKey, harness.Room.ModeId));
    }

    /// <summary>Room, plaza and menu typed entries retain the group notice in the complete native install.</summary>
    [Fact]
    public async Task TypedSceneEntriesCarryThePartyNotice()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var room = harness.Room;
        var binding = harness.Sender<RoomEntered>();
        var view = new RoomView(room.Id, "room", 3, "rel_Map", 4, 0, true, [],
            [new(0, 11, "host", 1, 1, false, 300001, [300001])], room.BuildSettings);
        await binding.SendAsync(harness.Host, new(view, 0) { PartyTransition = true }, default);
        AssertNotice();
        var member = new PlazaMember(2, 11, "host", 1, 300001, [300001]);
        await harness.Sender<PlazaEntered>().SendAsync(harness.Host, new(member, []) { PartyTransition = true }, default);
        AssertNotice();
        await harness.Sender<PlazaMenuReturnRequested>().SendAsync(harness.Host, new() { You = member, PartyTransition = true }, default);
        AssertNotice();
        void AssertNotice() => Assert.Equal(new byte[] { 1, 0, 0, 0 }, harness.Host.Body(117)[^4..]);
    }

    private static readonly string Root = FindRoot();
    private static readonly PacketTables Tables = PacketTables.Load(Path.Combine(Root, "packets"));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static byte[] Raw(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    private static SessionSettingBlob Source() => SessionSettingBlob.ForRoomListRow("room", "host", 3, 7, 1, 4, 89, true) with
    {
        Map = MapMetaBlob.ForTrainingRoom(3, "rel_Map") with { Unknown0 = 23, Unknown8 = 1, Unknown20 = 7 },
        Unknown80 = 12345678901234,
        Unknown88 = "opaque",
    };

    // Until the master picks a round map, the startup map the create carried shows as the all-random entry
    // (builds.yaml room_unchosen_map).
    private static MapMetaBlob Unchosen(MapMetaBlob map) => map with
    {
        Unknown44 = "Random", Unknown68 = "", Unknown92 = "", Unknown116 = "",
        Unknown144 = "Random", Unknown168 = "", Unknown192 = "Random", Unknown216 = "",
        Unknown240 = "Random", Unknown264 = "",
    };

    private static byte[] CreateBody() => Raw(writer =>
    {
        Source().Write(writer);
        writer.WriteWideStringPrefixed("pw");
        writer.WriteByte(0xBA);
    });

    private static byte[] Request(string structure, Dictionary<string, object?> fields) =>
        PacketCodec.Write(Tables, "kr-1256", structure, fields);

    private static void Parity(string structure, byte[] raw, byte[] actual)
    {
        // kr-1256 sub_1143710 canonicalizes occupancy truth; all other native fields retain their values.
        Assert.Equal(raw.Length, actual.Length);
        Assert.Equal(PacketCodec.Write(Tables, "kr-1256", structure, PacketCodec.Read(Tables, "kr-1256", structure, raw)), actual);
    }

    /// <summary>The creator's opaque fields survive while ordinary-room gate words and identity are overlaid.</summary>
    [Fact]
    public void CreateProjectionRetainsNativeSettingsWithoutUsingTheClientRoomId()
    {
        var request = ModernRoomProjection.Create(Tables, "kr-1256", CreateBody());
        Assert.Equal(("room", "pw", 3, "rel_Map"), (request.Name, request.Password, request.ModeId, request.MapKey));
        Assert.True(request.UpdateExistingTitle);
        var source = Assert.IsType<RoomBuildSettings>(request.BuildSettings);
        Assert.Equal(Raw(Source().Write), source.SessionPrefix.ToArray());
        var room = new RoomView(12, "renamed", 4, "rel_Map", 4, 0, true, [], [], source);
        var actual = PacketCodec.Write(Tables, "kr-1256", "SessionSetting", ModernRoomProjection.Session(Tables, "kr-1256", room));
        var expected = SessionSettingBlob.ForRoom(Source(), 2) with
        {
            Unknown0 = 12, Unknown8 = "renamed", Map = Unchosen(MapMetaBlob.ForRoom(Source().Map) with { Unknown4 = 4 }),
        };
        Assert.Equal(Raw(expected.Write), actual);
        Assert.Throws<InvalidOperationException>(() => ModernRoomProjection.Session(Tables, "kr-1256", room with { BuildSettings = null }));
        Assert.Throws<InvalidOperationException>(() => ModernRoomProjection.Session(Tables, "kr-1256", room with { BuildSettings = source with { Lineage = "kr-812" } }));
    }

    /// <summary>Two native requests install only the entrant and announce the newcomer to existing occupants.</summary>
    [Fact]
    public async Task CreateAndJoinKeepTheNativeBurstAndEquippedViewRecipients()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        Assert.Equal(new ushort[] { 117, 501, 201, 806, 503 }, harness.Host.Opcodes);
        var room = harness.Room;
        var baseline = SessionSettingBlob.ForRoom(Source(), 2);
        var setting = baseline with { Unknown0 = checked((ushort)room.Id), Map = Unchosen(baseline.Map) };
        Assert.Equal(Raw(setting.Write), harness.Host.Body(501));
        Assert.Equal(RoomSessionBlob.UpdatePartySolo(11, "host", 300001, []), harness.Host.Body(201));
        Assert.Equal(new[] { (101L, 11, false) }, harness.Views.Sent);
        var hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = room.Id, ["password"] = "pw" }));
        Assert.Equal(new ushort[] { 128, 117, 501, 201, 806, 806, 503, 503, 118 }, harness.Guest.Opcodes);
        Assert.Equal(new ushort[] { 503, 806, 118 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new[] { (101L, 11, false), (102L, 22, false), (102L, 11, true), (101L, 22, true) }, harness.Views.Sent);
        Assert.Equal(GameToClient.WaitingDialog(0, 0), harness.Guest.Body(128));
        var profiles = room.Seats.Select((seat, index) => new ProfileMapEntry(seat.UserNum, seat.CharacterId, index, EquippedItemIds: seat.EquippedItemIds ?? [])).ToArray();
        Parity("gspGotoRoom", RoomSessionBlob.GotoRoom(setting, room.Seats.Select(seat => seat.ToIdentity()).ToArray(), profiles, localSlot: 1), harness.Guest.Body(117));
        var guestRecords = harness.Guest.Bodies(503);
        Parity("RoomSeatUpdate", GameToClient.CreateRoomReply(1, room.Seats[1].ToPlayerInfo(), profiles), guestRecords[0]);
        Parity("RoomSeatUpdate", GameToClient.CreateRoomReply(0, room.Seats[0].ToPlayerInfo(), profiles), guestRecords[1]);
        Parity("RoomSeatUpdate", GameToClient.CreateRoomReply(1, room.Seats[1].ToPlayerInfo(), profiles), harness.Host.Body(503));
    }

    /// <summary>Presence, team, chat, seat locks and refusal use native payloads without reinstalling the room.</summary>
    [Fact]
    public async Task WaitingRoomChangesAndRefusalsUseTheSharedState()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "wrong" }));
        Assert.Equal(new ushort[] { 103 }, harness.Guest.Opcodes);
        Assert.Equal(GameToClient.JoinResult(4), harness.Guest.Body(103));
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var installs = harness.Host.Bodies(117).Length;
        await harness.Dispatch(harness.Guest, 505, Request("RoomReadyRequest", new() { ["unknown_5"] = true }));
        Assert.True(harness.Room.Seats[1].InShop);
        await harness.Dispatch(harness.Guest, 515, new byte[] { 0xBA, 0xBA });
        Assert.Equal(GameToClient.SeatTeamLabel(1, harness.Room.Seats[1].Team), harness.Host.Body(504));
        await harness.Dispatch(harness.Guest, 502, Request("RoomChatRequest", new() { ["channel"] = 0, ["text"] = "hello" }));
        // The raw chat-module reply: S2C 509 with kind 0 and colour -1 for an open line, the team colour for channel 1.
        Assert.Equal(GameToClient.ChatPanel(1, "guest", 0, 0, "hello", -1, 0, 0, string.Empty, 0), harness.Host.Body(509));
        await harness.Dispatch(harness.Guest, 502, Request("RoomChatRequest", new() { ["channel"] = 1, ["text"] = "team" }));
        Assert.Equal(GameToClient.ChatPanel(1, "guest", 0, 0, "team", unchecked((int)0xFFFFA1A1), 0, 0, string.Empty, 0), harness.Host.Body(509));
        await harness.Dispatch(harness.Host, 509, Request("RoomSeatLock", new() { ["slot"] = 3, ["closed"] = true }));
        Assert.Equal(GameToClient.SeatClosed(3, true), harness.Guest.Body(511));
        Assert.Equal(installs, harness.Host.Bodies(117).Length);
        await harness.Dispatch(harness.Host, 404, []);
        Assert.Equal(GameToClient.SeatVacated(0), harness.Guest.Body(511));
        Assert.Equal(GameToClient.MasterChanged(1), harness.Guest.Body(502));
    }

    /// <summary>A validated native start is inert; malformed requests never reach room mutation.</summary>
    [Fact]
    public async Task RoomOnlyStartAndMalformedPacketsCannotStartOrMutateARoom()
    {
        using var harness = await Harness.CreateAsync();
        await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 402, CreateBody()[..10]));
        Assert.Empty(harness.Registry.OpenRooms());
        Assert.Empty(harness.Host.Opcodes);
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var room = harness.Room;
        var count = harness.Host.Opcodes.Count();
        var start = Request("RoomEnterGameRequest", new());
        await harness.Dispatch(harness.Host, 503, start);
        await harness.Dispatch(harness.Host, 503, start);
        Assert.True(room.StartUnavailableLogged);
        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Equal(count, harness.Host.Opcodes.Count());
        await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 503, start[..^1]));
        await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 517, new byte[] { 0, 0, 0 }));
        Assert.Equal(count, harness.Host.Opcodes.Count());
        await harness.Dispatch(harness.Host, 517, Request("RoomPingRequest", new() { ["unknown_n0"] = 9999 }));
        Assert.Equal(new ushort[] { 517, 518, 117, 501, 201, 806, 118 }, harness.Host.Opcodes.Skip(count));
        Assert.Equal(new byte[12], harness.Host.Body(518));
        Assert.Equal(room.Id, harness.Room.Id);
    }

    /// <summary>Plaza entry, peer reports, room departure and menu return retain the raw native packet order.</summary>
    [Fact]
    public async Task PlazaRoomAndMenuRoundTripUsesTypedOwnership()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 106, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, harness.Host.Opcodes);
        Parity("gspGotoRoom", RoomSessionBlob.GotoRoom(SessionSettingBlob.ForPlaza(), [new(11, "host", 0)],
            [new(11, 300001, 0, EquippedItemIds: [300001])]), harness.Host.Body(117));
        Assert.Equal(RoomSessionBlob.UpdatePartySolo(11, "host", 300001, [300001]), harness.Host.Body(201));
        var hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 106, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, harness.Guest.Opcodes);
        Assert.Equal(new ushort[] { 806, 511 }, harness.Host.Opcodes.Skip(hostCount));
        Parity("RoomSeatLeave", GameToClient.SeatRecord(1, RoomPlayerInfo.ForSeat(22, "guest", 1)), harness.Host.Body(511));
        var position = Request("PlazaPositionUpload", new()
        {
            ["unknown_4"] = 77, ["x"] = 1.5f, ["y"] = 2.5f, ["z"] = 3.5f,
            ["unknown_20"] = 0x13, ["unknown_21"] = 0x29, ["unknown_24"] = 7.5f,
        });
        var guestCount = harness.Guest.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 301, position);
        Assert.Equal(guestCount, harness.Guest.Opcodes.Count());
        Assert.Equal(new byte[] { 22, 0, 0, 0 }.Concat(position), harness.Host.Body(301));
        await harness.Dispatch(harness.Host, 402, CreateBody());
        Assert.Equal(GameToClient.SeatVacated(0), harness.Guest.Body(511));
        hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Host, 404, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Null(harness.Registry.TryGet(harness.Host.Id));
        guestCount = harness.Guest.Opcodes.Count();
        hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Host, 105, []);
        Assert.Equal(new ushort[] { 117, 201, 806 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 511 }, harness.Guest.Opcodes.Skip(guestCount));
        Parity("gspGotoRoom", RoomSessionBlob.GotoRoom(SessionSettingBlob.ForPlaza() with { Kind = 1 },
            [new(11, "host", 0)], [new(11, 300001, 0, EquippedItemIds: [300001])]), harness.Host.Body(117));
        await harness.Dispatch(harness.Host, 402, CreateBody());
        Assert.NotNull(harness.Registry.TryGet(harness.Host.Id));
    }

    /// <summary>kr-1256 sub_978F20 indexes the seat ping history with the first S2C 518 word: the prober's own seat.</summary>
    [Fact]
    public async Task ProbeReplyNamesTheProbingSeat()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var slot = harness.Room.FindByGame(harness.Guest.Id)!.Slot;
        Assert.NotEqual(0, slot);
        await harness.Dispatch(harness.Guest, 517, Request("RoomPingRequest", new() { ["unknown_n0"] = slot }));
        Assert.Equal(slot, (int)PacketCodec.Read(Tables, "kr-1256", "gspRepPingUser", harness.Guest.Body(518))["slot"]!);
    }

    /// <summary>A readable team change with a populated qualifier has no audited operation: it is dropped, not fatal.</summary>
    [Fact]
    public async Task QualifiedTeamChangeIsDroppedWithoutChangingTheSeat()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var team = harness.Room.Seats.Single().Team;
        var count = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Host, 515, new byte[] { 0x3A, 7, 0, 0, 0, 0xBA });
        Assert.Equal(count, harness.Host.Opcodes.Count());
        Assert.Equal(team, harness.Room.Seats.Single().Team);
    }

    /// <summary>A plaza enter during the login burst sends nothing, as the raw kr-1256 plaza path does.</summary>
    [Fact]
    public async Task PlazaEnterBeforeTheLoginBurstEndsSendsNothing()
    {
        using var harness = await Harness.CreateAsync();
        var early = new RoomPushTests.RecordingSession(104);
        early.SetLobbyPlayer(44, "early", pastLoginBurst: false);
        await harness.Dispatch(early, 106, []);
        Assert.Empty(early.Opcodes);
        early.Features.Get<IPlayerSession>().MoveTo(SessionPhase.MainMenu);
        await harness.Dispatch(early, 106, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, early.Opcodes);
    }

    /// <summary>Refilling a low seat keeps profile stamps and equipment vectors in the raw membership order.</summary>
    [Fact]
    public async Task ReentryIntoASeatHoleKeepsTheNativeProfileVectorOrder()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var id = harness.Room.Id;
        var third = new RoomPushTests.RecordingSession(103);
        third.SetLobbyPlayer(33, "third");
        var join = Request("RoomJoinRequest", new() { ["room_id"] = id, ["password"] = "pw" });
        await harness.Dispatch(harness.Guest, 403, join);
        await harness.Dispatch(third, 403, join);
        await harness.Dispatch(harness.Guest, 404, []);
        await harness.Dispatch(harness.Guest, 403, join);
        Assert.Equal(new[] { 0, 2, 1 }, harness.Room.Seats.Select(seat => seat.Slot));
        var baseline = SessionSettingBlob.ForRoom(Source(), 2);
        var setting = baseline with { Unknown0 = checked((ushort)id), Map = Unchosen(baseline.Map) };
        var profiles = harness.Room.Seats.Select((seat, index) => new ProfileMapEntry(seat.UserNum, seat.CharacterId, index,
            EquippedItemIds: seat.EquippedItemIds ?? [])).ToArray();
        Parity("gspGotoRoom", RoomSessionBlob.GotoRoom(setting, harness.Room.Seats.Select(seat => seat.ToIdentity()).ToArray(),
            profiles, localSlot: 1), harness.Guest.Body(117));
        var replies = harness.Guest.Bodies(503)[^3..];
        Assert.Equal(new[] { 1, 0, 2 }, replies.Select(body => (int)PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", body)["slot"]!));
        foreach (var body in replies)
        {
            var slot = (int)PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", body)["slot"]!;
            var seat = harness.Room.Seats.Single(member => member.Slot == slot);
            Parity("RoomSeatUpdate", GameToClient.CreateRoomReply(slot, seat.ToPlayerInfo(), profiles), body);
        }
    }

    private sealed class Views : IEquippedViewPush
    {
        internal List<(long Recipient, int User, bool Remote)> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
            Emit(session, userNum, false, ct);
        public ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
            Emit(recipient, userNum, true, ct);
        private ValueTask Emit(ISession session, int user, bool remote, CancellationToken ct)
        {
            Sent.Add((session.Id, user, remote));
            return session.SendAsync(806, BitConverter.GetBytes(user), ct);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<PluginContext> _contexts = [];
        internal Views Views { get; } = new();
        internal RoomPushTests.RecordingSession Host { get; } = Player(101, 11, "host");
        internal RoomPushTests.RecordingSession Guest { get; } = Player(102, 22, "guest");
        internal RoomRegistry Registry => (RoomRegistry)_services.GetAll<IMatchCapabilityDirectory>().Single();
        internal PlayRoom Room => Registry.TryGet(Host.Id) ?? Registry.TryGet(Guest.Id)!;

        internal IMessageSender<T> Sender<T>() where T : IMessage => _services.GetAll<IMessageSender<T>>().Single();

        internal static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            try
            {
                var root = h.Context("router");
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new MessageRouterPlugin().ActivateAsync(h.Context("messages"), default);
                root.Effect(h._services.Register<IEquippedViewPush>(h.Views));
                var table = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", "kr-1256", "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
                new ModernRoomBinding("kr-1256/game", table, Tables, "kr-1256", NullLogger.Instance).Mount(h.Context("binding"));
                new ModernPlazaBinding("kr-1256/game", table, Tables, "kr-1256", NullLogger.Instance).Mount(h.Context("plaza"));
                await new InGamePlugin().ActivateAsync(h.Context("ingame", new()
                {
                    ["typed_only"] = true, ["max_players_per_match"] = 4, ["plaza_enter_requires_finished_login"] = true, ["plaza_replay_positions_on_enter"] = false,
                    ["plaza_echo_positions"] = false, ["plaza_vacate_peers_on_menu_return"] = false,
                }), default);
                return h;
            }
            catch { h.Dispose(); throw; }
        }

        private PluginContext Context(string id, Dictionary<string, object?>? config = null)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = id, Source = "test", Config = config }, _services, _events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }

        internal async Task Dispatch(RoomPushTests.RecordingSession session, int opcode, byte[] body) =>
            await _services.Get<IPacketRouter>().DispatchAsync("kr-1256/game", session, new((ushort)opcode, body), default);

        private static RoomPushTests.RecordingSession Player(long sessionId, int user, string name)
        {
            var session = new RoomPushTests.RecordingSession(sessionId);
            session.SetLobbyPlayer(user, name);
            return session;
        }

        public void Dispose()
        {
            for (var index = _contexts.Count - 1; index >= 0; index--) _contexts[index].Unwind();
        }
    }
}
