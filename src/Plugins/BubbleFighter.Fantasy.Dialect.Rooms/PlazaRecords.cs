using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Projects plaza membership through the build's native seat and appearance containers.</summary>
internal static class PlazaRecords
{
    private static bool HasEquippedObjects(PacketTables tables, string build) =>
        tables.Resolve(build, "gspGotoRoom").Any(field => field.Name == "equipped_objects");

    internal static void RequireLayouts(PacketTables tables, string build)
    {
        if (!HasEquippedObjects(tables, build))
        {
            RoomRecords.ValidateLayouts(tables, build);
            return;
        }
        MainMenuInstall.RequireLayouts(tables, build);
        PacketLayoutContract.RequireFields(tables, build, "EquippedObjectEntry", "Plaza appearance",
            ("user_number", "u32"), ("character_id", "u32"), ("equipped_items", "list<u32>"));
        PacketLayoutContract.RequireFields(tables, build, "RoomSeatLeave", "Plaza membership",
            ("slot", "u32"), ("user", "UserSlot"), ("team_info", "ServerTeamInfo"));
    }

    internal static Dictionary<string, object?> Seat(PacketTables tables, string build, RoomMemberView member,
        ISession? recipient = null, IGameText? texts = null)
    {
        if (!HasEquippedObjects(tables, build))
            return RoomRecords.Seat(member, recipient, texts);
        if (member.CharacterItemId is < 300001 or > 301999)
            throw new ArgumentException("A plaza seat requires a resolved native character item.", nameof(member));
        // cn-774 sub_493E20 / tw-454 sub_48BD60 carry the character type at +40; the account identifies the local seat.
        var values = new Dictionary<string, object?>
        {
            ["occupied"] = true, ["user_number"] = member.UserNum,
            ["nickname"] = RoomNames.Resolve(member.Nickname, member.Ai?.NicknameKey ?? "", recipient, texts),
            ["character_type"] = member.CharacterItemId - 300000, ["level"] = Math.Max(1, member.Level),
            // cn-774 sub_8DB6B0 / tw-454 sub_A5B220 install the team and the ready/shop/inventory state.
            ["slot"] = checked((byte)member.Slot), ["team"] = checked((byte)member.Team),
            ["ready"] = member.Ready, ["in_shop"] = member.InShop, ["in_inventory"] = member.InInventory,
            // cn-774 sub_894B80 builds every actor from seat +36 (cn-774 sub_53D670 keeps it at actor +860, which is
            // compared with actor kind ids); send the value the kr-1256 seat writer sends rather than zero.
            ["unknown_36"] = 1,
        };
        if (member.Ai is { } ai)
        {
            // cn-774 sub_8EA170 copies the controller's +7/+8; cn-774 sub_E4E6D0 resolves +284 to that account.
            values["slot"] = checked((byte)ai.ControllerSlot);
            values["user_number"] = ai.ControllerUserNum;
            values["is_ai"] = true;
            values["ai_controller_user_number"] = ai.ControllerUserNum;
            values["ai_difficulty"] = ai.Difficulty;
            values["ai_preset"] = ai.Preset;
        }
        return values;
    }

    internal static byte[] Install(PacketTables tables, string build, IReadOnlyList<RoomMemberView> seats,
        int localSlot, int kind, bool partyTransition)
    {
        RequireLayouts(tables, build);
        if (!HasEquippedObjects(tables, build))
            return RoomRecords.Install(tables, build, new Dictionary<string, object?> { ["kind"] = kind }, seats, 0, localSlot, partyTransition);
        if (seats.Count == 0 || !seats.Any(seat => seat.Slot == localSlot)
            || seats.Any(seat => seat.Slot is < 0 or >= 36)
            || seats.Select(seat => seat.Slot).Distinct().Count() != seats.Count)
            throw new ArgumentException("A plaza install requires unique native seats and its local member.", nameof(seats));
        var bySlot = seats.ToDictionary(seat => seat.Slot);
        var rows = Enumerable.Range(0, seats.Max(seat => seat.Slot) + 1)
            .Select(slot => bySlot.TryGetValue(slot, out var member) ? Seat(tables, build, member)
                : new Dictionary<string, object?> { ["occupied"] = false }).ToArray();
        // cn-774 sub_8D6590 / tw-454 sub_A560E0 install the local slot before the unkeyed appearance vector.
        return PacketCodec.Write(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = PacketMenuProjection.Project(tables, build, "SessionSetting", new Dictionary<string, object?>
            {
                ["room_id"] = 1, ["session_state"] = 1, ["kind"] = kind,
                ["title"] = kind == 1 ? "MainMenu" : "",
                ["game_setting"] = PacketMenuProjection.Project(tables, build, "GameSetting", new Dictionary<string, object?>()),
            }),
            ["user_slots"] = rows,
            ["local_slot"] = localSlot,
            ["party_transition"] = partyTransition,
            ["equipped_objects"] = seats.Select(RoomRecords.EquippedObject).ToArray(),
        });
    }

    internal static byte[] Leave(PacketTables tables, string build, int slot) =>
        HasEquippedObjects(tables, build)
            ? PacketCodec.Write(tables, build, "RoomSeatLeave", new Dictionary<string, object?>
                { ["slot"] = slot, ["user"] = new Dictionary<string, object?> { ["occupied"] = false } })
            : RoomRecords.SeatLeave(tables, build, slot);
}
