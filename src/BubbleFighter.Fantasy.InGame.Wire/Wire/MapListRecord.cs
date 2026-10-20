using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 513 body, decoded against the builder <c>sub_10D3F80</c> at
/// <c>0x10D3F80</c>, which <c>sub_5E1490</c> calls after stamping opcode 513.
/// </summary>
/// <remarks>
/// <para>
/// The builder writes three <c>std::wstring</c>s and then thirteen <c>i32</c>s
/// (<c>sub_10D3F80</c>): each wstring is a <c>u32</c> character count
/// followed by that many UTF-16LE code units with no terminator, and two of the
/// trailing integers are widened from <c>u8</c> at <c>a1+72</c>, <c>a1+73</c>,
/// <c>a1+112</c> and <c>a1+113</c>. The widths are <b>verified</b> from the
/// write sites; the meaning of every field past the second wstring is
/// <b>unverified</b>, so they are named <c>unknownN</c> and not interpreted.
/// </para>
/// <para>
/// The reader <c>sub_9D8C00</c> recovers only the first two wstrings and one
/// integer, which is why an earlier pass left the rest of the body unread and
/// concluded the frames did not resolve. The builder settles the shape.
/// </para>
/// </remarks>
/// <param name="Name">First wstring: the selection's display name.</param>
/// <param name="MapKey">Second wstring: the map record key, empty for <c>Random</c>.</param>
/// <param name="SceneKey">Third wstring; <c>Random_Hot</c> on a random selection.</param>
/// <param name="Unknown4">First trailing <c>i32</c>, widened from a <c>u8</c>.</param>
/// <param name="Unknown5">Second trailing <c>i32</c>, widened from a <c>u8</c>.</param>
/// <param name="Unknown6">Third trailing <c>i32</c>.</param>
/// <param name="Unknown7">Fourth trailing <c>i32</c>.</param>
/// <param name="Unknown8">Fifth trailing <c>i32</c>.</param>
/// <param name="Unknown9">Sixth trailing <c>i32</c>.</param>
/// <param name="Unknown10">Seventh trailing <c>i32</c>.</param>
/// <param name="Unknown11">Eighth trailing <c>i32</c>.</param>
/// <param name="Unknown12">Ninth trailing <c>i32</c>.</param>
/// <param name="Unknown13">Tenth trailing <c>i32</c>.</param>
/// <param name="Unknown14">Eleventh trailing <c>i32</c>.</param>
/// <param name="Unknown15">Twelfth trailing <c>i32</c>, widened from a <c>u8</c>.</param>
/// <param name="Unknown16">Thirteenth trailing <c>i32</c>, widened from a <c>u8</c>.</param>
public readonly record struct MapListReport(
    string Name,
    string MapKey,
    string SceneKey,
    int Unknown4,
    int Unknown5,
    int Unknown6,
    int Unknown7,
    int Unknown8,
    int Unknown9,
    int Unknown10,
    int Unknown11,
    int Unknown12,
    int Unknown13,
    int Unknown14,
    int Unknown15,
    int Unknown16)
{
    /// <summary>Parses a complete C2S 513 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static MapListReport Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var name = r.ReadWideStringPrefixed();
        var mapKey = r.ReadWideStringPrefixed();
        var sceneKey = r.ReadWideStringPrefixed();
        var unknown4 = r.ReadInt32();
        var unknown5 = r.ReadInt32();
        var unknown6 = r.ReadInt32();
        var unknown7 = r.ReadInt32();
        var unknown8 = r.ReadInt32();
        var unknown9 = r.ReadInt32();
        var unknown10 = r.ReadInt32();
        var unknown11 = r.ReadInt32();
        var unknown12 = r.ReadInt32();
        var unknown13 = r.ReadInt32();
        var unknown14 = r.ReadInt32();
        var unknown15 = r.ReadInt32();
        var unknown16 = r.ReadInt32();
        return new MapListReport(
            name, mapKey, sceneKey,
            unknown4, unknown5, unknown6, unknown7, unknown8, unknown9,
            unknown10, unknown11, unknown12, unknown13, unknown14, unknown15, unknown16);
    }
}
