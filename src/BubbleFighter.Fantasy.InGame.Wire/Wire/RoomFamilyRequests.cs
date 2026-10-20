using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 108 training-ground create request. Builder <c>sub_892E10</c> at <c>0x892E10</c> (the
/// <c>btnGoToTrainingRoom</c> click arm, gated by <c>sub_99D510(3)</c> and <c>sub_939CD0</c>): the 56-byte rule
/// record <c>sub_AADD50</c> builds, written by <c>sub_10D26C0</c> as thirteen <c>i32</c> plus one <c>u8</c>
/// bitfield. Body: 53 bytes, no <c>SessionSetting</c>, no <c>MapMeta</c>, no mode id and no map name. Evidence:
/// verified wire order and widths. </summary> <remarks> The rule record carries per-mode rule values, not the
/// mode id. The single-player training ground is mode 40: <c>sub_1134B50</c> maps 40 to <c>wgtTrainingRoom</c>
/// and the map record is <c>rel_trainingRoom01</c>. The mode and the map name are therefore server-synthesized,
/// not read off the wire. </remarks> <param name="Rules">The 56-byte rule record.</param>
public sealed record TrainingRoomRequest(RoomRuleRecord Rules)
{
    /// <summary>Wire length of the request body: thirteen <c>i32</c> plus one <c>u8</c>.</summary>
    public const int BodyBytes = 13 * 4 + 1;

    /// <summary>Parses a complete C2S 108 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <exception cref="PacketFormatException">Thrown when the body is not exactly 53 bytes.</exception>
    public static TrainingRoomRequest Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != BodyBytes)
            throw new PacketFormatException(
                $"C2S 108 has {payload.Length} byte(s); the training-ground builder writes {BodyBytes}.",
                payload.Length);
        var r = new PacketReader(payload);
        var rules = RoomRuleRecord.Read(ref r);
        WireUtil.RequireConsumed(r, "C2S 108");
        return new TrainingRoomRequest(rules);
    }
}

/// <summary>
/// C2S 503 create-room request. Builder <c>sub_9D7570</c> at <c>0x9D7570</c>
/// (callers <c>sub_9A0880</c> / <c>sub_9A0C70</c>, the create-room paths):
/// a zero-extended <c>u8</c>, three prefixed wstrings, one <c>i32</c>, the
/// 56-byte rule record written by <c>sub_10D26C0</c>, and a trailing
/// <c>i32</c>. Evidence: verified wire order and widths.
///
/// </summary>
/// <remarks>
/// The nested record's wire order matches its object offsets (the reader
/// writes it sequentially), and the per-mode key mapping in
/// <c>sub_AADD50</c> is verified: <c>EndGameTimeOverTick</c>, <c>EndGameNotifyWinOrLoseTickTeam</c>,
/// <c>EndGameNotifyWinOrLoseTickStar</c>, <c>EndGameNotifyResultTick</c>,
/// <c>FeverTimeDuration</c>, <c>StarDropTimeDuration</c>,
/// <c>DeathMatchKillLimit</c>, <c>DeathMatchTimeLimit</c> (x1000), <c>TimeAttackTimeLimit</c>
/// (x1000), <c>ConquerTimeLimit</c> (x1000), <c>StarModeTimeLimit</c> (x1000),
/// two unnamed dwords, and the +52 bitfield. The record therefore carries
/// per-mode rule values, not the mode id: which request field (or config
/// lookup) supplies the mode id is unverified —
/// </remarks>
/// <param name="Unknown0">Leading value written from a source byte.</param>
/// <param name="Unknown1">First prefixed wstring.</param>
/// <param name="Unknown2">Second prefixed wstring.</param>
/// <param name="Unknown3">Third prefixed wstring.</param>
/// <param name="Unknown4"><c>i32</c> before the rule record.</param>
/// <param name="Rules">The 56-byte rule record.</param>
/// <param name="Unknown5">Trailing <c>i32</c>.</param>
public sealed record CreateRoomRequest(
    int Unknown0,
    string Unknown1,
    string Unknown2,
    string Unknown3,
    int Unknown4,
    RoomRuleRecord Rules,
    int Unknown5)
{
    /// <summary>Parses a complete C2S 503 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static CreateRoomRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var u0 = r.ReadInt32();
        var s1 = r.ReadWideStringPrefixed();
        var s2 = r.ReadWideStringPrefixed();
        var s3 = r.ReadWideStringPrefixed();
        var u4 = r.ReadInt32();
        var rules = RoomRuleRecord.Read(ref r);
        var u5 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 503");
        return new CreateRoomRequest(u0, s1, s2, s3, u4, rules, u5);
    }
}

