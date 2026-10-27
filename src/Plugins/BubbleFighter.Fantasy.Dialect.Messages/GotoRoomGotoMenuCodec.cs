using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Encodes the two-frame main menu of the cn-735 lineage through the same room tables used by legacy
/// room rows: a kind-1 <c>gspGotoRoom</c> (handler <c>cn-735 sub_A23390</c>, parser
/// <c>cn-735 sub_A4D6F0</c>) followed by an empty <c>gspGotoMenu</c> (reader <c>cn-735 sub_A23050</c>).
/// cn-738 and cn-742 share these layouts through their profiles' lineage.
/// </summary>
internal static class GotoRoomGotoMenuCodec
{
    // cn-735 sub_A23390 installs kind 1; cn-735 sub_A23050 then enters the menu stage.
    /// <summary>The native session kind that installs the main-menu room.</summary>
    public const int MainMenuKind = 1;

    /// <summary>
    /// Checks the <c>gspGotoRoom</c> layouts this codec encodes: the cn-735 envelope with its seat and
    /// equipped-item vectors, the <c>kind</c> and <c>game_setting</c> slot of the session setting the
    /// menu installs, the seat and equipped-item fields the player dictionary supplies, and the
    /// one-bool <c>gspGotoMenu</c> body. A field that keeps its name but changes its type fails at
    /// activation instead of when the menu opens.
    /// </summary>
    /// <param name="tables">The packet tables that state each build's layout.</param>
    /// <param name="build">The client build whose layout is checked.</param>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "gspGotoRoom", "GotoRoom+GotoMenu menu",
            ("session_setting", "SessionSetting"),
            ("user_slots", "list<UserSlot>"),
            ("equipped_item_lists", "list<EquippedItemList>"));
        // Requiring game_setting pins GameSetting's existence the way the modern install's SessionSetting
        // contract does; the menu itself writes only the kind into the setting.
        PacketLayoutContract.RequireFields(tables, build, "SessionSetting", "GotoRoom+GotoMenu menu",
            ("kind", "u32"),
            ("game_setting", "GameSetting"));
        PacketLayoutContract.RequireFields(tables, build, "UserSlot", "GotoRoom+GotoMenu menu",
            ("user_number", "u32"),
            ("nickname", "wstr"),
            ("character_type", "u16"),
            ("equipped_items", "list<u32>"));
        PacketLayoutContract.RequireFields(tables, build, "EquippedItemList", "GotoRoom+GotoMenu menu",
            ("user_number", "u32"),
            ("character_item", "u32"),
            ("equipped_items", "list<u32>"));
        PacketLayoutContract.RequireFields(tables, build, "RoomSceneTransition", "GotoRoom+GotoMenu menu",
            ("notice", "bool"));
    }

    internal static byte[] WriteGotoMenu(PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "RoomSceneTransition", new Dictionary<string, object?>());

    internal static byte[] WriteMainMenu(PacketTables tables, string build, MainMenuRoomOpened room)
    {
        var player = new Dictionary<string, object?>
        {
            ["user_number"] = room.UserNumber, ["nickname"] = room.Nickname,
            ["character_type"] = checked((byte)(room.CharacterItemId - 300000)),
            ["character_item"] = room.CharacterItemId, ["equipped_items"] = room.EquippedItemIds,
        };
        var game = PacketMenuProjection.Project(tables, build, "GameSetting", new Dictionary<string, object?>());
        var session = PacketMenuProjection.Project(tables, build, "SessionSetting", new Dictionary<string, object?>
        {
            ["kind"] = MainMenuKind, ["game_setting"] = game,
        });
        var envelope = PacketMenuProjection.Project(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = session,
            ["user_slots"] = new[] { PacketMenuProjection.Project(tables, build, "UserSlot", player) },
            ["equipped_item_lists"] = new[] { PacketMenuProjection.Project(tables, build, "EquippedItemList", player) },
        });
        return PacketCodec.Write(tables, build, "gspGotoRoom", envelope);
    }
}
