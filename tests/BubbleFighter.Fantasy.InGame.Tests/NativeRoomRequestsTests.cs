using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    /// <summary>Partial round edits and late joins retain Random for every round the master has not chosen.</summary>
    [Theory]
    [InlineData("cn-774", 117, "unknown_4")]
    [InlineData("tw-454", 115, "unknown_0")]
    public async Task NativeNewRoomAndPartialRoundEditDiscardStartupMaps(string build, int installOpcode, string mode)
    {
        using var harness = await Harness.CreateAsync(build: build);
        var unchosen = Assert.IsType<RoomUnchosenMap>(RoomUnchosenMap.Load(Tables, build));
        var startup = unchosen.Fields.ToDictionary(pair => pair.Key, _ => (object?)"rel_Village01_Byway");
        startup[mode] = 1;
        await harness.Dispatch(harness.Host, 402, PacketCodec.Write(Tables, build, "RoomCreateRequest", new Dictionary<string, object?>()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "random room", ["open_seats"] = (ushort)4, ["game_setting"] = startup,
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        }));
        Dictionary<string, object?> Game(byte[] body)
        {
            var install = PacketCodec.Read(Tables, build, "gspGotoRoom", body);
            var session = Assert.IsType<Dictionary<string, object?>>(install["session_setting"]);
            return Assert.IsType<Dictionary<string, object?>>(session["game_setting"]);
        }
        var created = Game(harness.Host.Body((ushort)installOpcode));
        foreach (var (field, value) in unchosen.Fields)
            Assert.Equal(value, created[field]);
        await harness.Dispatch(harness.Host, 514, PacketCodec.Write(Tables, build, "RoomRoundSettingChangeRequest", new Dictionary<string, object?>()
        {
            ["round_index"] = 1, ["unknown_4"] = "rel_SelectedRound", ["unknown_28"] = "selected text",
        }));
        await harness.Dispatch(harness.Guest, 403, PacketCodec.Write(Tables, build, "RoomJoinRequest", new Dictionary<string, object?>()
        {
            ["room_id"] = harness.Room.Id, ["password"] = "",
        }));
        var joined = Game(harness.Guest.Body((ushort)installOpcode));
        var expected = new Dictionary<string, object?>(created);
        Assert.IsType<RoomRoundSettingFields>(RoomRoundSettingFields.Load(Tables, build))
            .Apply(expected, 1, "rel_SelectedRound", "selected text");
        Assert.Equal(PacketCodec.Write(Tables, build, "GameSetting", expected),
            PacketCodec.Write(Tables, build, "GameSetting", joined));
    }

    /// <summary>Actual party moves retain their native notice flag in both room and plaza installs.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativePartyTransitionsRetainTheirNotice(string build)
    {
        BubbleFighter.Fantasy.Contracts.Rooms.RoomMemberView[] seats = [new(0, 11, "host", 1, 0, false, 300001, [])];
        foreach (var notify in new[] { false, true })
        {
            var room = RoomRecords.Install(Tables, build, new Dictionary<string, object?>(), seats, 0, 0, notify);
            var plaza = PlazaRecords.Install(Tables, build, seats, 0, 3, notify);
            Assert.Equal(notify, PacketCodec.Read(Tables, build, "gspGotoRoom", room)["party_transition"]);
            Assert.Equal(notify, PacketCodec.Read(Tables, build, "gspGotoRoom", plaza)["party_transition"]);
        }
    }

    /// <summary>The native map panels share master authority and notify both occupants without reinstalling seats.</summary>
    [Theory]
    [InlineData("cn-774", "unknown_4", "unknown_76", "unknown_100", "unknown_124", "unknown_148", "unknown_224", "unknown_248")]
    [InlineData("tw-454", "unknown_0", "unknown_60", "unknown_84", "unknown_108", "unknown_132", "unknown_208", "unknown_232")]
    public async Task NativeMapChangesUseSharedAuthorityAndRetainNativeText(string build, string mode, string key,
        string name, string first, string third, string roundKey, string roundText)
    {
        using var harness = await Harness.CreateAsync(build: build);
        byte[] Encode(string structure, Dictionary<string, object?> values) => PacketCodec.Write(Tables, build, structure, values);
        await harness.Dispatch(harness.Host, 402, Encode("RoomCreateRequest", new()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "native map room", ["open_seats"] = (ushort)4,
                ["game_setting"] = new Dictionary<string, object?> { [mode] = 1, [key] = "rel_Old" },
            },
            ["password"] = "pw", ["unknown_n2"] = (byte)0xBA,
        }));
        await harness.Dispatch(harness.Guest, 403, Encode("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        var hostCount = harness.Host.Opcodes.Count();
        var guestCount = harness.Guest.Opcodes.Count();
        var change = Encode("RoomGameSettingChangeRequest", new()
        {
            ["unknown_0"] = "native-first", ["unknown_24"] = "display-name", ["unknown_48"] = "native-third",
            ["unknown_72"] = "rel_New", ["unknown_128"] = 1,
        });
        await harness.Dispatch(harness.Guest, 513, change);
        Assert.Equal(hostCount, harness.Host.Opcodes.Count());
        Assert.Equal(guestCount, harness.Guest.Opcodes.Count());
        Assert.Equal("rel_Old", harness.Room.MapKey);
        await harness.Dispatch(harness.Host, 513, change);
        Assert.Equal(new ushort[] { 505 }, harness.Host.Opcodes.Skip(hostCount));
        Assert.Equal(new ushort[] { 505 }, harness.Guest.Opcodes.Skip(guestCount));
        Assert.Equal(harness.Host.Body(505), harness.Guest.Body(505));
        Assert.Equal("rel_New", harness.Room.MapKey);
        var map = PacketCodec.Read(Tables, build, "GameSetting", harness.Host.Body(505));
        Assert.Equal("rel_New", map[key]);
        Assert.Equal("display-name", map[name]);
        Assert.Equal("native-first", map[first]);
        Assert.Equal("native-third", map[third]);
        var beforeRound = harness.Host.Body(505);
        var round = Encode("RoomRoundSettingChangeRequest", new() { ["round_index"] = 1, ["unknown_4"] = "rel_Round", ["unknown_28"] = "round-text" });
        await harness.Dispatch(harness.Guest, 514, round);
        Assert.Equal(beforeRound, harness.Host.Body(505));
        await harness.Dispatch(harness.Host, 514, round);
        var updated = PacketCodec.Read(Tables, build, "GameSetting", harness.Host.Body(505));
        Assert.Equal("rel_Round", updated[roundKey]);
        Assert.Equal("round-text", updated[roundText]);
        updated[roundKey] = map[roundKey];
        updated[roundText] = map[roundText];
        Assert.Equal(beforeRound, PacketCodec.Write(Tables, build, "GameSetting", updated));
    }

    /// <summary>Native clients share room ownership while preserving their own seat and appearance containers.</summary>
    [Theory]
    [InlineData("cn-774", 117, 128)]
    [InlineData("tw-454", 115, 126)]
    public async Task NativeRoomsCreateJoinReadyChangeTeamAndLeave(string build, int installOpcode, int dialogOpcode)
    {
        using var harness = await Harness.CreateAsync(build: build);
        byte[] Encode(string name, Dictionary<string, object?> values) => PacketCodec.Write(Tables, build, name, values);
        var mode = build == "cn-774" ? "unknown_4" : "unknown_0";
        var map = build == "cn-774" ? "unknown_76" : "unknown_60";
        var create = Encode("RoomCreateRequest", new()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "native room", ["open_seats"] = (ushort)4,
                ["game_setting"] = new Dictionary<string, object?> { [mode] = 1, [map] = "rel_Village01" },
            },
            ["password"] = "pw", ["unknown_n2"] = (byte)0xBA,
        });
        await harness.Dispatch(harness.Host, 402, create);
        Assert.Single(harness.Room.Seats);
        var install = PacketCodec.Read(Tables, build, "gspGotoRoom", harness.Host.Body((ushort)installOpcode));
        Assert.Equal(0, install["local_slot"]);
        Assert.False(install.ContainsKey("profile_entries"));
        var self = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(install["user_slots"])));
        Assert.Equal(11, self["user_number"]);
        Assert.Equal((byte)0, self["slot"]);
        Assert.Equal(-1, self["party_marker_index"]);
        await harness.Dispatch(harness.Guest, 401, [0, 0, 10, 0]);
        var list = PacketCodec.Read(Tables, build, "RoomListReply", harness.Guest.Body(101));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object?>>(list["rooms"]));
        await harness.Dispatch(harness.Guest, 403, Encode("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "bad" }));
        Assert.Equal(Convert.FromHexString("0400000000000000"), harness.Guest.Body(103));
        Assert.Single(harness.Room.Seats);
        await harness.Dispatch(harness.Guest, 403, Encode("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "pw" }));
        Assert.Contains((ushort)dialogOpcode, harness.Guest.Opcodes);
        Assert.Equal(2, harness.Room.Seats.Count);
        var guestInstall = PacketCodec.Read(Tables, build, "gspGotoRoom", harness.Guest.Body((ushort)installOpcode));
        Assert.Equal(1, guestInstall["local_slot"]);
        var update = PacketCodec.Read(Tables, build, "RoomSeatUpdate", harness.Host.Body(503));
        Assert.IsType<Dictionary<string, object?>>(update["equipment"]);
        Assert.False(update.ContainsKey("profile_entries"));
        await harness.Dispatch(harness.Guest, 505, Convert.FromHexString("010000000000000000000000"));
        Assert.True(harness.Room.FindByGame(harness.Guest.Id)!.Ready);
        update = PacketCodec.Read(Tables, build, "RoomSeatUpdate", harness.Host.Body(503));
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(update["user"])["ready"]);
        await harness.Dispatch(harness.Guest, 505, new byte[12]);
        var previousTeam = harness.Room.FindByGame(harness.Guest.Id)!.Team;
        await harness.Dispatch(harness.Guest, 515, Convert.FromHexString("FFFFFFFF0000000000000000"));
        Assert.NotEqual(previousTeam, harness.Room.FindByGame(harness.Guest.Id)!.Team);
        Assert.Equal(25, harness.Host.Body(504).Length);
        await harness.Dispatch(harness.Guest, 404, []);
        Assert.Single(harness.Room.Seats);
        var leave = PacketCodec.Read(Tables, build, "RoomSeatLeave", harness.Host.Body(511));
        Assert.Equal(false, Assert.IsType<Dictionary<string, object?>>(leave["user"])["occupied"]);
    }

    /// <summary>The native three-word local toggle maps to the shared operation without authorizing other actors.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativePlainTeamToggleRejectsQualifiedActors(string build)
    {
        byte[] localToggle = [255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.NotNull(RoomRequests.ReadTeamChange(Tables, build, localToggle));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(Tables, build, localToggle[..^1]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(Tables, build, [.. localToggle, 0]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadTeamChange(Tables, build, [0xBA, 0xBA]));
        foreach (var index in new[] { 0, 4, 8 })
        {
            var qualified = (byte[])localToggle.Clone();
            qualified[index] = 1;
            Assert.Throws<NotSupportedException>(() => RoomRequests.ReadTeamChange(Tables, build, qualified));
        }
    }

    /// <summary>The nested native rule writer contributes all thirteen words and the byte before the final word.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void NativeStartIncludesNestedRuleRecord(string build)
    {
        // cn-774 sub_105F2C0 / tw-454 sub_EBE7E0 write the same rule sequence as the Korean baseline.
        byte[] body = [
            0, 0, 0, 0,
            1, 0, 0, 0, 109, 0,
            1, 0, 0, 0, 110, 0,
            1, 0, 0, 0, 115, 0,
            0, 0, 0, 0,
            1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0,
            5, 0, 0, 0, 6, 0, 0, 0, 7, 0, 0, 0,
            64, 13, 3, 0, 128, 26, 6, 0,
            10, 0, 0, 0, 11, 0, 0, 0, 12, 0, 0, 0, 13, 0, 0, 0,
            1, 23, 0, 0, 0,
        ];
        var parsed = RoomRequests.ReadStart(Tables, build, body);
        Assert.Equal(83, body.Length);
        Assert.Equal("m", parsed["map_key"]);
        Assert.Equal("n", parsed["unknown_36"]);
        Assert.Equal("s", parsed["unknown_64"]);
        Assert.Equal(200000, parsed["death_match_time_limit_ms"]);
        Assert.Equal(400000, parsed["time_attack_time_limit_ms"]);
        Assert.Equal(body, PacketCodec.Write(Tables, build, "RoomEnterGameRequest",
            parsed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadStart(Tables, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadStart(Tables, build, [.. body, 0]));
    }

    /// <summary>Native creators retain their own settings while the shared room identity overlays the audited fields.</summary>
    [Theory]
    [InlineData("cn-774", "unknown_4", "unknown_76", "unknown_0", 37978)]
    [InlineData("tw-454", "unknown_0", "unknown_60", "unknown_8", 5210)]
    public void NativeRoomProjectionRetainsOpaqueSettings(string build, string modeField, string mapField,
        string flagsField, int mask)
    {
        var values = new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "native-room",
                ["game_setting"] = new Dictionary<string, object?>
                {
                    [modeField] = 1, [mapField] = "rel_Village01", [flagsField] = mask | 0x20,
                },
            },
            ["password"] = "secret", ["unknown_n2"] = (byte)0xBA,
        };
        var request = RoomProjection.Create(Tables, build, PacketCodec.Write(Tables, build, "RoomCreateRequest", values));
        Assert.Equal(1, request.ModeId);
        Assert.Equal("rel_Village01", request.MapKey);
        var source = Assert.IsType<BubbleFighter.Fantasy.Contracts.Rooms.RoomBuildSettings>(request.BuildSettings);
        var retained = PacketCodec.Read(Tables, build, "GameSetting", source.Bytes.Span);
        Assert.Equal(mask | 0x20, Convert.ToInt32(retained[flagsField]));
        var projection = RoomProjectionFields.Load(Tables, build);
        projection.Apply(retained, 2);
        Assert.Equal(2, projection.ReadMode(retained));
        Assert.Equal(0x20, Convert.ToInt32(retained[flagsField]));
        Assert.Equal("rel_Village01", projection.ReadMap(retained));
        var changed = PacketCodec.Read(Tables, build, "GameSetting", PacketCodec.Write(Tables, build, "GameSetting", retained));
        Assert.Equal(2, projection.ReadMode(changed));
        Assert.Equal(0x20, Convert.ToInt32(changed[flagsField]));
    }

    /// <summary>Creation follows the native suffix, including a client whose writer ends after the password.</summary>
    [Theory]
    [InlineData("kr-1256", true)]
    [InlineData("cn-774", true)]
    [InlineData("tw-454", false)]
    public void NativeCreateConsumesOnlyItsOwnOptionalTail(string build, bool tagged)
    {
        var values = new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?> { ["title"] = "native-room" },
            ["password"] = "secret",
            ["unknown_n2"] = (byte)0xBA,
        };
        var body = PacketCodec.Write(Tables, build, "RoomCreateRequest", values);
        var session = PacketCodec.Write(Tables, build, "SessionSetting", (Dictionary<string, object?>)values["session_setting"]!);
        Assert.Equal(session.Length + 4 + 12 + (tagged ? 1 : 0), body.Length);
        var parsed = RoomRequests.ReadCreate(Tables, build, body);
        Assert.Equal("secret", parsed["password"]);
        Assert.Equal(tagged, parsed.ContainsKey("unknown_n2"));
        Assert.Throws<PacketFormatException>(() => RoomRequests.ReadCreate(Tables, build, [.. body, 0]));
        if (tagged)
        {
            Assert.Equal((byte)0xBA, RoomRequests.ReadCreate(Tables, build, body[..^1])["unknown_n2"]);
            values["unknown_n2"] = new Dictionary<string, object?> { ["unknown_n0"] = 23 };
            var present = PacketCodec.Write(Tables, build, "RoomCreateRequest", values);
            Assert.Equal(body.Length + 4, present.Length);
            var tail = Assert.IsType<Dictionary<string, object?>>(RoomRequests.ReadCreate(Tables, build, present)["unknown_n2"]);
            Assert.Equal(23, Convert.ToInt32(tail["unknown_n0"]));
        }
        else
        {
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadCreate(Tables, build, body[..^1]));
            Assert.Throws<PacketFormatException>(() => RoomRequests.ReadCreate(Tables, build, [.. body, 0xBA]));
        }
    }
}
