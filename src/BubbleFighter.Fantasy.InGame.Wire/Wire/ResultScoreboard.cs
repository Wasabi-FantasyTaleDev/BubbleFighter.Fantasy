using System.Numerics;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// One scoreboard row inside the S2C 614 <c>ScoreData</c> record. The wire shape
/// is the row reader <c>kr-1256 sub_429620</c> (<c>0x429620</c>), the virtual
/// <c>ScoreData::Deserialize</c> behind vftable <c>0x154BE48</c> slot +4 (RTTI
/// <c>.?AVScoreData@@</c>), reached from the record builder <c>kr-1256 sub_10D16A0</c>
/// line 38. Memory stride is 512 B; the wire row is variable length.
/// </summary>
/// <remarks>
/// <para>
/// Wire order (all widths verified at <c>kr-1256 sub_429620</c>): <c>i32</c> to
/// <c>row+500</c>; <c>i32</c> to <c>row+0</c>; wstring to <c>row+4</c>;
/// <c>u8</c> to <c>row+52</c>; 27 x <c>i32</c>; <c>i32</c> bool to
/// <c>row+388</c>; <c>i32</c> to <c>row+436</c>; a <c>u32</c> count plus that
/// many wstrings into the vector at <c>row+464</c> (element reader
/// <c>kr-1256 sub_429E00</c>); wstring to <c>row+440</c>; then
/// the scalar tail listed in the field docs.
/// </para>
/// <para>
/// No client label names any stat integer, so every value field stays
/// <c>unknownN</c> named by its destination offset. The one string at
/// <c>row+4</c> is the row's only display-name-class string (inferred);
/// <c>row+496</c> keeps the constructor-seeded index and is never read from
/// wire (<c>kr-1256 sub_4289F0</c>, verified), so it is not a field here.
/// </para>
/// </remarks>
public sealed record ResultScoreboardRow
{
    /// <summary>The number of <c>i32</c> stat slots the row carries (destinations <c>+56</c>..<c>+384</c>).</summary>
    public const int StatSlotCount = 27;

    /// <summary>The number of <c>i32</c> values in the fixed block written to <c>+192</c>..<c>+220</c>.</summary>
    public const int Block192Count = 8;

    /// <summary>
    /// Wire bytes for a row with every string empty and the vector empty:
    /// 230 B of scalars plus the two 4-byte empty wstring prefixes and the
    /// 4-byte vector count. Every present row costs this plus two bytes per
    /// display-name character plus two bytes per vector character.
    /// </summary>
    public const int EmptyRowByteLength = 242;

    /// <summary>
    /// Gets or sets the first <c>i32</c>, stored at <c>row+500</c>
    /// (<c>kr-1256 sub_429620</c>). Native initializer 0x9E5E70 sets it to 1 for an occupied player.
    /// </summary>
    public int Unknown500 { get; init; }

    /// <summary>
    /// Gets or sets the presence-mask position. The client keeps this index at row+496;
    /// it is implicit in the mask and is not the row's serialized player identity.
    /// </summary>
    public int Slot { get; init; }

    /// <summary>Gets or sets the player identity at row+0, copied from seat+8 by 0x9E5E70 and read by 0x429620.</summary>
    public int UserNumber { get; init; }

    /// <summary>
    /// Gets or sets the wstring stored at <c>row+4</c> (<c>kr-1256 sub_429620</c>),
    /// the row's only display-name-class string (inferred). Written as an
    /// <c>i32</c> character count plus UTF-16LE, no NUL (<c>kr-1256 sub_1126430</c>).
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets or sets the team byte at row+52, initialized from seat+4 by 0x9E5E70 and read by 0x429620.</summary>
    public byte Unknown52 { get; init; }

    /// <summary>
    /// Gets or sets the 27 stat integers, written in wire order to
    /// <c>+56, +60, +64, +68, +80, +72, +84, +88, +92, +96, +104, +100, +108,
    /// +112, +120, +124, +128, +176, +132, +136, +140, +364, +368, +372,
    /// +376, +380, +384</c> (<c>kr-1256 sub_429620</c>; note the wire order is
    /// not the destination order). No client label names any of them.
    /// </summary>
    public int[] UnknownStatBlock { get; init; } = new int[StatSlotCount];

    /// <summary>Gets or sets the <c>i32</c>-as-bool stored at <c>row+388</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown388 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+436</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown436 { get; init; }

