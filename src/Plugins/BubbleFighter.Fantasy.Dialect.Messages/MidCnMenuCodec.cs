using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Encodes the menu-only dialect through the same room tables used by legacy room rows.</summary>
internal static class MidCnMenuCodec
{
    // cn-735 sub_A23390 installs kind 1; cn-735 sub_A23050 then enters the menu stage.
    /// <summary>The native session kind that installs the main-menu room.</summary>
    public const int MainMenuKind = 1;

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