/// <summary>
/// The 56-byte rule record written by <c>sub_10D26C0</c> at <c>0x10D26C0</c> (thirteen little-endian
/// <c>i32</c> plus a trailing <c>u8</c>). Named fields follow the verified <c>kr-1256 sub_AADD50</c> config-key
/// mapping; unnamed positions stay <c>unknownN</c>. Evidence: verified widths and order, verified key names.
///
/// </summary>
public sealed record RoomRuleRecord(
    int EndGameTimeOver,
    int EndGameNotifyWinOrLoseTickTeam,
    int EndGameNotifyWinOrLoseTickStar,
    int EndGameNotifyResultTick,
    int FeverTimeDuration,
    int StarDropTimeDuration,
    int DeathMatchKill,
    int DeathMatchTimeMs,
    int TimeAttackTimeMs,
    int ConquerTimeLimitMs,
    int StarModeTimeLimitMs,
    int Unknown44,
    int Unknown48,
    byte Flags52)
{
    /// <summary>Reads the rule record from an open reader.</summary>
    /// <param name="r">The reader.</param>
    public static RoomRuleRecord Read(ref PacketReader r) => new(
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadInt32(),
        r.ReadByte());
}

/// <summary>
/// C2S 107 lobby quick-create variant. Builder <c>sub_9D7440</c> at
/// <c>0x9D7440</c> (caller <c>sub_99F580</c>): the same 56-byte rule record
/// first, then three prefixed wstrings and a trailing <c>i32</c>. Evidence:
/// verified wire order and widths.
///
/// </summary>
/// <param name="Rules">The 56-byte rule record.</param>
/// <param name="Unknown1">First prefixed wstring.</param>
/// <param name="Unknown2">Second prefixed wstring.</param>
/// <param name="Unknown3">Third prefixed wstring.</param>
/// <param name="Unknown4">Trailing <c>i32</c>.</param>
public sealed record QuickCreateRoomRequest(
    RoomRuleRecord Rules,
    string Unknown1,
    string Unknown2,
    string Unknown3,
    int Unknown4)
{
    /// <summary>Parses a complete C2S 107 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static QuickCreateRoomRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var rules = RoomRuleRecord.Read(ref r);
        var s1 = r.ReadWideStringPrefixed();
        var s2 = r.ReadWideStringPrefixed();
        var s3 = r.ReadWideStringPrefixed();
        var u4 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 107");
        return new QuickCreateRoomRequest(rules, s1, s2, s3, u4);
    }
}

/// <summary>
/// C2S 517 room join request. Builder <c>sub_708310</c> at <c>0x708310</c>: one
/// little-endian <c>i32</c> room id (the client arms a 20,000 ms timeout).
/// Evidence: verified wire shape.
///
/// </summary>
/// <param name="RoomId">The room id presented by the client.</param>
public sealed record JoinRoomRequest(int RoomId)
{
    /// <summary>Parses a complete C2S 517 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static JoinRoomRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var roomId = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 517");
        return new JoinRoomRequest(roomId);
    }
}