    /// <summary>
    /// Gets or sets the counted wstring vector at <c>row+464</c>: a <c>u32</c>
    /// count, then one wstring per entry (element reader <c>kr-1256 sub_429E00</c>,
    /// <c>kr-1256 sub_429E00</c>).
    /// </summary>
    public IReadOnlyList<string> UnknownVector464 { get; init; } = [];

    /// <summary>Gets or sets the wstring stored at <c>row+440</c> (<c>kr-1256 sub_429620</c>).</summary>
    public string Unknown440 { get; init; } = string.Empty;

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+236</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown236 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+476</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown476 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+480</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown480 { get; init; }

    /// <summary>
    /// Gets or sets the <c>i32</c> stored at <c>row+392</c>
    /// (<c>kr-1256 sub_429620</c>): the result grade's sprite frame, 0 (sss) to 8 (f) in the
    /// order of <c>Config/rank_point.xml</c>. The sprite drawer <c>kr-1256 sub_1056E30</c> draws
    /// nothing for a negative frame, and the record constructor initializes the slot to -1
    /// (<c>kr-1256 sub_428620</c>), so -1 is "no grade"; the default is -1 because a zero shows sss.
    /// </summary>
    public int Unknown392 { get; init; } = -1;

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+144</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown144 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+148</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown148 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+408</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown408 { get; init; }

    /// <summary>Gets or sets the <c>u16</c> stored at <c>row+412</c> (<c>kr-1256 sub_429620</c>).</summary>
    public ushort Unknown412 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+416</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown416 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+420</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown420 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+160</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown160 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+164</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown164 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+168</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown168 { get; init; }

    /// <summary>Gets or sets the <c>u8</c> stored at <c>row+172</c> (<c>kr-1256 sub_429620</c>).</summary>
    public byte Unknown172 { get; init; }

    /// <summary>Gets or sets the <c>u8</c> stored at <c>row+173</c> (<c>kr-1256 sub_429620</c>).</summary>
    public byte Unknown173 { get; init; }

    /// <summary>Gets or sets the <c>u16</c> stored at <c>row+184</c> (<c>kr-1256 sub_429620</c>).</summary>
    public ushort Unknown184 { get; init; }

    /// <summary>Gets or sets the <c>u16</c> stored at <c>row+76</c> (<c>kr-1256 sub_429620</c>).</summary>
    public ushort Unknown76 { get; init; }

    /// <summary>Gets or sets the <c>u16</c> stored at <c>row+186</c> (<c>kr-1256 sub_429620</c>).</summary>
    public ushort Unknown186 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+508</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown508 { get; init; }

    /// <summary>Gets or sets the <c>u16</c> stored at <c>row+188</c> (<c>kr-1256 sub_429620</c>).</summary>
    public ushort Unknown188 { get; init; }

    /// <summary>Gets or sets the <c>u8</c> stored at <c>row+432</c> (<c>kr-1256 sub_429620</c>).</summary>
    public byte Unknown432 { get; init; }

    /// <summary>
    /// Gets or sets the eight <c>i32</c> written to <c>+192, +196, +200, +204,
    /// +208, +212, +216, +220</c> (<c>kr-1256 sub_429620</c>).
    /// </summary>
    public int[] UnknownBlock192 { get; init; } = new int[Block192Count];

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+424</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown424 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>row+428</c> (<c>kr-1256 sub_429620</c>).</summary>
    public int Unknown428 { get; init; }

    /// <summary>
    /// Builds one synthesized row for a seat: the seat's slot at <c>row+0</c>,
    /// its nickname at <c>row+4</c>, and its C2S 103 round counters copied
    /// positionally into the leading stat slots where a report arrived. Every
    /// other field stays at the client default (zero; the vector empty).
    /// </summary>
    /// <param name="slot">The seat's slot, also the row's presence-mask bit.</param>
    /// <param name="displayName">The seat's nickname.</param>
    /// <param name="roundStats">
    /// The seat's C2S 103 counters (<c>RoomSeat.RoundStats</c>), or
    /// <see langword="null"/> when none arrived this round. The first
    /// <see cref="StatSlotCount"/> values are copied in order into the stat
    /// block; the mapping is positional and unverified - the client code proves the
    /// client scoreboard never reads those counters, which is why the whole record ships
    /// behind an off-by-default option.
    /// </param>
    /// <returns>The row.</returns>
    public static ResultScoreboardRow ForSeat(int slot, string displayName, IReadOnlyList<int>? roundStats)
    {
        var stats = new int[StatSlotCount];
        if (roundStats is not null)
        {
            for (var i = 0; i < stats.Length && i < roundStats.Count; i++)
                stats[i] = roundStats[i];
        }

        return new ResultScoreboardRow { Slot = slot, DisplayName = displayName, UnknownStatBlock = stats };
    }

