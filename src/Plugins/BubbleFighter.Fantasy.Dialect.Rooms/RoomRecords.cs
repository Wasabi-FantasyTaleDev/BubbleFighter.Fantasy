using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Supplies typed identity and equipment values to the modern family's audited room tables.</summary>
internal static class RoomRecords
{
    internal static bool UsesEquippedObjects(PacketTables tables, string build) =>
        tables.Resolve(build, "gspGotoRoom").Any(field => field.Name == "equipped_objects");

    internal static Dictionary<string, object?> Seat(RoomMemberView seat, ISession? recipient = null, IGameText? texts = null)
    {
        var values = new Dictionary<string, object?>
        {
        // kr-1256 sub_1143710 converts the occupancy word to truth; the table uses canonical true.
        ["occupied"] = true,
        ["unknown_3"] = checked((byte)seat.Slot),
        ["team"] = checked((byte)seat.Team),
        ["user_number"] = seat.UserNum,
        ["nickname"] = RoomNames.Resolve(seat.Nickname, seat.Ai?.NicknameKey ?? "", recipient, texts),
        ["unknown_36"] = 1,
        ["ready"] = seat.Ready,
        ["unknown_97"] = seat.InShop,
        ["unknown_98"] = seat.InInventory,
        // kr-1256 sub_EE9550 uses level - 1; zero makes the level icon disappear.
        ["level"] = Math.Max(1, seat.Level),
        };
        if (seat.Ai is { } ai)
        {
            // kr-1256 sub_9EBFE0 compares +3 with the local human slot; sub_921130 shares its account identity.
            values["unknown_3"] = checked((byte)ai.ControllerSlot);
            values["user_number"] = ai.ControllerUserNum;
            values["color_index"] = seat.ColorIndex;
            values["unknown_204"] = new Dictionary<string, object?>
            {
                ["unknown_204"] = ai.ControllerUserNum,
                ["unknown_208"] = ai.Difficulty,
                // kr-1256 sub_97C2F0 uses the nested pair to select the current difficulty/preset popup row.
                ["unknown_212"] = new Dictionary<string, object?>
                {
                    ["unknown_212"] = ai.Difficulty,
                    ["unknown_216"] = ai.Preset,
                },
            };
        }
        return values;
    }

    internal static Dictionary<string, object?> Vacated(bool closed = false) => new()
    {
        // kr-1256 sub_9DB1E0 removes an unoccupied seat; sub_980730 hides a closed seat's children.
        ["occupied"] = false,
        ["unknown_1"] = closed,
        ["unknown_3"] = byte.MaxValue,
        ["level"] = 0,
        ["unknown_204"] = (byte)0xBA,
    };

    internal static Dictionary<string, object?> Gap(int slot) => new()
    {
        // kr-1256 sub_9D9250 installs by vector position. A gap keeps its index but has no identity.
        ["occupied"] = false,
        ["unknown_3"] = checked((byte)slot),
        ["unknown_36"] = 1,
    };

