using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Retains the native creator's opaque settings and overlays the shared room's committed identity.</summary>
internal static class RoomProjection
{
    internal static RoomCreateRequested Create(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        ValidateLayouts(tables, build);
        var fields = RoomRequests.ReadCreate(tables, build, body);
        var setting = Map(fields["session_setting"]);
        var game = Map(setting["game_setting"]);
        var settings = new RoomBuildSettings(build, PacketCodec.Write(tables, build, "GameSetting", game))
        {
            SessionPrefix = PacketCodec.Write(tables, build, "SessionSetting", setting),
        };
        // kr-1256 sub_962200 writes the title at SessionSetting+8; the trailing string is the password.
        var projection = RoomProjectionFields.Load(tables, build);
        return new((string)setting["title"]!, (string)fields["password"]!, projection.ReadMode(game),
            projection.ReadMap(game), settings) { UpdateExistingTitle = true };
    }

    internal static Dictionary<string, object?> Session(PacketTables tables, string build, RoomView room)
    {
        ValidateLayouts(tables, build);
        var source = room.BuildSettings;
        if (source is null || source.Lineage != build || source.SessionPrefix.IsEmpty || source.Bytes.IsEmpty)
            throw new InvalidOperationException("Modern room entry requires its own encoded session and game settings.");
        var setting = PacketCodec.Read(tables, build, "SessionSetting", source.SessionPrefix.Span);
        var game = PacketCodec.Read(tables, build, "GameSetting", source.Bytes.Span);
        // The master's map change (C2S gcpRoomChangeGameSetting) lies over the creator's block
        // (builds.yaml room_setting_change_map).
        if (!source.Change.IsEmpty)
        {
            var change = RoomSettingChangeMap.Load(tables, build)
                ?? throw new InvalidOperationException("A retained map change requires the build's room_setting_change_map.");
            change.Apply(game, change.Read(source.Change.Span));
        }
        // The create request carries the client's startup map (kr-1256 sub_9620B0 copies it from the startup
        // configuration), never a choice, so until the master picks a map the room shows the build's all-random
        // entry (builds.yaml room_unchosen_map and room_unchosen_difficulty).
        else
            RoomUnchosenMap.Load(tables, build)?.Apply(game, room.ModeId);
        if (source.RoundSelections.Count > 0)
        {
            var projection = RoomRoundSettingFields.Load(tables, build)
                ?? throw new InvalidOperationException("Round selections require an audited packet-field projection.");
            foreach (var selection in source.RoundSelections)
            {
                var decoded = projection.Read(selection.Bytes.Span);
                if (decoded.Index != selection.Index)
                    throw new InvalidOperationException("Retained round selection index disagrees with its native body.");
                projection.Apply(game, selection.Index, decoded.First, decoded.Second);
            }
        }
        RoomProjectionFields.Load(tables, build).Apply(game, room.ModeId);
        // kr-1256 sub_970590 requires waiting state 2; sub_9AC490 selects the room stage for kind 2.
        setting["room_id"] = checked((ushort)room.RoomId);
        setting["session_state"] = 2;
        setting["title"] = room.Name;
        setting["kind"] = 2;
        // kr-1256 sub_9681C0 offers the add-computer-player panel only when the room's session word at +72 has its
        // lowest bit set (builds.yaml room_ai_seat).
        RoomAiSeatFlag.Load(tables, build)?.Apply(setting, room.ModeId);
        setting["game_setting"] = game;
        return setting;
    }

    internal static Dictionary<string, object?> Map(object? value) => value as Dictionary<string, object?>
        ?? throw new PacketFormatException("Expected a nested packet map.", 0);

    /// <summary>
    /// Checks the rows the projection overlays: the session words the waiting room reads back (the join request
    /// returns the room id, kr-1256 sub_854C90; the stage select reads the kind, kr-1256 sub_9AC490) and the game
    /// words the room gates test (kr-1256 sub_97C610, sub_9201D0 and sub_96F3A0 need the flag word, the referee
    /// byte and the channel word as stated, and the mode is the word the list row and the settings change carry).
    /// This helper has no bind step of its own, so it checks before every use rather than on the first send.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    internal static void ValidateLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "SessionSetting", "Room projection",
            ("room_id", "u16"), ("session_state", "u32"), ("title", "wstr"), ("kind", "u32"),
            ("game_setting", "GameSetting"));
        _ = RoomProjectionFields.Load(tables, build);
    }

    internal static IReadOnlyList<RoomMemberView> Members(RoomView room)
    {
        if (room.MemberOrder is not { } order)
            return room.Seats;
        var bySlot = room.Seats.ToDictionary(seat => seat.Slot);
        if (order.Count != bySlot.Count || order.Distinct().Count() != order.Count || order.Any(slot => !bySlot.ContainsKey(slot)))
            throw new InvalidOperationException("The room's profile order must name every occupied seat exactly once.");
        return order.Select(slot => bySlot[slot]).ToArray();
    }

}