    /// <summary>Writes this row in wire order.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        if (UnknownStatBlock.Length != StatSlotCount)
        {
            throw new InvalidOperationException(
                $"The scoreboard stat block must carry exactly {StatSlotCount} i32 values; got {UnknownStatBlock.Length}.");
        }

        if (UnknownBlock192.Length != Block192Count)
        {
            throw new InvalidOperationException(
                $"The scoreboard +192 block must carry exactly {Block192Count} i32 values; got {UnknownBlock192.Length}.");
        }

        if (UnknownVector464.Count > WireShapes.MaxVectorCount)
        {
            throw new InvalidOperationException(
                $"The scoreboard row vector count {UnknownVector464.Count} exceeds {WireShapes.MaxVectorCount}.");
        }

        w.WriteInt32(Unknown500);
        w.WriteInt32(UserNumber);
        w.WriteWideStringPrefixed(DisplayName);
        w.WriteByte(Unknown52);
        foreach (var value in UnknownStatBlock)
            w.WriteInt32(value);
        w.WriteInt32(Unknown388);
        w.WriteInt32(Unknown436);
        w.WriteUInt32((uint)UnknownVector464.Count);
        foreach (var entry in UnknownVector464)
            w.WriteWideStringPrefixed(entry);
        w.WriteWideStringPrefixed(Unknown440);
        w.WriteInt32(Unknown236);
        w.WriteInt32(Unknown476);
        w.WriteInt32(Unknown480);
        w.WriteInt32(Unknown392);
        w.WriteInt32(Unknown144);
        w.WriteInt32(Unknown148);
        w.WriteInt32(Unknown408);
        w.WriteUInt16(Unknown412);
        w.WriteInt32(Unknown416);
        w.WriteInt32(Unknown420);
        w.WriteInt32(Unknown160);
        w.WriteInt32(Unknown164);
        w.WriteInt32(Unknown168);
        w.WriteByte(Unknown172);
        w.WriteByte(Unknown173);
        w.WriteUInt16(Unknown184);
        w.WriteUInt16(Unknown76);
        w.WriteUInt16(Unknown186);
        w.WriteInt32(Unknown508);
        w.WriteUInt16(Unknown188);
        w.WriteByte(Unknown432);
        foreach (var value in UnknownBlock192)
            w.WriteInt32(value);
        w.WriteInt32(Unknown424);
        w.WriteInt32(Unknown428);
    }
}

/// <summary>
/// One entry of the trailing <c>u32</c>-counted container the S2C 614 stream
/// carries after the two tail <c>i32</c> values, spliced into the record at
/// <c>+19188</c>: reader <c>kr-1256 sub_10D2220</c> -&gt; <c>kr-1256 sub_10D30B0</c> reads a
/// count, then per entry three <c>u32</c> values (<c>kr-1256 sub_10D30B0</c>); the
/// dispatcher pre-places the list head (<c>kr-1256 sub_9E2FE0</c>) and the consumer
/// <c>kr-1256 sub_9E9A60</c> splices it into session <c>+1720</c>
/// (<c>kr-1256 sub_9E9A60</c>). Verified; no entry field has a recovered meaning.
/// </summary>
/// <param name="Unknown0">First <c>u32</c> of the entry.</param>
/// <param name="Unknown1">Second <c>u32</c> of the entry.</param>
/// <param name="Unknown2">Third <c>u32</c> of the entry.</param>
public readonly record struct ResultScoreboardContainerEntry(uint Unknown0, uint Unknown1, uint Unknown2)
{
    /// <summary>Writes this entry in wire order.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteUInt32(Unknown0);
        w.WriteUInt32(Unknown1);
        w.WriteUInt32(Unknown2);
    }
}

