using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The kr-1256 room layouts in <c>packets/</c> must write the same bytes as the hand-written kr-1256 codecs
/// they replace, for the same values; the codecs were checked against the client readers and are the oracle
/// until they are deleted.
/// </summary>
public sealed class Kr1256RoomTableParityTests
{
    /// <summary>The party notice is bool32 after the own slot, not the optional object's one-byte tag.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void PartyTransitionPreservesTheOptionalObjectAndOwnSeat(int localSlot)
    {
        var setting = SettingValues(SampleSetting());
        RoomMemberView[] seats = [Member(0, 553, "Master", 300012), Member(2, 991, "Guest", 300005)];
        var ordinary = ModernRoomRecords.Install(Tables.Value, "kr-1256", setting, seats, 0, localSlot);
        var party = ModernRoomRecords.Install(Tables.Value, "kr-1256", setting, seats, 0, localSlot, partyTransition: true);
        var expected = ordinary.ToArray();
        expected[^4] = 1;
        Assert.Equal(ordinary.Length, party.Length);
        Assert.Equal(expected, party);
        Assert.Equal((byte)0, party[^9]);
        Assert.Equal(localSlot, BitConverter.ToInt32(party, party.Length - 8));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, party[^4..]);
        var decoded = PacketCodec.Read(Tables.Value, "kr-1256", "gspGotoRoom", party);
        Assert.Equal(true, decoded["party_transition"]);
        Assert.Equal((byte)0, decoded["unknown_648"]);
    }

    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    private static byte[] Written(Action<PacketWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new PacketWriter(buffer));
        return buffer.WrittenSpan.ToArray();
    }

    private static MapMetaBlob SampleMap() => new(
        0x11, 0x2233, 1, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99,
        "a44", "b68", "c92", "d116", 0x140,
        "e144", "f168", "g192", "h216", "i240", "j264",
        0x288, 0x292, 1, [new MapMetaPair(7, 8), new MapMetaPair(-1, 9)], 1,
        [new MapMetaKeyedString(5, "k"), new MapMetaKeyedString(6, "")], [1, 0, 1, 0, 1],
        [new MapMetaU16Ints(0x3344, [1, 2, 3]), new MapMetaU16Ints(0, [])],
        1, 0, 0x5A, 0x348, 0x352, "l356", "m380", 0x404);

    private static Dictionary<string, object?> MapValues(MapMetaBlob map) => new()
    {
        ["unknown_0"] = map.Unknown0, ["mode"] = map.Unknown4, ["unknown_8"] = map.Unknown8,
        ["unknown_20"] = map.Unknown20, ["unknown_24"] = map.Unknown24, ["unknown_28"] = map.Unknown28,
        ["unknown_32"] = map.Unknown32, ["unknown_36"] = map.Unknown36, ["unknown_40"] = map.Unknown40,
        ["unknown_44"] = map.Unknown44, ["unknown_68"] = map.Unknown68, ["unknown_92"] = map.Unknown92,
        ["unknown_116"] = map.Unknown116, ["unknown_140"] = map.Unknown140, ["unknown_144"] = map.Unknown144,
        ["unknown_168"] = map.Unknown168, ["unknown_192"] = map.Unknown192, ["unknown_216"] = map.Unknown216,
        ["unknown_240"] = map.Unknown240, ["unknown_264"] = map.Unknown264, ["unknown_288"] = map.Unknown288,
        ["unknown_292"] = map.Unknown292, ["unknown_296"] = map.Unknown296,
        ["unknown_297"] = map.Unknown297.Select(pair => new Dictionary<string, object?> { ["unknown_0"] = pair.Unknown0, ["unknown_4"] = pair.Unknown1 }).ToArray(),
        ["unknown_309"] = map.Unknown309,
        ["unknown_310"] = map.Unknown310.Select(row => new Dictionary<string, object?> { ["unknown_0"] = row.Unknown0, ["unknown_4"] = row.Unknown1 }).ToArray(),
        ["unknown_322"] = map.Unknown322,
        ["unknown_328"] = map.Unknown328.Select(row => new Dictionary<string, object?> { ["unknown_0"] = row.Unknown0, ["unknown_4"] = row.Unknown1 }).ToArray(),
        ["unknown_340"] = map.Unknown340, ["unknown_341"] = map.Unknown341, ["unknown_344"] = map.Unknown344,
        ["unknown_348"] = map.Unknown348, ["unknown_352"] = map.Unknown352, ["unknown_356"] = map.Unknown356,
        ["unknown_380"] = map.Unknown380, ["unknown_404"] = map.Unknown404,
    };

    [Fact]
    public void TheGameSettingTableWritesTheCodecsBytes()
    {
        var map = SampleMap();
        var expected = Written(map.Write);
        Assert.Equal(Convert.ToHexString(expected),
            Convert.ToHexString(PacketCodec.Write(Tables.Value, "kr-1256", "GameSetting", MapValues(map))));
        var empty = MapMetaBlob.Empty();
        Assert.Equal(Convert.ToHexString(Written(empty.Write)),
            Convert.ToHexString(PacketCodec.Write(Tables.Value, "kr-1256", "GameSetting", MapValues(empty))));
    }

    private static Dictionary<string, object?> SeatValues(RoomPlayerInfo seat)
    {
        object? tail = seat.Opt204Tag != RoomPlayerInfo.OptPresentTag ? seat.Opt204Tag : new Dictionary<string, object?>
        {
            ["unknown_204"] = seat.Unknown204, ["unknown_208"] = seat.Unknown208,
            ["unknown_212"] = seat.Opt212Tag != RoomPlayerInfo.OptPresentTag ? seat.Opt212Tag
                : new Dictionary<string, object?> { ["unknown_212"] = seat.Unknown212, ["unknown_216"] = seat.Unknown216 },
        };
        var social = seat.Social;
        return new()
        {
            ["occupied"] = seat.Unknown0, ["unknown_1"] = seat.Unknown1, ["unknown_2"] = seat.Unknown2,
            ["unknown_3"] = seat.Unknown3, ["team"] = seat.Unknown4, ["user_number"] = seat.UserNum,
            ["nickname"] = seat.Nickname, ["unknown_36"] = seat.Unknown36, ["unknown_40"] = seat.Unknown40,
            ["ready"] = seat.Unknown96, ["unknown_97"] = seat.Unknown97, ["unknown_98"] = seat.Unknown98,
            ["unknown_44"] = seat.Unknown44, ["unknown_48"] = seat.Unknown48, ["unknown_52"] = seat.Unknown52,
            ["unknown_64"] = seat.Unknown64, ["unknown_68"] = seat.Unknown68, ["unknown_88"] = seat.Unknown88,
            ["unknown_94"] = seat.Unknown94, ["unknown_100"] = seat.Unknown100, ["unknown_104"] = seat.Unknown104,
            ["unknown_108"] = seat.Unknown108, ["unknown_112"] = seat.Unknown112, ["unknown_116"] = seat.Unknown116,
            ["unknown_140"] = seat.Unknown140, ["unknown_144"] = seat.Unknown144, ["unknown_168"] = seat.Unknown168,
            ["unknown_169"] = seat.Unknown169, ["level"] = seat.Unknown172, ["unknown_176"] = seat.Unknown176,
            ["unknown_180"] = seat.Unknown180, ["unknown_184"] = seat.Unknown184, ["unknown_185"] = seat.Unknown185,
            ["unknown_188"] = seat.Unknown188, ["unknown_192"] = seat.Unknown192, ["unknown_196"] = seat.Unknown196,
            ["unknown_200"] = seat.Unknown200, ["unknown_204"] = tail, ["unknown_232"] = seat.Unknown232,
            ["social"] = new Dictionary<string, object?>
            {
                ["unknown_4"] = social.Unknown4, ["unknown_8"] = social.Unknown8, ["unknown_12"] = social.Unknown12,
                ["unknown_16"] = social.Unknown16, ["unknown_20"] = social.Unknown20, ["unknown_24"] = social.Unknown24,
                ["unknown_28"] = social.Unknown28, ["unknown_32"] = social.Unknown32, ["unknown_40"] = social.Unknown40,
                ["unknown_48"] = social.Unknown48, ["unknown_49"] = social.Unknown49,
            },
        };
    }

    // The codec writes the occupancy word as 0x0101 + seat; the client stores only whether it is nonzero
    // (kr-1256 sub_1143710), and the table writes that as 1, so the samples use 1.
    public static TheoryData<string> Seats => ["seat", "vacated", "full"];

    [Theory]
    [MemberData(nameof(Seats))]
    public void TheSeatRecordTableWritesTheCodecsBytes(string sample)
    {
        var seat = sample switch
        {
            "seat" => RoomPlayerInfo.ForSeat(553, "Seat", 3, level: 12, team: 2) with { Unknown0 = 1 },
            "vacated" => RoomPlayerInfo.VacatedSeat(),
            _ => RoomPlayerInfo.ForSeat(9, "Full", 1) with
            {
                Unknown0 = 1, Unknown1 = 1, Unknown52 = [4, 5], Unknown116 = "x", Unknown144 = "y",
                Opt204Tag = RoomPlayerInfo.OptPresentTag, Unknown204 = 0x204, Unknown208 = 0x208,
                Opt212Tag = RoomPlayerInfo.OptPresentTag, Unknown212 = 0x212, Unknown216 = 0x216,
                Social = new SocialSystemInfoBlob(1, 2, 3, 4, 5, 6, 7, 1, 0x0102030405060708, 1, 0),
            },
        };
        var bytes = PacketCodec.Write(Tables.Value, "kr-1256", "UserSlot", SeatValues(seat));
        Assert.Equal(Convert.ToHexString(Written(seat.Write)), Convert.ToHexString(bytes));
        var decoded = PacketCodec.Read(Tables.Value, "kr-1256", "UserSlot", bytes);
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(PacketCodec.Write(Tables.Value, "kr-1256", "UserSlot", decoded)));
    }

    [Fact]
    public void TheSessionSettingTableWritesTheCodecsBytes()
    {
        var setting = SampleSetting();
        var values = SettingValues(setting);
        var bytes = PacketCodec.Write(Tables.Value, "kr-1256", "SessionSetting", values);
        Assert.Equal(Convert.ToHexString(Written(setting.Write)), Convert.ToHexString(bytes));
        var decoded = PacketCodec.Read(Tables.Value, "kr-1256", "SessionSetting", bytes);
        Assert.Equal(Convert.ToHexString(bytes), Convert.ToHexString(PacketCodec.Write(Tables.Value, "kr-1256", "SessionSetting", decoded)));
    }

    private static SessionSettingBlob SampleSetting() => new(0x1234, 4, "room", 3, 0, 8, 1, "master", 2, SampleMap(),
        0x68, 5, 2, 0x0102030405060708, "s88", 1, 0x116, 0x120, 0x124, 0x128);

    private static Dictionary<string, object?> SettingValues(SessionSettingBlob setting) => new()
    {
            ["room_id"] = setting.Unknown0, ["session_state"] = setting.Unknown4, ["title"] = setting.Unknown8,
            ["occupants"] = setting.Unknown32, ["unknown_34"] = setting.Unknown34, ["open_seats"] = setting.Unknown36,
            ["has_password"] = setting.Unknown38, ["master_nickname"] = setting.Unknown40, ["kind"] = setting.Kind,
            ["game_setting"] = MapValues(setting.Map), ["unknown_68"] = setting.Unknown68, ["unknown_72"] = setting.Unknown72,
            ["unknown_75"] = setting.Unknown75, ["unknown_80"] = setting.Unknown80, ["unknown_88"] = setting.Unknown88,
            ["unknown_112"] = setting.Unknown112, ["unknown_116"] = setting.Unknown116, ["unknown_120"] = setting.Unknown120,
            ["unknown_124"] = setting.Unknown124, ["unknown_128"] = setting.Unknown128,
    };

    [Fact]
    public void TheRoomInstallTableWritesTheCodecsBytes()
    {
        var setting = SessionSettingBlob.ForRoom(SampleSetting());
        RoomPlayerIdentity[] players =
        [
            new(553, "Master", 0, Level: 7, Team: 1, Ready: true),
            new(9, "Guest", 2, Level: 3, Team: 2, InShop: true),
        ];
        ProfileMapEntry[] profiles =
        [
            new(553, 300001, 0, [5, 6], [300001, 202001, 201001]),
            new(9, 300003, 2),
        ];
        var expected = RoomSessionBlob.GotoRoom(setting, players, profiles, masterSlot: 0, localSlot: 2);
        // A gap is written as the empty seat record the codec writes for it.
        var seats = Enumerable.Range(0, 3).Select(slot => players.Any(player => player.Slot == slot)
            ? SeatValues(players.First(player => player.Slot == slot).ToPlayerInfo() with { Unknown0 = 1 })
            : SeatValues(RoomPlayerInfo.ForSeat(0, string.Empty, (byte)slot) with { Unknown0 = 0 })).ToArray();
        var entries = profiles.Select(entry => new Dictionary<string, object?>
        {
            ["user_number"] = entry.Identity, ["character_id"] = entry.CharacterId,
            ["equipped_items"] = entry.EquippedItemIds?.Where(id => id != entry.CharacterId).ToArray() ?? [],
            ["unknown_n3"] = entry.ModelItemIds?.ToArray() ?? [],
            ["unknown_n5"] = new[] { entry.Identity, entry.Slot, entry.CharacterId, entry.Identity ^ entry.CharacterId ^ entry.Slot }
                .SelectMany(BitConverter.GetBytes).Cast<object?>().ToArray(),
        }).ToArray();
        var values = new Dictionary<string, object?>
        {
            ["session_setting"] = SettingValues(setting), ["master_slot"] = 0, ["user_slots"] = seats,
            ["profile_entries"] = entries, ["local_slot"] = 2,
            ["team_info"] = new Dictionary<string, object?>(),
        };
        // The codec writes each seat's occupancy word as 0x0101 + seat, which the client reads as a bool
        // (kr-1256 sub_1143710); read through the table, its bytes write back exactly as the table writes.
        var table = PacketCodec.Write(Tables.Value, "kr-1256", "gspGotoRoom", values);
        var canonical = PacketCodec.Write(Tables.Value, "kr-1256", "gspGotoRoom",
            PacketCodec.Read(Tables.Value, "kr-1256", "gspGotoRoom", expected));
        Assert.Equal(Convert.ToHexString(canonical), Convert.ToHexString(table));
        Assert.Equal(expected.Length, table.Length);
    }

    // Writes a table and requires the codec's bytes, compared through the table's reader so a bool word the
    // codec writes as another nonzero value counts as the client reads it.
    private static void Same(byte[] codec, string structure, Dictionary<string, object?> values)
    {
        var table = PacketCodec.Write(Tables.Value, "kr-1256", structure, values);
        var canonical = PacketCodec.Write(Tables.Value, "kr-1256", structure, PacketCodec.Read(Tables.Value, "kr-1256", structure, codec));
        Assert.Equal(Convert.ToHexString(canonical), Convert.ToHexString(table));
        Assert.Equal(codec.Length, table.Length);
    }

    [Fact]
    public void TheRoomControlTablesWriteTheCodecsBytes()
    {
        var setting = SessionSettingBlob.ForRoomListRow("room", "master", 1, 3, 2, 8, 0x22, hasPassword: true);
        Same(GameToClient.RoomList(1, 2, 3, [setting, setting]), "RoomListReply", new()
        {
            ["unknown_0"] = 1, ["unknown_4"] = 2, ["unknown_8"] = 3, ["rooms"] = new[] { SettingValues(setting), SettingValues(setting) },
        });
        var seat = new RoomPlayerIdentity(553, "Seat", 4, Level: 9, Team: 1, Ready: true).ToPlayerInfo();
        Same(GameToClient.SeatRecord(4, seat), "RoomSeatLeave", new()
        {
            ["slot"] = 4, ["user"] = SeatValues(seat), ["team_info"] = new Dictionary<string, object?>(),
        });
        Same(GameToClient.SeatVacated(5), "RoomSeatLeave", new()
        {
            ["slot"] = 5, ["user"] = SeatValues(RoomPlayerInfo.VacatedSeat()), ["team_info"] = new Dictionary<string, object?>(),
        });
        Same(GameToClient.SeatClosed(6, true), "RoomSeatLeave", new()
        {
            ["slot"] = 6, ["user"] = SeatValues(RoomPlayerInfo.VacatedSeat() with { Unknown1 = 1 }), ["team_info"] = new Dictionary<string, object?>(),
        });
        ProfileMapEntry[] profiles = [new(553, 300001, 4, null, [300001, 202001])];
        Same(GameToClient.CreateRoomReply(4, seat, profiles), "RoomSeatUpdate", new()
        {
            ["slot"] = 4, ["user"] = SeatValues(seat), ["team_info"] = new Dictionary<string, object?>(),
            ["profile_entries"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["user_number"] = 553, ["character_id"] = 300001, ["equipped_items"] = new[] { 202001 },
                    ["unknown_n5"] = new[] { 553, 4, 300001, 553 ^ 300001 ^ 4 }.SelectMany(BitConverter.GetBytes).Cast<object?>().ToArray(),
                },
            },
        });
        Same(GameToClient.JoinResult(GameToClient.JoinResultRoomFull, 7), "RoomJoinResult", new() { ["result"] = 3, ["unknown_4"] = 7 });
        Same(GameToClient.ChatPanel(2, "nick", 3, 4, "hello", 5, 6, 1, "extra", 1), "RoomChatReceived", new()
        {
            ["slot"] = 2, ["nickname"] = "nick", ["channel"] = 3, ["unknown_n3"] = 4, ["unknown_40"] = "", ["text"] = "hello",
            ["unknown_96"] = 5, ["unknown_100"] = 6, ["unknown_104"] = true, ["unknown_n9"] = "extra", ["unknown_n10"] = true,
        });
        Same(GameToClient.SeatChat(3, "line"), "gspRoomReceiveChatMsg", new() { ["slot"] = 3, ["text"] = "line" });
        Same(GameToClient.SeatTeamLabel(3, 14), "ZombieTeamChange", new() { ["seat"] = 3, ["team"] = 14 });
        Same(GameToClient.MasterChanged(2), "TutorialMasterSlot", new() { ["master_slot"] = 2 });
        Same(GameToClient.GotoMenu(0), "RoomSceneTransition", new() { ["notice"] = 0 });
        Same(RoomSessionBlob.GotoLobby(1), "RoomSceneTransition", new() { ["notice"] = 1 });
        Same(RoomSessionBlob.WaitUserList([new(1, "a", 0, Level: 4), new(2, "b", 1)]), "gspRepWaitUserList", new()
        {
            ["users"] = new[] { new Dictionary<string, object?> { ["nickname"] = "a", ["level"] = 4 }, new Dictionary<string, object?> { ["nickname"] = "b", ["level"] = 1 } },
        });
        Same(GameToClient.WaitingDialog(0x14, 9), "RoomWaitingDialog", new() { ["state"] = 0x14, ["unknown_n1"] = 9 });
        Same(RoomSessionBlob.JoinHandshake(77), "gspReqPingUser", new() { ["token"] = 77 });
        Same(RoomSessionBlob.UpdatePartySolo(553, "Seat", 300001, [300001, 202001, 201001]), "SoloPartySnapshot", new()
        {
            ["members"] = new[] { new Dictionary<string, object?> { ["user_number"] = 553, ["nickname"] = "Seat" } },
            ["profile_entries"] = new[] { new Dictionary<string, object?> { ["user_number"] = 553, ["character_id"] = 300001, ["equipped_items"] = new[] { 202001, 201001 } } },
        });
    }

    /// <summary>The inbound four-word reply must not dictate the outbound three-word seat statistics layout.</summary>
    [Fact]
    public void PingReplyUsesTheClientConsumedSeatAndTwoStatisticsWords()
    {
        // kr-1256 sub_9D9930 case 0x206 reads three words; sub_978F20 indexes the occupied seat by the first.
        var body = Request("gspRepPingUser", new() { ["slot"] = 3, ["unknown_n1"] = 8, ["unknown_n2"] = 12 });
        Assert.Equal(new byte[] { 3, 0, 0, 0, 8, 0, 0, 0, 12, 0, 0, 0 }, body);
        var parsed = PacketCodec.ReadScalars(Tables.Value, "kr-1256", "gspRepPingUser", body);
        Assert.Equal(3, parsed["slot"]);
        Assert.Equal(8, parsed["unknown_n1"]);
        Assert.Equal(12, parsed["unknown_n2"]);
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables.Value, "kr-1256", "gspRepPingUser", body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables.Value, "kr-1256", "gspRepPingUser", [.. body, (byte)0]));
    }

    /// <summary>The room timer sends one opaque word, rather than a room join request.</summary>
    [Fact]
    public void PingRequestRequiresExactlyOneWord()
    {
        var body = Request("RoomPingRequest", new() { ["unknown_n0"] = 7 });
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, body);
        Assert.Equal(7, PacketCodec.ReadScalars(Tables.Value, "kr-1256", "RoomPingRequest", body)["unknown_n0"]);
        for (var length = 0; length < body.Length; length++)
        {
            var prefix = body[..length];
            Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables.Value, "kr-1256", "RoomPingRequest", prefix));
        }
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables.Value, "kr-1256", "RoomPingRequest", [.. body, (byte)0]));
    }

    private static byte[] Request(string structure, Dictionary<string, object?> values) =>
        PacketCodec.Write(Tables.Value, "kr-1256", structure, values);

    [Fact]
    public void TheRequestTablesReadAsTheCodecsParsersDo()
    {
        var words = Enumerable.Range(1, 14).ToArray();
        var create = Request("RoomEnterGameRequest", new Dictionary<string, object?>
        {
            ["unknown_4"] = 1, ["map_key"] = "a", ["unknown_36"] = "b", ["unknown_64"] = "c",
            ["unknown_92"] = words[0], ["end_game_time_over_tick"] = words[1], ["end_game_notify_win_or_lose_tick_team"] = words[2],
            ["end_game_notify_win_or_lose_tick_star"] = words[3], ["end_game_notify_result_tick"] = words[4],
            ["fever_time_duration"] = words[5], ["star_drop_time_duration"] = words[6], ["death_match_kill_limit"] = words[7],
            ["death_match_time_limit_ms"] = words[8], ["time_attack_time_limit_ms"] = words[9],
            ["conquer_time_limit_ms"] = words[10], ["star_mode_time_limit_ms"] = words[11],
            ["unknown_144"] = words[12], ["unknown_148"] = words[13], ["unknown_n18"] = 0x5A, ["unknown_n19"] = 99,
        });
        var parsed = CreateRoomRequest.Parse(create);
        Assert.Equal((1, "a", "b", "c", 1, 99), (parsed.Unknown0, parsed.Unknown1, parsed.Unknown2, parsed.Unknown3, parsed.Unknown4, parsed.Unknown5));
        Assert.Equal((2, 14, (byte)0x5A), (parsed.Rules.EndGameTimeOver, parsed.Rules.Unknown48, parsed.Rules.Flags52));

        var chat = RoomChatRequest.Parse(Request("RoomChatRequest",
            new Dictionary<string, object?> { ["channel"] = 3, ["text"] = "hi", ["unknown_n2"] = "x" }));
        Assert.Equal((3, "hi", "x"), (chat.Unknown0, chat.Unknown1, chat.Unknown2));

        var ready = RoomStatusRequest.Parse(Request("RoomReadyRequest", new Dictionary<string, object?> { ["unknown_5"] = true }));
        Assert.Equal((false, true, false), (ready.Ready, ready.InShop, ready.InInventory));

        var position = RoomStateCommandRequest.Parse(Request("PlazaPositionUpload",
            new Dictionary<string, object?> { ["unknown_4"] = 7, ["x"] = 1.5f, ["unknown_20"] = 2, ["unknown_21"] = 3, ["unknown_24"] = 2.5f }));
        Assert.Equal((7, BitConverter.SingleToInt32Bits(1.5f), (byte)2, (byte)3, BitConverter.SingleToInt32Bits(2.5f)),
            (position.Unknown0, position.Unknown1, position.Unknown4, position.Unknown5, position.Unknown6));

        Assert.Equal(8, Request("RoomSeatLock", new Dictionary<string, object?> { ["slot"] = 2, ["closed"] = true }).Length);
        Assert.Equal(4, Request("RoomListRequest", []).Length);

        var sample = SampleSetting();
        var created = Unknown402Request.Parse(Request("RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = SettingValues(sample), ["password"] = "pw",
            ["unknown_n2"] = new Dictionary<string, object?> { ["unknown_n0"] = 77 },
        }));
        Assert.Equal(("pw", (byte)0x3A, (int?)77, sample.Unknown8), (created.Password, created.OptTag, created.UnknownOpt, created.Setting.Unknown8));
        var open = Unknown402Request.Parse(Request("RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = SettingValues(sample), ["password"] = "", ["unknown_n2"] = 0xBA,
        }));
        Assert.Equal(((byte)0xBA, (int?)null), (open.OptTag, open.UnknownOpt));

        var join = Unknown403Request.Parse(Request("RoomJoinRequest", new Dictionary<string, object?> { ["room_id"] = 12, ["password"] = "pw" }));
        Assert.Equal((12, "pw"), (join.Unknown0, join.Password));
    }

    [Fact]
    public void TheTableRoomListWritesWhatTheCodecWrote()
    {
        BubbleFighter.Fantasy.Contracts.Rooms.RoomListEntry[] rooms =
        [
            new(7, "first", "master", 1, 3, 8, true),
            new(9, "second", "", 8, 1, 16, false) { MatchInProgress = true },
        ];
        var snapshot = new BubbleFighter.Fantasy.Contracts.Rooms.RoomListSnapshot(2, rooms);
        var rows = rooms.Select(room => SessionSettingBlob.ForRoomListRow(room.Name, room.MasterNickname, room.ModeId, 0,
            room.Occupants, room.Capacity, (ushort)room.RoomId, room.HasPassword, room.MatchInProgress)).ToList();
        Assert.Equal(Convert.ToHexString(GameToClient.RoomList(2, 1, rows.Count, rows)),
            Convert.ToHexString(BubbleFighter.Fantasy.Dialect.Modern.Messages.Play.RoomListCodec.Write(Tables.Value, "kr-1256", snapshot)));
    }

    private static RoomMemberView Member(int slot, int user, string nickname, int character, bool ready = false) =>
        new(slot, user, nickname, 7, 2, ready, character, [character, 201001, 202001, 205001, 207001]);

    private static RoomPlayerIdentity Identity(RoomMemberView member) =>
        new(member.UserNum, member.Nickname, checked((byte)member.Slot), member.Level, checked((byte)member.Team),
            member.Ready, member.InShop, member.InInventory);

    private static void MatchesRaw(string structure, byte[] raw, byte[] typed)
    {
        // The native reader canonicalizes occupancy truth; every other byte must stay identical.
        var canonical = PacketCodec.Write(Tables.Value, "kr-1256", structure,
            PacketCodec.Read(Tables.Value, "kr-1256", structure, raw));
        Assert.Equal(raw.Length, typed.Length);
        Assert.Equal(canonical, typed);
    }

    /// <summary>Typed record values preserve each player's actual character, worn items and browsing state.</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ModernSeatAndPartyValuesMatchTheRawRecords(bool shop, bool inventory, bool ready)
    {
        var member = Member(2, 553, "Lavi", 300012, ready) with { InShop = shop, InInventory = inventory };
        var peer = Member(0, 991, "Peer", 300005);
        RoomMemberView[] seats = [peer, member];
        ProfileMapEntry[] profiles = seats.Select((seat, position) =>
            new ProfileMapEntry(seat.UserNum, seat.CharacterItemId, position, EquippedItemIds: seat.EquippedItemIds)).ToArray();
        MatchesRaw("RoomSeatUpdate", GameToClient.CreateRoomReply(member.Slot, Identity(member).ToPlayerInfo(), profiles),
            ModernRoomRecords.SeatUpdate(Tables.Value, "kr-1256", member, seats));
        Assert.Equal(RoomSessionBlob.UpdatePartySolo(member.UserNum, member.Nickname, member.CharacterItemId, member.EquippedItemIds),
            ModernRoomRecords.Party(Tables.Value, "kr-1256", member));
        var profile = ModernRoomRecords.Profiles(seats)[1];
        Assert.Equal(new[] { 201001, 202001, 205001, 207001 }, Assert.IsType<int[]>(profile["equipped_items"]));
        var stamp = Assert.IsType<byte[]>(profile["unknown_n5"]);
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(stamp.AsSpan(4)));
    }

    /// <summary>A gap is a vector position without identity; it must not shift the later player's seat.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ModernInstallPreservesSparseSeatIndexesAndReceiver(int localSlot)
    {
        var setting = SessionSettingBlob.ForRoom(SampleSetting(), roomState: 2);
        RoomMemberView[] seats = [Member(0, 553, "Master", 300012), Member(2, 991, "Guest", 300005) with { InInventory = true }];
        var profiles = seats.Select((seat, position) =>
            new ProfileMapEntry(seat.UserNum, seat.CharacterItemId, position, EquippedItemIds: seat.EquippedItemIds)).ToArray();
        var raw = RoomSessionBlob.GotoRoom(setting, seats.Select(Identity).ToArray(), profiles, masterSlot: 0, localSlot: localSlot);
        var typed = ModernRoomRecords.Install(Tables.Value, "kr-1256", SettingValues(setting), seats, 0, localSlot);
        MatchesRaw("gspGotoRoom", raw, typed);
        var read = PacketCodec.Read(Tables.Value, "kr-1256", "gspGotoRoom", typed);
        var rows = Assert.IsType<object?[]>(read["user_slots"]);
        Assert.Equal(3, rows.Length);
        var gap = Assert.IsType<Dictionary<string, object?>>(rows[1]);
        Assert.Equal(false, gap["occupied"]);
        Assert.Equal(0, gap["user_number"]);
        Assert.Equal((byte)1, gap["unknown_3"]);
        Assert.Equal(localSlot, read["local_slot"]);
    }

    /// <summary>The cleared and closed forms remove identity while retaining the native sentinel fields.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModernLeaveAndClosedRecordsMatchTheRawBytes(bool closed)
    {
        var raw = closed ? GameToClient.SeatClosed(6, true) : GameToClient.SeatVacated(6);
        var typed = ModernRoomRecords.SeatLeave(Tables.Value, "kr-1256", 6, closed);
        Assert.Equal(raw, typed);
        var read = PacketCodec.Read(Tables.Value, "kr-1256", "RoomSeatLeave", typed);
        var seat = Assert.IsType<Dictionary<string, object?>>(read["user"]);
        Assert.Equal(closed, seat["unknown_1"]);
        Assert.Equal(-1, seat["unknown_88"]);
        Assert.Equal(-1, seat["unknown_108"]);
        Assert.Equal(0, seat["level"]);
        Assert.Equal((byte)0xBA, seat["unknown_204"]);
    }

    /// <summary>Invalid indexes and unresolved characters fail before an install or profile can be written.</summary>
    [Fact]
    public void ModernRecordsRejectInvalidIdentityInputs()
    {
        var setting = SettingValues(SampleSetting());
        var seat = Member(0, 553, "Master", 300012);
        foreach (var seats in new RoomMemberView[][] { [seat, seat], [seat with { Slot = -1 }], [seat with { Slot = 36 }] })
            Assert.Throws<ArgumentException>(() => ModernRoomRecords.Install(Tables.Value, "kr-1256", setting, seats, 0, 0));
        Assert.Throws<ArgumentException>(() => ModernRoomRecords.Profile(seat with { CharacterItemId = 0 }, 0));
    }
}
