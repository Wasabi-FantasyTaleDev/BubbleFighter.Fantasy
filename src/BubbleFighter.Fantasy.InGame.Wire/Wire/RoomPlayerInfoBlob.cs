using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// One <c>RoomPlayerInfo</c> seat record (client object 296 bytes; reader <c>sub_1143710</c> at
/// <c>0x1143710</c> with <c>sub_1143B10</c> and <c>sub_10E1B20</c> tails). Wire minimum 188 bytes with empty
/// wstrings, an empty <c>unknown52</c> vector and both optional tags absent. The field order below is the WIRE
/// order, which differs from the object-offset order (for example the +44/+48/+52 reads happen after the
/// +96/+97/+98 reads); the object offset is named on each field. Evidence: verified row-for-row against the
/// reader chain; every meaning except the <c>UserNum</c>/<c>Nickname</c> identity pair is unverified.
/// </summary> <param name="Unknown0"><c>i32</c> as bool at object +0.</param> <param name="Unknown1"><c>i32</c>
/// as bool at object +1.</param> <param name="Unknown2"><c>i32</c> as bool at object +2.</param> <param
/// name="Unknown3"><c>u8</c> at object +3; seat-index meaning inferred.</param> <param
/// name="Unknown4"><c>u8</c> at object +4.</param> <param name="UserNum">Identity <c>i32</c> at object
/// +8.</param> <param name="Nickname">Prefixed wstring at object +12.</param> <param
/// name="Unknown36"><c>u16</c> at object +36.</param> <param name="Unknown40"><c>u16</c> color selector at
/// object +40; consumed through <c>0x9201D0</c> and <c>0x4C13B0</c>.</param> <param name="Unknown96"><c>i32</c>
/// as bool at object +96: ready, checked by <c>0x96DD20</c>.</param> <param name="Unknown97"><c>i32</c> as bool
/// at object +97: shop open, reported by <c>0x9785E0</c>.</param> <param name="Unknown98"><c>i32</c> as bool at
/// object +98: inventory open, reported by <c>0x9785E0</c>.</param> <param name="Unknown44"><c>i32</c> at
/// object +44.</param> <param name="Unknown48"><c>i32</c> at object +48.</param> <param
/// name="Unknown52">Counted <c>i32</c> vector at object +52.</param> <param name="Unknown64"><c>i32</c> at
/// object +64.</param> <param name="Unknown68"><c>i32</c> at object +68.</param> <param
/// name="Unknown88"><c>i32</c> at object +88.</param> <param name="Unknown94"><c>i32</c> as bool at object
/// +94.</param> <param name="Unknown100"><c>i32</c> at object +100.</param> <param name="Unknown104"><c>i32</c>
/// as bool at object +104.</param> <param name="Unknown108"><c>i32</c> at object +108.</param> <param
/// name="Unknown112"><c>i32</c> as bool at object +112.</param> <param name="Unknown116">Prefixed wstring at
/// object +116.</param> <param name="Unknown140"><c>i32</c> at object +140.</param> <param
/// name="Unknown144">Prefixed wstring at object +144.</param> <param name="Unknown168"><c>i32</c> as bool at
/// object +168.</param> <param name="Unknown169"><c>i32</c> as bool at object +169.</param> <param
/// name="Unknown172"><c>i32</c> at object +172.</param> <param name="Unknown176"><c>i32</c> at object
/// +176.</param> <param name="Unknown180"><c>i32</c> at object +180.</param> <param name="Unknown184"><c>u8</c>
/// at object +184.</param> <param name="Unknown185"><c>i32</c> as bool at object +185.</param> <param
/// name="Unknown188"><c>i32</c> at object +188.</param> <param name="Unknown192"><c>i32</c> at object
/// +192.</param> <param name="Unknown196"><c>i32</c> at object +196.</param> <param
/// name="Unknown200"><c>i32</c> at object +200.</param> <param name="Opt204Tag">Optional-block tag; <c>0x3A</c>
/// means the two following <c>i32</c> are present.</param> <param name="Unknown204">Optional <c>i32</c> at
/// object +204; written only when <paramref name="Opt204Tag"/> is <c>0x3A</c>.</param> <param
/// name="Unknown208">Optional <c>i32</c> at object +208; written only when <paramref name="Opt204Tag"/> is
/// <c>0x3A</c>.</param> <param name="Unknown232"><c>i32</c> as bool at object +232.</param> <param
/// name="Social">The 48-byte <c>SocialSystemInfo</c> tail at object +240.</param> <param name="Opt212Tag">
/// Second-level tag the reader consumes immediately after the <c>+204</c> pair; <c>0x3A</c> means the two
/// <c>i32</c> at <c>+212</c> follow. Declared last only because defaulted positional parameters must come after
/// required ones - on the wire it sits inside the optional block, not at the tail. </param> <param
/// name="Unknown212">Optional <c>i32</c> at object +212; written only when <paramref name="Opt212Tag"/> is
/// <c>0x3A</c>.</param> <param name="Unknown216">Optional <c>i32</c> at object +216; written only when
/// <paramref name="Opt212Tag"/> is <c>0x3A</c>.</param>
public sealed record RoomPlayerInfo(
    int Unknown0,
    int Unknown1,
    int Unknown2,
    byte Unknown3,
    byte Unknown4,
    int UserNum,
    string Nickname,
    ushort Unknown36,
    ushort Unknown40,
    int Unknown96,
    int Unknown97,
    int Unknown98,
    int Unknown44,
    int Unknown48,
    int[] Unknown52,
    int Unknown64,
    int Unknown68,
    int Unknown88,
    int Unknown94,
    int Unknown100,
    int Unknown104,
    int Unknown108,
    int Unknown112,
    string Unknown116,
    int Unknown140,
    string Unknown144,
    int Unknown168,
    int Unknown169,
    int Unknown172,
    int Unknown176,
    int Unknown180,
    byte Unknown184,
    int Unknown185,
    int Unknown188,
    int Unknown192,
    int Unknown196,
    int Unknown200,
    byte Opt204Tag,
    int Unknown204,
    int Unknown208,
    int Unknown232,
    SocialSystemInfoBlob Social,
    byte Opt212Tag = 0,
    int Unknown212 = 0,
    int Unknown216 = 0)
{
    /// <summary>Tag value that marks the optional block present.</summary>
    public const byte OptPresentTag = 0x3A;

    /// <summary>
    /// The constructor-zero form with the identity pair filled in: every
    /// scalar zero, wstrings empty, the <c>unknown52</c> vector empty, both
    /// optional tags absent and an all-zero social tail. That is exactly what
    /// the client's default-constructed seat record carries, except the first
    /// dword, which the room-occupancy scan <c>sub_9EE100</c> reads as two
    /// bytes at the seat-array base and must therefore be nonzero.
    ///
    /// </summary>
    /// <param name="userNum">The seat owner identity.</param>
    /// <param name="nickname">The seat owner display name.</param>
    public static RoomPlayerInfo Default(long userNum, string nickname) => ForSeat(userNum, nickname, 0);

    /// <summary>
    /// The vacated-seat record: occupancy (<c>+0</c>) zero, no identity, seat byte <c>+3</c> =
    /// <c>0xFF</c>, <c>+88</c>/<c>+108</c> = -1, <c>+180</c> = 1, optional tag byte <c>0xBA</c>
    /// (not <c>0x3A</c>, so no optional block). <c>sub_9DB1E0</c> takes its unoccupied branch on
    /// this record and removes the seat's scene entity; there is no separate removal opcode.
    /// Every cleared seat is byte-identical apart from the slot index.
    ///
    /// </summary>
    public static RoomPlayerInfo VacatedSeat() => new(
        0, 0, 0, 0xFF, 0, 0, string.Empty, 0, 0, 0, 0, 0, 0, 0, [],
        0, 0, -1, 0, 0, 0, -1, 0, string.Empty, 0, string.Empty, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 0, 0xBA, 0,
        0, 0, SocialSystemInfoBlob.Empty());

    /// <summary>
    /// The seat form of <see cref="Default"/>: the same constructor-zero record with the occupancy
    /// dword derived from the seat index so the room's occupancy scan sees nonzero bytes, and the seat written
    /// into its own <c>u8</c> slot. </summary> <param name="userNum">The seat owner identity.</param> <param
    /// name="nickname">The seat owner display name.</param> <param name="slot">Seat index used by S2C 584 and
    /// the occupancy dword.</param> <param name="level">The seat owner's level, floored at one.</param> <param
    /// name="team">The team label written to <c>unknown4</c>: 0 unassigned, 1 blue, 2 red - the key
    /// <c>sub_980730</c>'s team board switches on.</param> <remarks> The occupancy dword
    /// must keep BOTH of its low bytes nonzero: the scan reads the byte at <c>session+592+320*slot</c> and the
    /// one at <c>+593</c>, which on a little-endian wire are the first and second byte of this dword. <c>0x0101
    /// + slot</c> satisfies that for every seat in the 0..254 range the seat byte can address while staying
    /// unique per seat. <para> <see cref="Unknown1"/> is written as zero, not one: it lands at <c>seat+1</c>,
    /// and <c>sub_980730</c> routes any seat whose <c>seat+1</c> is nonzero into <c>sub_980540(slot, 1)</c>,
    /// which hides the slot's children and never writes its name. Keeping this byte zero therefore avoids
    /// selecting that disabled-seat branch. </para> <para> Three scalar slots retain the fixture values rather
    /// than zero: the <c>+88</c> peer target is <c>-1</c> (no target) because a zero reads as a resolvable
    /// identity and the client then looks up a player that does not exist; the <c>+108</c> status scalar is
    /// <c>-1</c> for the same reason; and <c>+180</c> is the constructor default 1. All three are plain
    /// <c>i32</c> slots in <c>sub_1143710</c> / <c>sub_1143B10</c>, so their widths are verified but their
    /// chosen values remain compatibility defaults, not verified semantics. </para> <para> <c>Unknown4</c>
    /// carries zero in the waiting-room fixture: the field is the team-label key <c>sub_980730</c> switches on
    /// at <c>sub_980730</c> (in mode 1, 1 selects the team-1 board and 0 the waiting form), and the
    /// in-round seat state of <c>slot + 1</c> is not a wire value at all - it is what the client's own S2C 510
    /// handler writes through <c>sub_9E4C80</c> at <c>sub_9E5E70</c>. Carrying <c>slot + 1</c> here made the
    /// waiting room display the team-1 state, the abnormal team of the second entry. </para> <para>
    /// <c>Unknown36</c> retains the existing appearance constructor argument. <c>Unknown40</c> is the color
    /// selector: <c>0x9201D0</c> reads seat+40 and passes it to <c>0x4C13B0</c>, then refreshes the character
    /// materials. Zero selects the native default, matching S2C 105 and FighterInfo until explicit color
    /// ownership and persistence are implemented. </para> <para> <c>+172</c> carries the level floored at one,
    /// because every <c>LevelSymbolIcon</c> reader takes the level from that seat slot (<c>sub_912A00</c>,
    /// <c>sub_783D90</c>, <c>sub_65B190</c>) and <c>sub_EE9550</c> stores <c>level - 1</c> into the icon, so
    /// a zero there builds the icon from <c>-1</c> and the level renders blank. </para> </remarks>
    public static RoomPlayerInfo ForSeat(long userNum, string nickname, byte slot, int level = 1, byte team = 0) => new(
        checked(0x0101 + slot), 0, 0, slot, team, checked((int)userNum), nickname, 1, 0, 0, 0, 0, 0, 0, [],
        0, 0, -1, 0, 0, 0, -1, 0, string.Empty, 0, string.Empty, 0, 0,
        Math.Max(1, level), 0, 1, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, SocialSystemInfoBlob.Empty());

    /// <summary>Writes the record in wire order.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Unknown0);
        w.WriteInt32(Unknown1);
        w.WriteInt32(Unknown2);
        w.WriteByte(Unknown3);
        w.WriteByte(Unknown4);
        w.WriteInt32(UserNum);
        w.WriteWideStringPrefixed(Nickname);
        w.WriteUInt16(Unknown36);
        w.WriteUInt16(Unknown40);
        w.WriteInt32(Unknown96);
        w.WriteInt32(Unknown97);
        w.WriteInt32(Unknown98);
        w.WriteInt32(Unknown44);
        w.WriteInt32(Unknown48);
        w.WriteUInt32((uint)Unknown52.Length);
        foreach (var value in Unknown52)
            w.WriteInt32(value);
        w.WriteInt32(Unknown64);
        w.WriteInt32(Unknown68);
        w.WriteInt32(Unknown88);
        w.WriteInt32(Unknown94);
        w.WriteInt32(Unknown100);
        w.WriteInt32(Unknown104);
        w.WriteInt32(Unknown108);
        w.WriteInt32(Unknown112);
        w.WriteWideStringPrefixed(Unknown116);
        w.WriteInt32(Unknown140);
        w.WriteWideStringPrefixed(Unknown144);
        w.WriteInt32(Unknown168);
        w.WriteInt32(Unknown169);
        w.WriteInt32(Unknown172);
        w.WriteInt32(Unknown176);
        w.WriteInt32(Unknown180);
        w.WriteByte(Unknown184);
        w.WriteInt32(Unknown185);
        w.WriteInt32(Unknown188);
        w.WriteInt32(Unknown192);
        w.WriteInt32(Unknown196);
        w.WriteInt32(Unknown200);
        w.WriteByte(Opt204Tag);
        if (Opt204Tag == OptPresentTag)
        {
            w.WriteInt32(Unknown204);
            w.WriteInt32(Unknown208);
            // sub_1143710 consumes a SECOND tag here and, when that is 0x3A too,
            // two more i32 at +212 (sub_1143710). Omitting the byte left
            // every field after the optional block one byte early in the reader.
            w.WriteByte(Opt212Tag);
            if (Opt212Tag == OptPresentTag)
            {
                w.WriteInt32(Unknown212);
                w.WriteInt32(Unknown216);
            }
        }

        w.WriteInt32(Unknown232);
        Social.Write(w);
    }
}
