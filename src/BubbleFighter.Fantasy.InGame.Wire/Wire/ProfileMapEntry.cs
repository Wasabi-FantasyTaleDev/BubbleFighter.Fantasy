using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// One entry of the S2C 117 <c>map564</c> profile map (reader <c>sub_10C4010</c>, appending node +16
/// to the side vector). The client folds each entry into the profile map <c>dword_18E55D8</c> through
/// <c>sub_81D060</c>; that map is the avatar gate for local and remote characters alike. </summary> <remarks>
/// <para> The entry carries the identity, character id and worn items in vecA,
/// including the selected weapons in both the install and the subsequent seat update. An empty vecA clears those items through <c>0x817DF0</c>, even after a complete S2C
/// 806. </para> <para> <c>0x817500</c> replays vecB/vecC before <c>0x817DF0</c> resets equipment and replays
/// vecA through <c>0x817680</c>. The same worn snapshot therefore belongs in vecA for scene maps and in S2C
/// 806's first item vector. </para> </remarks> <param name="Identity">Profile key: the player
/// <c>UserNum</c>.</param> <param name="CharacterId">Equipped character id; must be 300000..301999 to pass the
/// avatar gate.</param> <param name="Slot">Snapshot position the client derives the seat from; defaults to
/// zero.</param> <param name="ModelItemIds"> Legacy experimental vecB input. It is independent of the persisted
/// worn list in vecA. </param> <param name="EquippedItemIds">Worn items written to vecA, excluding the
/// separately encoded character id.</param>
public sealed record ProfileMapEntry(int Identity, int CharacterId, int Slot = 0,
    IReadOnlyList<int>? ModelItemIds = null, IReadOnlyList<int>? EquippedItemIds = null)
{
    /// <summary>The Dao character item id used by the starter profile.</summary>
    public const int DaoCharacterItemId = 300001;

    /// <summary>
    /// Legacy fallback weapon ids for deployments without a mounted inventory selection service.
    ///
    /// </summary>
    public static readonly int[] StarterWeaponIds = [202001, 201001, 207001];

    /// <summary>Writes the record in the wire order the reader expects.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Identity);                       // key
        w.WriteInt32(CharacterId);                    // unknown1
        WriteIdVector(w, EquippedItemIds?.Where(id => id != CharacterId).ToArray());
        WriteIdVector(w, ModelItemIds);               // vecB: erased by sub_818190 later in the same fold
        w.WriteUInt32(0);                             // vecC
        w.WriteUInt32(16);                            // blob: a BYTE count, not an element count
        w.WriteInt32(Identity);                       // unknown236: our own stamp, never read by the client
        w.WriteInt32(Slot);                           // unknown240: our own stamp, never read by the client
        w.WriteInt32(CharacterId);                    // unknown244: our own stamp, never read by the client
        w.WriteInt32(Identity ^ CharacterId ^ Slot);  // unknown248: our own stamp, never read by the client
        w.WriteUInt32(0);                             // items
    }

    /// <summary>
    /// Writes a counted id vector, or an empty one when <paramref name="ids"/> is
    /// null or empty. The reader is a plain <c>u32</c> count followed by that many
    /// <c>i32</c> ids.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="ids">The ids to write, or null for an empty vector.</param>
    private static void WriteIdVector(PacketWriter w, IReadOnlyList<int>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            w.WriteUInt32(0);
            return;
        }

        w.WriteUInt32((uint)ids.Count);
        foreach (var id in ids)
            w.WriteInt32(id);
    }
}
