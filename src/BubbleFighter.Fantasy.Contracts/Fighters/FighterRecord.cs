namespace BubbleFighter.Fantasy.Contracts.Fighters;

/// <summary>
/// One stored fighter slot. <see cref="CharacterType"/> is the FighterConfig Field
/// id (1..31 for the live slot; 0 is legal only on the unused partner slot).
/// <see cref="EquippedItemId"/> is the inferred <c>300000 + CharacterType</c>
/// character-item class, or 0 when none is stored.
/// </summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="Slot">0 = S2C 133 local <c>FighterInfo</c>; 1 = partner slot.</param>
/// <param name="CharacterType">Field id, or 0 on an empty partner slot.</param>
/// <param name="Level"><c>FighterInfo+36</c> (copied to user-info dword [15]).</param>
/// <param name="EquippedItemId">0 or a 300001..300031 character-item id.</param>
/// <param name="Nickname">Display name written on the fighter block.</param>
public sealed record FighterRecord(
    long UserNum,
    byte Slot,
    int CharacterType,
    int Level,
    int EquippedItemId,
    string Nickname);