    internal static Dictionary<string, object?> Profile(RoomMemberView seat, int position)
    {
        if (seat.CharacterItemId is < 300000 or > 301999)
            throw new ArgumentException("A room profile requires a resolved native character item.", nameof(seat));
        // kr-1256 sub_10C4290 reads this opaque byte vector. Keep the established server stamp for parity;
        // it supplies no authorization and its words have no claimed client meaning.
        var stamp = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(stamp, seat.UserNum);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(4), position);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(8), seat.CharacterItemId);
        BinaryPrimitives.WriteInt32LittleEndian(stamp.AsSpan(12), seat.UserNum ^ seat.CharacterItemId ^ position);
        return new Dictionary<string, object?>
        {
            ["user_number"] = seat.UserNum,
            ["character_id"] = seat.CharacterItemId,
            // kr-1256 sub_817DF0 replaces the worn vector, so every committed worn item is retained.
            ["equipped_items"] = seat.EquippedItemIds.Where(id => id != seat.CharacterItemId).ToArray(),
            ["unknown_n5"] = stamp,
            // kr-1256 sub_8175E0 installs these modifiers; kr-1256 sub_91F720 reads kind 7 for material selection.
            ["unknown_n6"] = seat.ItemAttributes.Select(attribute => new Dictionary<string, object?>
            {
                ["unknown_4"] = attribute.ItemId,
                ["unknown_8"] = attribute.Kind,
                ["unknown_12"] = attribute.Values,
                ["unknown_28"] = attribute.Unknown28,
                ["unknown_32"] = attribute.Unknown32,
            }).ToArray(),
        };
    }

    internal static Dictionary<string, object?>[] Profiles(IReadOnlyList<RoomMemberView> seats) =>
        seats.Select((seat, position) => Profile(seat, position)).ToArray();

    private static Dictionary<string, object?>[] SlotProfiles(IReadOnlyList<RoomMemberView> seats)
    {
        if (!seats.Any(seat => seat.Ai is not null))
            return Profiles(seats);
        // kr-1256 sub_81D060 keys the first appearance map by seat, including computer players.
        return seats.Select((seat, position) =>
        {
            var profile = Profile(seat, position);
            profile["user_number"] = seat.Slot;
            return profile;
        }).ToArray();
    }

    private static Dictionary<string, object?>[] HumanProfiles(IReadOnlyList<RoomMemberView> seats) =>
        // kr-1256 sub_81D010 keys the second map by account, separately from the bot slot map.
        seats.Any(seat => seat.Ai is not null) ? Profiles([.. seats.Where(seat => seat.Ai is null)]) : [];

    internal static byte[] Install(PacketTables tables, string build, IReadOnlyDictionary<string, object?> setting,
        IReadOnlyList<RoomMemberView> seats, int masterSlot, int localSlot, bool partyTransition = false,
        ISession? recipient = null, IGameText? texts = null)
    {
        ValidateLayouts(tables, build);
        // kr-1256 sub_9D9250 supports 36 seats in the plaza and 16 in rooms, with no duplicate indexes.
        if (seats.Any(seat => seat.Slot is < 0 or >= 36) || seats.Select(seat => seat.Slot).Distinct().Count() != seats.Count)
            throw new ArgumentException("Room installs require unique seat indexes below 36.", nameof(seats));
        var count = seats.Count == 0 ? 0 : seats.Max(seat => seat.Slot) + 1;
        var ordered = seats.ToDictionary(seat => seat.Slot);
        var native = UsesEquippedObjects(tables, build);
        var rows = Enumerable.Range(0, count).Select(slot => ordered.TryGetValue(slot, out var seat)
            ? native ? PlazaRecords.Seat(tables, build, seat, recipient, texts) : Seat(seat, recipient, texts)
            : native ? new Dictionary<string, object?> { ["occupied"] = false } : Gap(slot)).ToArray();
        return PacketCodec.Write(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = setting,
            ["master_slot"] = masterSlot,
            ["user_slots"] = rows,
            ["profile_entries"] = SlotProfiles(seats),
            ["unknown_588"] = HumanProfiles(seats),
            ["local_slot"] = localSlot,
            ["party_transition"] = partyTransition,
            ["equipped_objects"] = native ? seats.Select(EquippedObject).ToArray() : [],
        });
    }

    internal static byte[] SeatUpdate(PacketTables tables, string build, RoomMemberView seat, IReadOnlyList<RoomMemberView> seats,
        ISession? recipient = null, IGameText? texts = null)
    {
        ValidateLayouts(tables, build);
        return PacketCodec.Write(tables, build, "RoomSeatUpdate", new Dictionary<string, object?>
        {
            ["slot"] = seat.Slot,
            ["user"] = UsesEquippedObjects(tables, build) ? PlazaRecords.Seat(tables, build, seat, recipient, texts) : Seat(seat, recipient, texts),
            ["equipment"] = UsesEquippedObjects(tables, build) ? EquippedObject(seat) : null,
            ["profile_entries"] = SlotProfiles(seats),
            ["unknown_n3"] = HumanProfiles(seats),
        });
    }

    internal static byte[] SeatLeave(PacketTables tables, string build, int slot, bool closed = false)
    {
        ValidateLayouts(tables, build);
        return PacketCodec.Write(tables, build, "RoomSeatLeave", new Dictionary<string, object?>
        {
            ["slot"] = slot,
            ["user"] = UsesEquippedObjects(tables, build)
                ? new Dictionary<string, object?> { ["occupied"] = false, ["closed"] = closed }
                : Vacated(closed),
        });
    }

    internal static byte[] Party(PacketTables tables, string build, RoomMemberView seat, ISession? session = null)
    {
        var party = session is not null && session.TryFeature<IPartyMembership>(out var membership)
            ? membership.Party : new PartySnapshot([new(seat.UserNum, seat.Nickname)]);
        return PartyRecords.Write(tables, build,
            new PartyUpdated(party, new(seat.UserNum, seat.CharacterItemId, seat.EquippedItemIds)));
    }

    /// <summary>
    /// Checks the rows the writers fill: the install (kr-1256 sub_9D9250 installs by vector position, and the
    /// avatar gate kr-1256 sub_81D060 reads the profile map), the seat record the install and the seat update
    /// share (the occupancy scan kr-1256 sub_9EE100 and the level-icon readers named in packets/room.yaml), and
    /// the slot word both seat envelopes lead with. This helper has no bind step of its own, so it checks before
    /// every use rather than on the first send.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    internal static void ValidateLayouts(PacketTables tables, string build)
    {
        if (UsesEquippedObjects(tables, build))
        {
            PlazaRecords.RequireLayouts(tables, build);
            PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Room records",
                ("slot", "u8"), ("team", "u8"), ("closed", "bool"), ("ready", "bool"),
                ("in_shop", "bool"), ("in_inventory", "bool"));
            PacketLayoutContract.RequireFields(tables, build, "RoomSeatUpdate", "Room records",
                ("slot", "u32"), ("user", "UserSlot"), ("equipment", "EquippedObjectEntry"), ("team_info", "ServerTeamInfo"));
            return;
        }
        PacketLayoutContract.RequireFields(tables, build, "RoomProfileEntry", "Room appearance",
            ("user_number", "u32"), ("character_id", "u32"), ("equipped_items", "list<u32>"),
            ("unknown_n5", "list<u8>"), ("unknown_n6", "list<ItemAttr>"));
        PacketLayoutContract.RequireFields(tables, build, "ItemAttr", "Room item attributes",
            ("unknown_4", "u32"), ("unknown_8", "u32"), ("unknown_12", "list<u32>"),
            ("unknown_28", "u32"), ("unknown_32", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "gspGotoRoom", "Room records",
            ("session_setting", "SessionSetting"), ("master_slot", "u32"), ("user_slots", "list<UserSlot>"),
            ("profile_entries", "list<RoomProfileEntry>"), ("local_slot", "u32"), ("party_transition", "bool"));
        PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Room records",
            ("occupied", "bool"), ("unknown_1", "bool"), ("unknown_3", "u8"), ("team", "u8"),
            ("user_number", "u32"), ("nickname", "wstr"), ("unknown_36", "u16"), ("ready", "bool"),
            ("unknown_97", "bool"), ("unknown_98", "bool"), ("level", "u32"),
            ("unknown_204", "optional<0x3A,UserSlotTail204>"));
        PacketLayoutContract.RequireFields(tables, build, "RoomSeatUpdate", "Room records",
            ("slot", "u32"), ("user", "UserSlot"), ("profile_entries", "list<RoomProfileEntry>"));
        PacketLayoutContract.RequireFields(tables, build, "RoomSeatLeave", "Room records",
            ("slot", "u32"), ("user", "UserSlot"));
    }

    internal static Dictionary<string, object?> EquippedObject(RoomMemberView seat) => new()
    {
        ["user_number"] = seat.UserNum, ["character_id"] = seat.CharacterItemId,
        // cn-774 sub_9B82D0 keeps bot equipment by slot; ordinary humans retain the account lookup sentinel.
        ["unknown_16"] = seat.Ai is null ? -1 : seat.Slot,
        ["equipped_items"] = seat.EquippedItemIds.Where(id => id != seat.CharacterItemId).ToArray(),
    };
}
