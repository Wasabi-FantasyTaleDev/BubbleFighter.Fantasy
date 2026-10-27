using System.Buffers;
using BubbleFighter.Fantasy.RoomCore;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
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

/// <summary>Mounted modern requests must reach shared room ownership and retain native recipient sequences.</summary>
public sealed partial class RoomCommandsBindingTests
{
    /// <summary>Native bot requests reserve seats and publish controller metadata plus both appearance maps.</summary>
    [Fact]
    public async Task AiPopupAddsUpdatesAndRemovesAReservedSeat()
    {
        using var harness = await Harness.CreateAsync(ai: true);
        await harness.Dispatch(harness.Host, 402, CreateBody());
        harness.Room.ModeId = 38;
        var body = Request("RoomAiAddRequest", new()
        {
            ["team"] = new Dictionary<string, object?> { ["team"] = (byte)2 },
            ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2,
        });
        Assert.Equal(14, body.Length);
        await harness.Dispatch(harness.Host, 2076, body);
        Assert.Single(harness.Room.Seats);
        Assert.Equal(2, harness.Room.OccupantCount);
        var bot = Assert.Single(harness.Room.Bots).Appearance;
        Assert.Equal(1, bot.Slot);
        Assert.Equal(new RoomAiIdentity(0, 11, 1, 2), bot.Ai);
        var update = PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", harness.Host.Body(503));
        var user = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(update["user"]);
        Assert.Equal((byte)0, user["unknown_3"]);
        Assert.Equal(11, user["user_number"]);
        var outer = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(user["unknown_204"]);
        Assert.Equal(11, outer["unknown_204"]);
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(outer["unknown_212"]);
        Assert.Equal(1, nested["unknown_212"]);
        Assert.Equal(2, nested["unknown_216"]);
        var slots = Assert.IsAssignableFrom<IReadOnlyList<object?>>(update["profile_entries"]);
        Assert.Equal(new[] { 0, 1 }, slots.Select(row => (int)((IReadOnlyDictionary<string, object?>)row!)["user_number"]!));
        var humans = Assert.IsAssignableFrom<IReadOnlyList<object?>>(update["unknown_n3"]);
        Assert.Equal(11, ((IReadOnlyDictionary<string, object?>)Assert.Single(humans)!)["user_number"]);

        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        Assert.Equal(2, harness.Room.FindByGame(harness.Guest.Id)!.Slot);
        var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", harness.Guest.Body(117));
        Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["profile_entries"]).Count);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["unknown_588"]).Count);
        await harness.Dispatch(harness.Guest, 2077, Request("RoomAiRemoveRequest", new() { ["slot"] = 1 }));
        Assert.Single(harness.Room.Bots);
        await harness.Dispatch(harness.Host, 2077, Request("RoomAiRemoveRequest", new() { ["slot"] = 1 }));
        Assert.Empty(harness.Room.Bots);
        Assert.Equal(2, harness.Room.Seats.Count);
    }

    /// <summary>Only complete native optional forms are decoded, and malformed requests leave the roster unchanged.</summary>
    [Fact]
    public async Task AiPopupRejectsMalformedAndUnauthorizedSelections()
    {
        using var harness = await Harness.CreateAsync(ai: true);
        await harness.Dispatch(harness.Host, 402, CreateBody());
        var absent = Request("RoomAiAddRequest", new() { ["team"] = (byte)0xBA, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2 });
        Assert.Equal(13, absent.Length);
        await harness.Dispatch(harness.Host, 2076, absent);
        Assert.Empty(harness.Room.Bots);
        harness.Room.ModeId = 38;
        foreach (var bad in new[] { absent[..^1], absent.Concat(new byte[] { 0 }).ToArray(), new byte[] { 0 }.Concat(absent[1..]).ToArray() })
            await Assert.ThrowsAsync<PacketFormatException>(() => harness.Dispatch(harness.Host, 2076, bad));
        await harness.Dispatch(harness.Host, 2076, Request("RoomAiAddRequest", new() { ["team"] = (byte)0xBA, ["slot"] = 0, ["difficulty"] = 1, ["preset"] = 2 }));
        await harness.Dispatch(harness.Host, 2076, Request("RoomAiAddRequest", new() { ["team"] = (byte)0xBA, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 99 }));
        Assert.Empty(harness.Room.Bots);
        await harness.Dispatch(harness.Host, 2076, absent);
        Assert.Single(harness.Room.Bots);
        await harness.Dispatch(harness.Host, 2076, absent);
        Assert.Single(harness.Room.Bots);
        Assert.Equal(4, Request("RoomAiRemoveRequest", new() { ["slot"] = 1 }).Length);
    }

    /// <summary>Native ownership migration changes the real controller while preserving bot seat and preset.</summary>
    [Fact]
    public async Task AiOwnershipMovesWithExplicitAndDisconnectMasterChanges()
    {
        using var harness = await Harness.CreateAsync(ai: true);
        await harness.Dispatch(harness.Host, 402, CreateBody());
        harness.Room.ModeId = 38;
        await harness.Dispatch(harness.Host, 2076, Request("RoomAiAddRequest", new() { ["team"] = (byte)0xBA, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2 }));
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        await harness.Dispatch(harness.Host, 508, Request("RoomMasterChangeRequest", new() { ["nickname"] = "guest" }));
        Assert.Equal(new RoomAiIdentity(2, 22, 1, 2), Assert.Single(harness.Room.Bots).Appearance.Ai);
        Assert.Equal(new byte[] { 11, 0, 0, 0, 2, 0, 0, 0 }, harness.Host.Body(1549));
        await harness.Dispatch(harness.Guest, 404, []);
        Assert.Equal(new RoomAiIdentity(0, 11, 1, 2), Assert.Single(harness.Room.Bots).Appearance.Ai);
        Assert.Equal(new byte[] { 22, 0, 0, 0, 0, 0, 0, 0 }, harness.Host.Body(1549));
    }

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
        var expected = Source().Map with
        {
            Unknown0 = 0, Unknown8 = 0, Unknown20 = 0,
            Unknown44 = "Random", Unknown68 = "", Unknown92 = "", Unknown116 = "",
            Unknown144 = "Random", Unknown168 = "", Unknown192 = "second", Unknown216 = "first",
            Unknown240 = "Random", Unknown264 = "",
        };
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
                var settings = RoomProjection.Create(Tables, "kr-1256", CreateBody()).BuildSettings;
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
        Assert.Equal(Table("TutorialMasterSlot", new() { ["master_slot"] = 1 }), harness.Guest.Body(502));

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
        var source = RoomProjection.Create(Tables, "kr-1256", CreateBody()).BuildSettings!;
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

    private static byte[] Table(string structure, Dictionary<string, object?> fields) =>
        PacketCodec.Write(Tables, "kr-1256", structure, fields);

    /// <summary>The UserSlot row one seat occupies, spelled from the fields the client's own reader consumes
    /// (kr-1256 sub_1143710 truth, sub_EE9550's level-1 icon word).</summary>
    private static Dictionary<string, object?> SeatRow(RoomSeat seat) => new()
    {
        ["occupied"] = true, ["unknown_3"] = checked((byte)seat.Slot), ["team"] = checked((byte)seat.Team),
        ["user_number"] = seat.UserNum, ["nickname"] = seat.Nickname, ["unknown_36"] = 1,
        ["ready"] = seat.Ready, ["unknown_97"] = seat.InShop, ["unknown_98"] = seat.InInventory,
        ["level"] = Math.Max(1, seat.Level),
    };

    /// <summary>The vacated row: kr-1256 sub_9DB1E0 removes the seat, sub_980730 hides a closed one's children.</summary>
    private static Dictionary<string, object?> VacatedRow(bool closed) => new()
    {
        ["occupied"] = false, ["unknown_1"] = closed, ["unknown_3"] = byte.MaxValue,
        ["level"] = 0, ["unknown_204"] = (byte)0xBA,
    };

    /// <summary>One profile-map entry (element reader kr-1256 sub_10C4290): the opaque stamp names the actor,
    /// the position in the vector and the character, plus the worn items the view retains.</summary>
    private static Dictionary<string, object?> ProfileRow(RoomSeat seat, int position)
    {
        var stamp = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(stamp, seat.UserNum);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(4), position);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(8), seat.CharacterId);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(12), seat.UserNum ^ seat.CharacterId ^ position);
        return new Dictionary<string, object?>
        {
            ["user_number"] = seat.UserNum, ["character_id"] = seat.CharacterId,
            ["equipped_items"] = (seat.EquippedItemIds ?? []).Where(id => id != seat.CharacterId).ToArray(),
            ["unknown_n5"] = stamp,
        };
    }

    private static Dictionary<string, object?>[] ProfileRows(System.Collections.Generic.IReadOnlyList<RoomSeat> seats) =>
        seats.Select((seat, position) => ProfileRow(seat, position)).ToArray();

    /// <summary>The creator's opaque fields survive while ordinary-room gate words and identity are overlaid.</summary>
    [Fact]
    public void CreateProjectionRetainsNativeSettingsWithoutUsingTheClientRoomId()
    {
        var request = RoomProjection.Create(Tables, "kr-1256", CreateBody());
        Assert.Equal(("room", "pw", 3, "rel_Map"), (request.Name, request.Password, request.ModeId, request.MapKey));
        Assert.True(request.UpdateExistingTitle);
        var source = Assert.IsType<RoomBuildSettings>(request.BuildSettings);
        Assert.Equal(Raw(Source().Write), source.SessionPrefix.ToArray());
        var room = new RoomView(12, "renamed", 4, "rel_Map", 4, 0, true, [], [], source);
        var actual = PacketCodec.Write(Tables, "kr-1256", "SessionSetting", RoomProjection.Session(Tables, "kr-1256", room));
        var expected = SessionSettingBlob.ForRoom(Source(), 2) with
        {
            Unknown0 = 12, Unknown8 = "renamed", Map = Unchosen(MapMetaBlob.ForRoom(Source().Map) with { Unknown4 = 4 }),
        };
        Assert.Equal(Raw(expected.Write), actual);
        Assert.Throws<InvalidOperationException>(() => RoomProjection.Session(Tables, "kr-1256", room with { BuildSettings = null }));
        Assert.Throws<InvalidOperationException>(() => RoomProjection.Session(Tables, "kr-1256", room with { BuildSettings = source with { Lineage = "kr-812" } }));
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
        Assert.Equal(Table("SoloPartySnapshot", new()
        {
            ["members"] = new object[] { new Dictionary<string, object?> { ["user_number"] = 11, ["nickname"] = "host" } },
            ["profile_entries"] = new object[] { new Dictionary<string, object?> { ["user_number"] = 11, ["character_id"] = 300001, ["equipped_items"] = Array.Empty<int>() } },
        }), harness.Host.Body(201));
        Assert.Equal(new[] { (101L, 11, false) }, harness.Views.Sent);
        var hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = room.Id, ["password"] = "pw" }));
        Assert.Equal(new ushort[] { 128, 117, 501, 201, 806, 806, 503, 503, 118 }, harness.Guest.Opcodes);
        Assert.Equal(new ushort[] { 503, 806, 118 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new[] { (101L, 11, false), (102L, 22, false), (102L, 11, true), (101L, 22, true) }, harness.Views.Sent);
        Assert.Equal(Table("RoomWaitingDialog", new() { ["state"] = 0 }), harness.Guest.Body(128));
        // The install announces the joiner's own seat last (kr-1256 sub_9D9250 installs by vector position).
        var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", harness.Guest.Body(117));
        Assert.Equal(0, (int)install["master_slot"]!);
        Assert.Equal(1, (int)install["local_slot"]!);
        Assert.Equal(2, ((System.Collections.Generic.IReadOnlyList<object>)install["user_slots"]!).Count);
        var guestRecords = harness.Guest.Bodies(503);
        Parity("RoomSeatUpdate", Table("RoomSeatUpdate", new()
        {
            ["slot"] = 1, ["user"] = SeatRow(room.Seats[1]), ["profile_entries"] = ProfileRows(room.Seats),
        }), guestRecords[0]);
        Parity("RoomSeatUpdate", Table("RoomSeatUpdate", new()
        {
            ["slot"] = 0, ["user"] = SeatRow(room.Seats[0]), ["profile_entries"] = ProfileRows(room.Seats),
        }), guestRecords[1]);
        Parity("RoomSeatUpdate", Table("RoomSeatUpdate", new()
        {
            ["slot"] = 1, ["user"] = SeatRow(room.Seats[1]), ["profile_entries"] = ProfileRows(room.Seats),
        }), harness.Host.Body(503));
    }

    /// <summary>Presence, team, chat, seat locks and refusal use native payloads without reinstalling the room.</summary>
    [Fact]
    public async Task WaitingRoomChangesAndRefusalsUseTheSharedState()
    {
        using var harness = await Harness.CreateAsync();
        await harness.Dispatch(harness.Host, 402, CreateBody());
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "wrong" }));
        Assert.Equal(new ushort[] { 103 }, harness.Guest.Opcodes);
        Assert.Equal(Table("RoomJoinResult", new() { ["result"] = 4 }), harness.Guest.Body(103));
        await harness.Dispatch(harness.Guest, 403, Request("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var installs = harness.Host.Bodies(117).Length;
        await harness.Dispatch(harness.Guest, 505, Request("RoomReadyRequest", new() { ["unknown_5"] = true }));
        Assert.True(harness.Room.Seats[1].InShop);
        await harness.Dispatch(harness.Guest, 515, new byte[] { 0xBA, 0xBA });
        Assert.Equal(Table("ZombieTeamChange", new() { ["seat"] = 1, ["team"] = checked((byte)harness.Room.Seats[1].Team) }), harness.Host.Body(504));
        await harness.Dispatch(harness.Guest, 502, Request("RoomChatRequest", new() { ["channel"] = 0, ["text"] = "hello" }));
        // The raw chat-module reply: S2C 509 with kind 0 and colour -1 for an open line, the team colour for channel 1.
        Assert.Equal(Table("RoomChatReceived", new()
        {
            ["slot"] = checked((ushort)1), ["nickname"] = "guest", ["channel"] = 0, ["text"] = "hello", ["unknown_96"] = -1,
        }), harness.Host.Body(509));
        await harness.Dispatch(harness.Guest, 502, Request("RoomChatRequest", new() { ["channel"] = 1, ["text"] = "team" }));
        Assert.Equal(Table("RoomChatReceived", new()
        {
            ["slot"] = checked((ushort)1), ["nickname"] = "guest", ["channel"] = 0, ["text"] = "team",
            ["unknown_96"] = unchecked((int)0xFFFFA1A1),
        }), harness.Host.Body(509));
        await harness.Dispatch(harness.Host, 509, Request("RoomSeatLock", new() { ["slot"] = 3, ["closed"] = true }));
        Assert.Equal(Table("RoomSeatLeave", new() { ["slot"] = 3, ["user"] = VacatedRow(true) }), harness.Guest.Body(511));
        Assert.Equal(installs, harness.Host.Bodies(117).Length);
        await harness.Dispatch(harness.Host, 404, []);
        Assert.Equal(Table("RoomSeatLeave", new() { ["slot"] = 0, ["user"] = VacatedRow(false) }), harness.Guest.Body(511));
        Assert.Equal(Table("TutorialMasterSlot", new() { ["master_slot"] = 1 }), harness.Guest.Body(502));
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
        // The plaza install carries the client's own session initializer words (kr-1256 sub_113BFA0
        // sets the map difficulty to 1; sub_113C3E0 enables +324, +340 and +341 and initializes the
        // +404 sentinel to -1), is a canonical table encoding, and seats the host alone at slot 0.
        void PlazaInstall(int kind)
        {
            var body = harness.Host.Body(117);
            Assert.Equal(PacketCodec.Write(Tables, "kr-1256", "gspGotoRoom",
                PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", body)), body);
            var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", body);
            var setting = (System.Collections.Generic.IReadOnlyDictionary<string, object?>)install["session_setting"]!;
            Assert.Equal(kind, (int)setting["kind"]!);
            var game = (System.Collections.Generic.IReadOnlyDictionary<string, object?>)setting["game_setting"]!;
            Assert.Equal(1, (int)game["unknown_140"]!);
            // The +324/+340/+341 flags read as the table's bool words.
            Assert.True((bool)game["unknown_324"]!);
            Assert.True((bool)game["unknown_340"]!);
            Assert.True((bool)game["unknown_341"]!);
            Assert.Equal(-1, (int)game["unknown_404"]!);
            Assert.Equal(0, (int)install["master_slot"]!);
            Assert.Single((System.Collections.Generic.IReadOnlyList<object>)install["user_slots"]!);
        }
        PlazaInstall(3);
        Assert.Equal(Table("SoloPartySnapshot", new()
        {
            ["members"] = new object[] { new Dictionary<string, object?> { ["user_number"] = 11, ["nickname"] = "host" } },
            ["profile_entries"] = new object[] { new Dictionary<string, object?> { ["user_number"] = 11, ["character_id"] = 300001, ["equipped_items"] = Array.Empty<int>() } },
        }), harness.Host.Body(201));
        var hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Guest, 106, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, harness.Guest.Opcodes);
        Assert.Equal(new ushort[] { 806, 511 }, harness.Host.Opcodes.Skip(hostCount));
        // The plaza has no RoomView yet, so the guest row is spelled: seat 1, user 22, "guest".
        Parity("RoomSeatLeave", Table("RoomSeatLeave", new()
        {
            ["slot"] = 1,
            ["user"] = new Dictionary<string, object?>
            {
                ["occupied"] = true, ["unknown_3"] = (byte)1, ["team"] = (byte)0, ["user_number"] = 22,
                ["nickname"] = "guest", ["unknown_36"] = 1, ["ready"] = false, ["unknown_97"] = false,
                ["unknown_98"] = false, ["level"] = 1,
            },
        }), harness.Host.Body(511));
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
        Assert.Equal(Table("RoomSeatLeave", new() { ["slot"] = 0, ["user"] = VacatedRow(false) }), harness.Guest.Body(511));
        hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Host, 404, []);
        Assert.Equal(new ushort[] { 117, 201, 116 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Null(harness.Registry.TryGet(harness.Host.Id));
        guestCount = harness.Guest.Opcodes.Count();
        hostCount = harness.Host.Opcodes.Count();
        await harness.Dispatch(harness.Host, 105, []);
        Assert.Equal(new ushort[] { 117, 201, 806 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 511 }, harness.Guest.Opcodes.Skip(guestCount));
        PlazaInstall(1);
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
        var early = new RecordingSession(104);
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
        var third = new RecordingSession(103);
        third.SetLobbyPlayer(33, "third");
        var join = Request("RoomJoinRequest", new() { ["room_id"] = id, ["password"] = "pw" });
        await harness.Dispatch(harness.Guest, 403, join);
        await harness.Dispatch(third, 403, join);
        await harness.Dispatch(harness.Guest, 404, []);
        await harness.Dispatch(harness.Guest, 403, join);
        Assert.Equal(new[] { 0, 2, 1 }, harness.Room.Seats.Select(seat => seat.Slot));
        // The install carries the three refilled seats in vector order and names the joiner's slot.
        var install = PacketCodec.Read(Tables, "kr-1256", "gspGotoRoom", harness.Guest.Body(117));
        Assert.Equal(1, (int)install["local_slot"]!);
        Assert.Equal(3, ((System.Collections.Generic.IReadOnlyList<object>)install["user_slots"]!).Count);
        var replies = harness.Guest.Bodies(503)[^3..];
        Assert.Equal(new[] { 1, 0, 2 }, replies.Select(body => (int)PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", body)["slot"]!));
        foreach (var body in replies)
        {
            var slot = (int)PacketCodec.Read(Tables, "kr-1256", "RoomSeatUpdate", body)["slot"]!;
            var seat = harness.Room.Seats.Single(member => member.Slot == slot);
            Parity("RoomSeatUpdate", Table("RoomSeatUpdate", new()
            {
                ["slot"] = slot, ["user"] = SeatRow(seat), ["profile_entries"] = ProfileRows(harness.Room.Seats),
            }), body);
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
        private string _protocol = "kr-1256/game";
        internal RecordingSession Host { get; private set; } = Player(101, 11, "host");
        internal RecordingSession Guest { get; private set; } = Player(102, 22, "guest");
        internal RoomRegistry Registry => (RoomRegistry)_services.GetAll<IMatchCapabilityDirectory>().Single();
        internal PlayRoom Room => Registry.TryGet(Host.Id) ?? Registry.TryGet(Guest.Id)!;

        internal IMessageSender<T> Sender<T>() where T : IMessage => _services.GetAll<IMessageSender<T>>().Single();

        internal static async Task<Harness> CreateAsync(bool ai = false, IGameText? texts = null, string nicknameKey = "", int level = 1,
            object[]? itemAttributes = null, bool roundsBound = false, string build = "kr-1256")
        {
            var h = new Harness();
            h._protocol = build + "/game";
            if (build != "kr-1256")
            {
                h.Host = new RecordingSession(101, TestWireDialects.Get(build).Profile);
                h.Host.SetLobbyPlayer(11, "host");
                h.Guest = new RecordingSession(102, TestWireDialects.Get(build).Profile);
                h.Guest.SetLobbyPlayer(22, "guest");
            }
            try
            {
                var root = h.Context("router");
                if (texts is not null) root.Effect(h._services.Register<IGameText>(texts));
                await new PacketRouterPlugin().ActivateAsync(root, default);
                await new MessageRouterPlugin().ActivateAsync(h.Context("messages"), default);
                root.Effect(h._events.Intercept<EquipmentViewRequested, bool>(async (request, next, ct) =>
                {
                    if (request.Protocol != h._protocol)
                        return await next(ct);
                    if (request.Remote)
                        await h.Views.SendRemoteAsync(request.Session, request.UserNum, request.CharacterItemId, request.EquippedItemIds, ct);
                    else
                        await h.Views.SendAsync(request.Session, request.UserNum, request.CharacterItemId, request.EquippedItemIds, ct);
                    return true;
                }));
                var table = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", build, "opcodes.yaml"), TestWireDialects.Get(build).Profile);
                new RoomCommandsBinding(h._protocol, table, Tables, build, NullLogger.Instance,
                    roundsBound: roundsBound || !string.IsNullOrEmpty(nicknameKey)).Mount(h.Context("binding"));
                if (ai) new RoomAiBinding(h._protocol, table, Tables, build, NullLogger.Instance).Mount(h.Context("ai"));
                new PlazaSceneBinding(h._protocol, table, Tables, build, NullLogger.Instance).Mount(h.Context("plaza"));
                new RoomListCodec(h._protocol, table.C2S("gcpReqListSession"), table.S2C("gspRepListSession"), Tables, build, NullLogger.Instance).Mount(h.Context("list"));
                await new InGamePlugin().ActivateAsync(h.Context("ingame", new()
                {
                    ["game_protocol"] = h._protocol, ["relay_protocol"] = build + "/relay",
                    ["ai_mode_ids"] = ai ? new[] { 38, 39 } : Array.Empty<int>(),
                    ["ai_presets"] = ai ? new object[] { new Dictionary<string, object?>
                    {
                        ["difficulty"] = 1, ["preset"] = 2, ["nickname"] = "test bot",
                        ["nickname_key"] = nicknameKey, ["level"] = level,
                        ["character_item_id"] = 300025, ["equipped_item_ids"] = new[] { 203108 },
                        ["item_attributes"] = itemAttributes ?? [],
                    } } : Array.Empty<object>(),
                    ["max_players_per_match"] = 4, ["plaza_enter_requires_finished_login"] = true, ["plaza_replay_positions_on_enter"] = false,
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

        internal void MountRoundNames()
        {
            var table = OpcodeTableLoader.Load(Path.Combine(Root, "profiles", "kr-1256", "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
            new RoundStartBinding("kr-1256/game", table, Tables, "kr-1256", [1, 0, 1], NullLogger.Instance).Mount(Context("start-names"));
            new RoundResultBinding("kr-1256/game", table, Tables, "kr-1256", [38], NullLogger.Instance).Mount(Context("result-names"));
        }

        internal async Task Dispatch(RecordingSession session, int opcode, byte[] body) =>
            await _services.Get<IPacketRouter>().DispatchAsync(_protocol, session, new((ushort)opcode, body), default);

        private static RecordingSession Player(long sessionId, int user, string name)
        {
            var session = new RecordingSession(sessionId);
            session.SetLobbyPlayer(user, name);
            return session;
        }

        public void Dispose()
        {
            for (var index = _contexts.Count - 1; index >= 0; index--) _contexts[index].Unwind();
        }
    }
}