/// <summary>
/// The complete S2C 614 body: one <c>ScoreData</c> record deserialized by the
/// record's own virtual <c>kr-1256 sub_429620</c> inside dispatcher case <c>0x266</c>
/// (<c>kr-1256 sub_10D16A0</c>), consumed by <c>kr-1256 sub_9E9A60</c> which swaps the record
/// into session <c>+1720</c>. Everything arrives inside the single 614 body -
/// rows are counted by a <c>u64</c> presence mask, not fixed at the memory
/// image's 36 x 512 B.
/// </summary>
/// <remarks>
/// <para>
/// Wire order (offsets are destinations inside <c>ScoreData</c>; grades per the
/// doc's table, all transport verified): <c>u8</c> head count (to <c>+4</c>,
/// sizes only the three head arrays); <c>u8</c> winner slot (to <c>+452</c>,
/// kept by the consumer only when &lt;= 36, else -1, <c>kr-1256 sub_429620</c>);
/// <c>i32</c> banner gate (to <c>+456</c>, <c>kr-1256 sub_429620</c>); three head
/// arrays of that many <c>i32</c> (to <c>+12</c>, <c>+160</c>, <c>+308</c>,
/// <c>kr-1256 sub_429620</c>); the <c>u64</c> presence mask;
/// per set bit ascending, one row block (to <c>+460+512i</c>) then one
/// <c>i32</c> (to <c>+18892+4i</c>) and one <c>i32</c> (to <c>+19036+4i</c>,
/// <c>kr-1256 sub_429620</c>); two <c>i32</c> (to <c>+19180/+19184</c>); the
/// counted container (to <c>+19188</c>); the section-2 tail - two <c>u8</c>
/// the consumer copies to <c>GameInfo+308/+312</c> (<c>kr-1256 sub_9E9A60</c>), a
/// <c>u16</c> flags at <c>+19272</c> whose bit 2 gates three wstrings written
/// in wire order 1st/3rd/2nd to <c>+19200/+19248/+19224</c>
/// (<c>kr-1256 sub_10D16A0</c>), and a trailing optional tag whose present value
/// 0x3A adds three <c>i32</c> (<c>kr-1256 sub_10D16A0</c>).
/// </para>
/// <para>
/// An empty record (no head entries, clear mask) is exactly 31 bytes and each
/// present row adds 242 plus twice its display-name characters, plus 8 bytes
/// of mask-gated integers and 12 bytes per container entry.
/// </para>
/// </remarks>
public sealed record ResultScoreboard
{
    /// <summary>
    /// Gets or sets the head array landing at <c>ScoreData+12</c>. Its length
    /// is the wire's leading <c>u8</c> and must equal the other two head
    /// arrays' lengths.
    /// </summary>
    public int[] HeadUnknown12 { get; init; } = [];

    /// <summary>Gets or sets the head array landing at <c>ScoreData+160</c>; see <see cref="HeadUnknown12"/>.</summary>
    public int[] HeadUnknown160 { get; init; } = [];

    /// <summary>Gets or sets the head array landing at <c>ScoreData+308</c>; see <see cref="HeadUnknown12"/>.</summary>
    public int[] HeadUnknown308 { get; init; } = [];

    /// <summary>
    /// Gets the wire's leading <c>u8</c>: the common length of the three head
    /// arrays, stored by the consumer at <c>ScoreData+4</c> sign-extended
    /// (<c>kr-1256 sub_429620,61</c>). It sizes only the head arrays - the row
    /// blocks are gated by <see cref="PresenceMask"/>, not by this count, and
    /// the two are proven independent.
    /// </summary>
    public int HeadCount => HeadUnknown12.Length;

    /// <summary>
    /// Gets or sets the winner slot byte, stored at <c>ScoreData+452</c>; the
    /// consumer keeps it only when &lt;= 36 and otherwise stores -1
    /// (<c>kr-1256 sub_429620,56-60</c>). Zero here means no winner is claimed: the
    /// emulator derives none, and the banner stays off through
    /// <see cref="BannerGate"/> anyway.
    /// </summary>
    public byte WinnerSlot { get; init; }

    /// <summary>
    /// Gets or sets the <c>i32</c>-as-bool banner gate stored at
    /// <c>ScoreData+456</c> (<c>kr-1256 sub_429620</c>): the same field
    /// <c>ScoreData+456</c> that gates the type-19 result banner block
    /// in the kr-1256 result handler. Zero keeps the banner off.
    /// </summary>
    public int BannerGate { get; init; }

    /// <summary>
    /// Gets or sets the <c>u64</c> presence mask (<c>kr-1256 sub_429620</c>). Bit
    /// <c>i</c> set means the stream carries row <c>i</c>'s block (which lands
    /// at <c>+460+512i</c>) plus one mask-gated <c>i32</c> in each of the two
    /// trailing arrays.
    /// </summary>
    public ulong PresenceMask { get; init; }

