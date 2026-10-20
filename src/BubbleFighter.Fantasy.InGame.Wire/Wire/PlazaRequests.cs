using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 401 body from <c>sub_858E70</c> (<c>0x858E70</c>): two <c>u16</c>. Evidence: verified.
/// No S2C room-list pairing is derived.
///
/// </summary>
/// <param name="Unknown0">First <c>u16</c>. <c>DlgRoomListPop</c> writes the lobby page cursor.</param>
/// <param name="Unknown1">Second <c>u16</c>. This builder writes the literal 10.</param>
public readonly record struct Unknown401Request(ushort Unknown0, ushort Unknown1)
{
    /// <summary>Parses a complete C2S 401 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static Unknown401Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var a = r.ReadUInt16();
        var b = r.ReadUInt16();
        WireUtil.RequireConsumed(r, "C2S 401");
        return new Unknown401Request(a, b);
    }
}

/// <summary>
/// C2S 402 create-room body from <c>sub_962200</c> (<c>0x962200</c>): <c>SessionSetting</c>
/// plus a trailing wstring and a <c>0x3A</c>/<c>0xBA</c> tag with an optional <c>i32</c>.
/// Evidence: verified.
///
/// </summary>
/// <param name="Setting">Session prefix, same wire as S2C 117.</param>
/// <param name="Password">
/// Prefixed wstring after the session blob: the room password typed in the create dialog, empty for an
/// open room. <c>sub_855AE0</c> passes the password control text as the second argument of
/// <c>sub_9620B0</c>, and <c>sub_962200</c> writes it after the blob and sets
/// <c>SessionSetting+38</c> when it is non-empty.
/// </param>
/// <param name="OptTag">Tag byte. <c>0x3A</c> means <paramref name="UnknownOpt"/> is present.</param>
/// <param name="UnknownOpt">Optional trailing <c>i32</c> when <paramref name="OptTag"/> is <c>0x3A</c>.</param>
public sealed record Unknown402Request(
    SessionSettingBlob Setting,
    string Password,
    byte OptTag,
    int? UnknownOpt)
{
    /// <summary>Parses a complete C2S 402 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <remarks>
    /// The trailing tag is optional on the wire: a live client sends a 317-byte
    /// body whose session blob and room-name wstring end exactly at the tag's
    /// offset, so there is no tag byte to read. Treating that as absent rather
    /// than as a malformed packet is what keeps the ordinary create path alive -
    /// the strict read closed the connection on a real client
    /// (2026-09-21 live session).
    /// </remarks>
    public static Unknown402Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var setting = SessionSettingBlob.Read(ref r);
        var tail = WireShapes.ReadWide(ref r, "C2S 402 password");
        byte tag;
        int? opt = null;
        if (r.Remaining == 0)
        {
            // No tag byte at all: the body ended after the room name.
            tag = WireShapes.OptionalAbsent402;
        }
        else
        {
            tag = r.ReadByte();
            if (tag == WireShapes.OptionalPresent)
                opt = r.ReadInt32();
            else if (tag != WireShapes.OptionalAbsent402)
                throw new PacketFormatException($"C2S 402 optTag 0x{tag:X2} is not 0x3A or 0xBA.", r.Offset);
        }

        WireUtil.RequireConsumed(r, "C2S 402");
        return new Unknown402Request(setting, tail, tag, opt);
    }
}

/// <summary>
/// C2S 403 join body from <c>sub_854C90</c> (<c>0x854C90</c>): one <c>i32</c> plus a prefixed
/// wstring. Evidence: verified. </summary> <param name="Unknown0"> First dword of the clicked local room row.
/// It is the row's <c>SessionSetting+0</c> value (<c>sub_854C90</c> reads it, <c>:319-320</c> writes it into
/// the packet), which the server set to the room id, so this is the clicked room id - not a row index. </param>
/// <param name="Password"> Prefixed wstring: the password typed in the join prompt (<c>sub_854C90</c>,
/// written at <c>:321-326</c>). The prompt opens only for a row whose <c>SessionSetting+38</c> is set, so an
/// open room is joined with an empty string. </param>
public readonly record struct Unknown403Request(int Unknown0, string Password)
{
    /// <summary>Parses a complete C2S 403 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static Unknown403Request Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadInt32();
        var password = WireShapes.ReadWide(ref r, "C2S 403 password");
        WireUtil.RequireConsumed(r, "C2S 403");
        return new Unknown403Request(unknown0, password);
    }
}
