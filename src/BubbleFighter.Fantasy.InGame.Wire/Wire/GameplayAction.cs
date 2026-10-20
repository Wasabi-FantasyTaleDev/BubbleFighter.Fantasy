using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 608 body from the first family of <c>sub_6DDBA0</c> (<c>0x6DDBA0</c>):
/// <c>u8 type, i32, i32, u8, u8, u8, u16, u16</c>. Evidence: verified for those
/// type values the builder writes this way. Other types are not this layout.
/// </summary>
/// <param name="Type">Action type byte.</param>
/// <param name="Unknown1">First <c>i32</c>.</param>
/// <param name="Unknown2">Second <c>i32</c>.</param>
/// <param name="Unknown3">First extra <c>u8</c>.</param>
/// <param name="Unknown4">Second extra <c>u8</c>.</param>
/// <param name="Unknown5">Third extra <c>u8</c>.</param>
/// <param name="Unknown6">First <c>u16</c>.</param>
/// <param name="Unknown7">Second <c>u16</c>.</param>
public readonly record struct GameplayAction(
    byte Type,
    int Unknown1,
    int Unknown2,
    byte Unknown3,
    byte Unknown4,
    byte Unknown5,
    ushort Unknown6,
    ushort Unknown7)
{
    /// <summary>Types for which <c>sub_6DDBA0</c> emits the fixed 608 layout.</summary>
    public static ReadOnlySpan<byte> FixedLayoutTypes =>
    [
        1, 2, 3, 4, 5, 6, 7, 23, 42, 44, 45, 46, 47, 71, 72, 73, 74, 75, 103, 106, 107, 108, 110,
    ];

    /// <summary>Parses a complete C2S 608 payload of the fixed family.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static GameplayAction Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var type = r.ReadByte();
        if (!IsFixedLayout(type))
            throw new PacketFormatException($"C2S 608 type {type} is not the fixed 608 family (see sub_6DDBA0).", r.Offset - 1);
        var a = r.ReadInt32();
        var b = r.ReadInt32();
        var c = r.ReadByte();
        var d = r.ReadByte();
        var e = r.ReadByte();
        var f = r.ReadUInt16();
        var g = r.ReadUInt16();
        WireUtil.RequireConsumed(r, "C2S 608");
        return new GameplayAction(type, a, b, c, d, e, f, g);
    }

    /// <summary>Returns whether <paramref name="type"/> uses the fixed 608 layout.</summary>
    /// <param name="type">The action type byte.</param>
    public static bool IsFixedLayout(byte type)
    {
        foreach (var t in FixedLayoutTypes)
        {
            if (t == type)
                return true;
        }

        return false;
    }
}

/// <summary>
/// C2S 2029 body from <c>sub_708660</c> (<c>0x708660</c>): <c>u8, i32, u8</c>.
/// Evidence: verified. This is a sibling of 608, not a 608 payload.
/// </summary>
/// <param name="Unknown0">Leading <c>u8</c>.</param>
/// <param name="Unknown1"><c>i32</c>.</param>
/// <param name="Unknown2">Trailing <c>u8</c>.</param>
public readonly record struct AlternateAction(byte Unknown0, int Unknown1, byte Unknown2)
{
    /// <summary>Parses a complete C2S 2029 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static AlternateAction Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var a = r.ReadByte();
        var b = r.ReadInt32();
        var c = r.ReadByte();
        WireUtil.RequireConsumed(r, "C2S 2029");
        return new AlternateAction(a, b, c);
    }
}