    /// <summary>
    /// Gets or sets the row blocks, one per set mask bit in ascending bit
    /// order (<c>kr-1256 sub_429620</c>). The count must equal the mask's
    /// population.
    /// </summary>
    public IReadOnlyList<ResultScoreboardRow> Rows { get; init; } = [];

    /// <summary>
    /// Gets or sets one <c>i32</c> per set mask bit, stored at
    /// <c>+18892+4i</c> (<c>kr-1256 sub_429620</c>).
    /// </summary>
    public int[] Unknown18892 { get; init; } = [];

    /// <summary>
    /// Gets or sets one <c>i32</c> per set mask bit, stored at
    /// <c>+19036+4i</c> (<c>kr-1256 sub_429620</c>).
    /// </summary>
    public int[] Unknown19036 { get; init; } = [];

    /// <summary>Gets or sets the <c>i32</c> stored at <c>+19180</c> (<c>kr-1256 sub_10D16A0</c>); consumed by the tail renderers only under the session mode-flag bit 0 (mapping inferred).</summary>
    public int Unknown19180 { get; init; }

    /// <summary>Gets or sets the <c>i32</c> stored at <c>+19184</c> (<c>kr-1256 sub_10D16A0</c>); see <see cref="Unknown19180"/>.</summary>
    public int Unknown19184 { get; init; }

    /// <summary>Gets or sets the trailing counted container spliced at <c>+19188</c> (reader <c>kr-1256 sub_10D30B0</c>).</summary>
    public IReadOnlyList<ResultScoreboardContainerEntry> Container19188 { get; init; } = [];

    /// <summary>Gets or sets the first tail <c>u8</c>, copied by the consumer to <c>GameInfo+308</c> (<c>kr-1256 sub_9E9A60</c>).</summary>
    public byte Unknown19196 { get; init; }

    /// <summary>Gets or sets the second tail <c>u8</c>, copied by the consumer to <c>GameInfo+312</c> (<c>kr-1256 sub_9E9A60</c>).</summary>
    public byte Unknown19197 { get; init; }

    /// <summary>
    /// Gets or sets the <c>u16</c> flags stored at <c>+19272</c>
    /// (<c>kr-1256 sub_10D16A0</c>). Bit 2 gates the three wstrings; the emit
    /// recipe carries zero.
    /// </summary>
    public ushort Flags19272 { get; init; }

    /// <summary>
    /// Gets the <c>u16</c> flag bit that gates the three tail wstrings
    /// (<c>kr-1256 sub_10D16A0</c>, verified).
    /// </summary>
    public static ushort FlagsBitThreeStrings => 0x4;

    /// <summary>Gets or sets the first gated wstring, stored at <c>+19200</c> and written first on the wire.</summary>
    public string Unknown19200 { get; init; } = string.Empty;

    /// <summary>Gets or sets the second gated wstring on the wire, stored at <c>+19248</c> (wire order is 1st/3rd/2nd against the destination order).</summary>
    public string Unknown19248 { get; init; } = string.Empty;

