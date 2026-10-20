using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// The <c>ServerTeamInfo</c> nested blob (deserialize <c>sub_1159550</c> at <c>0x1159550</c>, vtable
/// at <c>0x15B159C</c> slot 1; the writer <c>sub_1159440</c> mirrors the shape). Wire order: a byte-keyed map
/// (<c>u32</c> count; per entry a <c>u8</c> key plus a counted <c>i32</c> vector) followed by two counted
/// <c>i32</c> vectors. An empty map with both vectors empty is the 12-byte form the main-menu path uses.
/// Evidence: verified three-part shape; key and vector meanings unverified. </summary> <param
/// name="TeamSlots">Map entries in wire order; each entry is a key plus its slot vector.</param> <param
/// name="UnknownVecA">First trailing <c>i32</c> vector.</param> <param name="UnknownVecB">Second trailing
/// <c>i32</c> vector.</param>
public sealed record ServerTeamInfoBlob(
    IReadOnlyList<(byte Key, int[] Slots)> TeamSlots,
    int[] UnknownVecA,
    int[] UnknownVecB)
{
    /// <summary>The empty 12-byte form.</summary>
    public static ServerTeamInfoBlob Empty() => new([], [], []);

    /// <summary>Writes the body in wire order.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteUInt32((uint)TeamSlots.Count);
        foreach (var (key, slots) in TeamSlots)
        {
            w.WriteByte(key);
            w.WriteUInt32((uint)slots.Length);
            foreach (var slot in slots)
                w.WriteInt32(slot);
        }

        w.WriteUInt32((uint)UnknownVecA.Length);
        foreach (var value in UnknownVecA)
            w.WriteInt32(value);
        w.WriteUInt32((uint)UnknownVecB.Length);
        foreach (var value in UnknownVecB)
            w.WriteInt32(value);
    }
}
