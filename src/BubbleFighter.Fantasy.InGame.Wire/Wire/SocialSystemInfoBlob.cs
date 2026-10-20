using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// The <c>SocialSystemInfo</c> nested blob (deserialize <c>sub_113F700</c> at <c>0x113F700</c>,
/// vtable slot 1 on the embedded object at <c>RoomPlayerInfo</c> +240). Twelve scalars, always present, no tags
/// and no counts; an all-zero body is the 48-byte constructor-zero form. Evidence: verified wire order and
/// widths; every meaning is unverified.
/// </summary>
public sealed record SocialSystemInfoBlob(
    int Unknown4,
    int Unknown8,
    int Unknown12,
    int Unknown16,
    int Unknown20,
    int Unknown24,
    int Unknown28,
    int Unknown32,
    long Unknown40,
    int Unknown48,
    int Unknown49)
{
    /// <summary>The all-zero form the client's default-constructed objects carry.</summary>
    public static SocialSystemInfoBlob Empty() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Writes the 48-byte body in wire order.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Unknown4);
        w.WriteInt32(Unknown8);
        w.WriteInt32(Unknown12);
        w.WriteInt32(Unknown16);
        w.WriteInt32(Unknown20);
        w.WriteInt32(Unknown24);
        w.WriteInt32(Unknown28);
        w.WriteInt32(Unknown32);
        w.WriteInt64(Unknown40);
        w.WriteInt32(Unknown48);
        w.WriteInt32(Unknown49);
    }
}