    /// <summary>Gets or sets the third gated wstring on the wire, stored at <c>+19224</c>.</summary>
    public string Unknown19224 { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the trailing optional tag (<c>kr-1256 sub_10D16A0</c>). The
    /// value <see cref="WireShapes.OptionalPresent"/> (58) makes the consumer
    /// read three more <c>i32</c> and raise the present flag at <c>+19276</c>;
    /// any other value ends the record. The emit recipe carries a value other
    /// than 58, so zero.
    /// </summary>
    public byte OptionalTag { get; init; }

    /// <summary>Gets or sets the first tagged <c>i32</c>, read only when <see cref="OptionalTag"/> is 58; consumed at the tail end (<c>+19280</c> band).</summary>
    public int Optional19280 { get; init; }

    /// <summary>Gets or sets the second tagged <c>i32</c>; see <see cref="Optional19280"/>.</summary>
    public int Optional19284 { get; init; }

    /// <summary>Gets or sets the third tagged <c>i32</c>; see <see cref="Optional19280"/>.</summary>
    public int Optional19288 { get; init; }

    /// <summary>
    /// Builds a record carrying exactly one synthesized row per seat: bit
    /// <c>seat.Slot</c> set in the presence mask, rows in ascending slot
    /// order, one zero in each mask-gated array per row, and the tail the
    /// emit recipe prescribes (two zero <c>i32</c>, empty container, zero
    /// <c>u8</c> pair, zero flags, absent optional tag). The three head
    /// arrays stay empty, so the wire's leading count is zero.
    /// </summary>
    /// <param name="rows">One row per occupied seat, each built by <see cref="ResultScoreboardRow.ForSeat"/>.</param>
    /// <returns>The record.</returns>
    public static ResultScoreboard FromRows(IEnumerable<ResultScoreboardRow> rows)
    {
        var ordered = rows.OrderBy(r => r.Slot).ToArray();
        ulong mask = 0;
        foreach (var row in ordered)
        {
            if (row.Slot is < 0 or > 63)
            {
                throw new InvalidOperationException(
                    $"Scoreboard row slot {row.Slot} cannot be a presence-mask bit.");
            }

            var bit = 1UL << row.Slot;
            if ((mask & bit) != 0)
            {
                throw new InvalidOperationException(
                    $"Scoreboard row for slot {row.Slot} appears more than once.");
            }

            mask |= bit;
        }

        var gated = new int[BitOperations.PopCount(mask)];
        return new ResultScoreboard
        {
            PresenceMask = mask,
            Rows = ordered,
            Unknown18892 = gated,
            Unknown19036 = new int[gated.Length],
        };
    }

    /// <summary>Writes the complete S2C 614 body in wire order, failing loud on any shape that would desynchronize the record reader.</summary>
    /// <param name="w">Destination writer.</param>
    public void Write(PacketWriter w)
    {
        var head = HeadUnknown12.Length;
        if (HeadUnknown160.Length != head || HeadUnknown308.Length != head)
        {
            throw new InvalidOperationException(
                "The three scoreboard head arrays must share one length: the wire carries a single u8 count that sizes all of them.");
        }

        if (head > byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"The scoreboard head count {head} does not fit the wire's u8.");
        }

        var population = BitOperations.PopCount(PresenceMask);
        if (Rows.Count != population || Unknown18892.Length != population || Unknown19036.Length != population)
        {
            throw new InvalidOperationException(
                $"The scoreboard rows ({Rows.Count}), +18892 array ({Unknown18892.Length}) and +19036 array "
                + $"({Unknown19036.Length}) must all match the presence-mask population {population}.");
        }

        if (Container19188.Count > WireShapes.MaxVectorCount)
        {
            throw new InvalidOperationException(
                $"The scoreboard container count {Container19188.Count} exceeds {WireShapes.MaxVectorCount}.");
        }

        if (OptionalTag != WireShapes.OptionalPresent
            && (Optional19280 != 0 || Optional19284 != 0 || Optional19288 != 0))
        {
            throw new InvalidOperationException(
                "The tagged scoreboard tail carries values but its optional tag does not announce them; "
                + "the reader would desynchronize.");
        }

        w.WriteByte((byte)head);
        w.WriteByte(WinnerSlot);
        w.WriteInt32(BannerGate);
        // The client code reads the three head arrays index-interleaved - one a[i],
        // b[i], c[i] per loop iteration (sub_429620) - so the wire carries
        // them interleaved, not as three consecutive arrays. Byte-identical
        // only when the values are equal or the count is under two.
        for (var i = 0; i < head; i++)
        {
            w.WriteInt32(HeadUnknown12[i]);
            w.WriteInt32(HeadUnknown160[i]);
            w.WriteInt32(HeadUnknown308[i]);
        }
        // The mask is an unsigned 64-bit field on the wire; the writer has no
        // u64 primitive, and the little-endian bytes of the unchecked cast are
        // identical (the same form SeatInfo's u64 seat mask uses).
        w.WriteInt64(unchecked((long)PresenceMask));
        foreach (var row in Rows)
            row.Write(w);
        foreach (var value in Unknown18892)
            w.WriteInt32(value);
        foreach (var value in Unknown19036)
            w.WriteInt32(value);
        w.WriteInt32(Unknown19180);
        w.WriteInt32(Unknown19184);
        w.WriteUInt32((uint)Container19188.Count);
        foreach (var entry in Container19188)
            entry.Write(w);
        w.WriteByte(Unknown19196);
        w.WriteByte(Unknown19197);
        w.WriteUInt16(Flags19272);
        if ((Flags19272 & FlagsBitThreeStrings) != 0)
        {
            w.WriteWideStringPrefixed(Unknown19200);
            w.WriteWideStringPrefixed(Unknown19248);
            w.WriteWideStringPrefixed(Unknown19224);
        }

        w.WriteByte(OptionalTag);
        if (OptionalTag == WireShapes.OptionalPresent)
        {
            w.WriteInt32(Optional19280);
            w.WriteInt32(Optional19284);
            w.WriteInt32(Optional19288);
        }
    }
}