/// <summary>
/// C2S 505 waiting-seat status from <c>0x9785E0</c>: ready, shop open,
/// inventory open, each a four-byte boolean. Manual and automatic ready
/// both reach this builder through <c>0x9787F0</c>.
///
/// </summary>
/// <param name="Ready">Whether the player has selected ready.</param>
/// <param name="InShop">Whether the shop dialog is open.</param>
/// <param name="InInventory">Whether the inventory dialog is open.</param>
public sealed record RoomStatusRequest(bool Ready, bool InShop, bool InInventory)
{
    /// <summary>Parses a complete C2S 505 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RoomStatusRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var u0 = r.ReadInt32();
        var u1 = r.ReadInt32();
        var u2 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 505");
        if (u0 is not (0 or 1) || u1 is not (0 or 1) || u2 is not (0 or 1)
            || u0 + u1 + u2 > 1)
            throw new PacketFormatException("C2S 505 requires mutually exclusive ready, shop, and inventory flags.", 0);
        return new RoomStatusRequest(u0 != 0, u1 != 0, u2 != 0);
    }
}

/// <summary>
/// C2S 502 room chat / room-name set. Builder <c>sub_4E9070</c> at
/// <c>0x4E9070</c> (callers <c>sub_572280</c> / <c>sub_99F1C0</c>):
/// <c>i32</c>, prefixed wstring, prefixed wstring. Evidence: verified wire
/// shape.
///
/// </summary>
/// <param name="Unknown0">Leading <c>i32</c> (channel / type).</param>
/// <param name="Unknown1">First prefixed wstring.</param>
/// <param name="Unknown2">Second prefixed wstring.</param>
public sealed record RoomChatRequest(int Unknown0, string Unknown1, string Unknown2)
{
    /// <summary>Parses a complete C2S 502 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RoomChatRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var u0 = r.ReadInt32();
        var s1 = r.ReadWideStringPrefixed();
        var s2 = r.ReadWideStringPrefixed();
        WireUtil.RequireConsumed(r, "C2S 502");
        return new RoomChatRequest(u0, s1, s2);
    }
}

/// <summary>
/// C2S 520 room chat (typed variants). Builder <c>sub_9D7350</c> at
/// <c>0x9D7350</c>: <c>i32</c>, prefixed wstring, <c>i32</c>, <c>i32</c>.
/// Evidence: verified wire shape; live values 101/102/103.
///
/// </summary>
/// <param name="Unknown0">Leading <c>i32</c> (chat type).</param>
/// <param name="Unknown1">Prefixed wstring.</param>
/// <param name="Unknown2">Trailing <c>i32</c>.</param>
/// <param name="Unknown3">Trailing <c>i32</c>.</param>
public sealed record RoomChatTypedRequest(int Unknown0, string Unknown1, int Unknown2, int Unknown3)
{
    /// <summary>Parses a complete C2S 520 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RoomChatTypedRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var u0 = r.ReadInt32();
        var s1 = r.ReadWideStringPrefixed();
        var u2 = r.ReadInt32();
        var u3 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 520");
        return new RoomChatTypedRequest(u0, s1, u2, u3);
    }
}

/// <summary>
/// C2S 511 in-room state command (ready / start candidate). Builder
/// <c>sub_913640</c> at <c>0x913640</c>: four <c>i32</c>, two <c>u8</c>, one
/// <c>i32</c>. Evidence: verified wire shape; the ready/start semantic is
/// unverified.
///
/// </summary>
/// <param name="Unknown0">First <c>i32</c>.</param>
/// <param name="Unknown1">Second <c>i32</c>.</param>
/// <param name="Unknown2">Third <c>i32</c>.</param>
/// <param name="Unknown3">Fourth <c>i32</c>.</param>
/// <param name="Unknown4">First <c>u8</c>.</param>
/// <param name="Unknown5">Second <c>u8</c>.</param>
/// <param name="Unknown6">Trailing <c>i32</c>.</param>
public sealed record RoomStateCommandRequest(
    int Unknown0, int Unknown1, int Unknown2, int Unknown3, byte Unknown4, byte Unknown5, int Unknown6)
{
    /// <summary>Parses a complete C2S 511 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RoomStateCommandRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var u0 = r.ReadInt32();
        var u1 = r.ReadInt32();
        var u2 = r.ReadInt32();
        var u3 = r.ReadInt32();
        var b0 = r.ReadByte();
        var b1 = r.ReadByte();
        var u6 = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 511");
        return new RoomStateCommandRequest(u0, u1, u2, u3, b0, b1, u6);
    }
}
