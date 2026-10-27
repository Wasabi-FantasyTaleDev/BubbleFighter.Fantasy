namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>One seat identity in a room user list.</summary>
/// <param name="UserNum">Identity written into <c>RoomPlayerInfo</c> +8.</param>
/// <param name="Nickname">Display name shown on the seat's member row.</param>
/// <param name="Slot">Seat index used by S2C 584 and the occupancy dword.</param>
/// <param name="Level">Player level used to display the seat's level icon; at least one.</param>
/// <param name="Team">Current team selector.</param>
/// <param name="Ready">Current ready toggle, as reported by C2S 505.</param>
/// <param name="InShop">Whether the player's shop dialog is open.</param>
/// <param name="InInventory">Whether the player's inventory dialog is open.</param>
public readonly record struct RoomPlayerIdentity(int UserNum, string Nickname, byte Slot, int Level = 1,
    byte Team = 0, bool Ready = false, bool InShop = false, bool InInventory = false);

