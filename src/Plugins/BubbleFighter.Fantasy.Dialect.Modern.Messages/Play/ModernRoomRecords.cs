using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Modern.Messages.Party;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>Supplies typed identity and equipment values to the modern family's audited room tables.</summary>
internal static class ModernRoomRecords
{
    internal static Dictionary<string, object?> Seat(RoomMemberView seat) => new()
    {
        // kr-1256 sub_1143710 converts the occupancy word to truth; the table uses canonical true.
        ["occupied"] = true,
        ["unknown_3"] = checked((byte)seat.Slot),
        ["team"] = checked((byte)seat.Team),
        ["user_number"] = seat.UserNum,
        ["nickname"] = seat.Nickname,
        ["unknown_36"] = 1,
        ["ready"] = seat.Ready,
        ["unknown_97"] = seat.InShop,
        ["unknown_98"] = seat.InInventory,
        // kr-1256 sub_EE9550 uses level - 1; zero makes the level icon disappear.
        ["level"] = Math.Max(1, seat.Level),
    };

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
        };
    }

    internal static Dictionary<string, object?>[] Profiles(IReadOnlyList<RoomMemberView> seats) =>
        seats.Select((seat, position) => Profile(seat, position)).ToArray();

    internal static byte[] Install(PacketTables tables, string build, IReadOnlyDictionary<string, object?> setting,
        IReadOnlyList<RoomMemberView> seats, int masterSlot, int localSlot, bool partyTransition = false)
    {
        // kr-1256 sub_9D9250 supports 36 seats in the plaza and 16 in rooms, with no duplicate indexes.
        if (seats.Any(seat => seat.Slot is < 0 or >= 36) || seats.Select(seat => seat.Slot).Distinct().Count() != seats.Count)
            throw new ArgumentException("Room installs require unique seat indexes below 36.", nameof(seats));
        var count = seats.Count == 0 ? 0 : seats.Max(seat => seat.Slot) + 1;
        var ordered = seats.ToDictionary(seat => seat.Slot);
        var rows = Enumerable.Range(0, count).Select(slot => ordered.TryGetValue(slot, out var seat) ? Seat(seat) : Gap(slot)).ToArray();
        return PacketCodec.Write(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = setting,
            ["master_slot"] = masterSlot,
            ["user_slots"] = rows,
            ["profile_entries"] = Profiles(seats),
            ["local_slot"] = localSlot,
            ["party_transition"] = partyTransition,
        });
    }

    internal static byte[] SeatUpdate(PacketTables tables, string build, RoomMemberView seat, IReadOnlyList<RoomMemberView> seats) =>
        PacketCodec.Write(tables, build, "RoomSeatUpdate", new Dictionary<string, object?>
        {
            ["slot"] = seat.Slot,
            ["user"] = Seat(seat),
            ["profile_entries"] = Profiles(seats),
        });

    internal static byte[] SeatLeave(PacketTables tables, string build, int slot, bool closed = false) =>
        PacketCodec.Write(tables, build, "RoomSeatLeave", new Dictionary<string, object?>
        {
            ["slot"] = slot,
            ["user"] = Vacated(closed),
        });

    internal static byte[] Party(PacketTables tables, string build, RoomMemberView seat, ISession? session = null)
    {
        var party = session is not null && session.TryFeature<IPartyMembership>(out var membership)
            ? membership.Party : new PartySnapshot([new(seat.UserNum, seat.Nickname)]);
        return ModernPartyRecords.Write(tables, build,
            new PartyUpdated(party, new(seat.UserNum, seat.CharacterItemId, seat.EquippedItemIds)));
    }
}
