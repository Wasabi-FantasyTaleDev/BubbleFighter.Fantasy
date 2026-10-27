using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Table-driven bodies of the login burst's small counters and of the channel move handshake: the six-word
/// money snapshot, the pc-room snapshot, the empty center list and the move-into reply, plus the strict read
/// of the move request. Every layout comes from <c>packets/login.yaml</c>, so a build supplies values by
/// field name and never a field order.
/// </summary>
internal static class LoginBurstWire
{
    /// <summary>
    /// Writes the six-word <c>gspUserMoneyStat</c> body. The first four words keep their table defaults; the
    /// fifth and sixth carry the stored balances, because the client stores them in the two user-state words
    /// its user-stat handler fills from lucci and cash (kr-1256 sub_9D2FF0 case 106 copies the words to user
    /// info +76 and +80, the destinations of kr-1256 sub_9A5C40).
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <param name="lucci">The stored lucci balance, written into the fifth word.</param>
    /// <param name="cash">The stored cash balance, written into the sixth word.</param>
    /// <returns>The 24-byte body.</returns>
    internal static byte[] WriteUserMoneyStat(PacketTables tables, string build, int lucci, int cash) =>
        PacketCodec.Write(tables, build, "UserMoneyStat", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unknown_16"] = lucci,
            ["unknown_20"] = cash,
        });

    /// <summary>
    /// Writes the zero and empty <c>gspUserPcroomStat</c> body: two zero words, an empty counted UTF-16
    /// string and a zero u16. The client's reader (kr-1256 sub_99CA30) keeps the words and the u16 in its
    /// user state, and no field has a proven meaning, so the table defaults are the whole body.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <returns>The 14-byte body.</returns>
    internal static byte[] WriteUserPcroomStat(PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "UserPcroomStat", new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>
    /// Writes the empty <c>gspCenterList</c> body: the u32 count alone. The modern reader takes the count
    /// first (kr-1256 sub_10C7500), and a zero count is legal, so an unset list writes four bytes. A build
    /// whose table does not describe the count-first list is refused rather than sent a zero-length body.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <returns>The four-byte body: a zero count.</returns>
    /// <exception cref="ConfigException">The build's tables do not describe the structure.</exception>
    internal static byte[] WriteCenterListEmpty(PacketTables tables, string build)
    {
        RequireCenterList(tables, build);
        return PacketCodec.Write(tables, build, "gspCenterList", new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Reads the one-word <c>gcpReqMoveStage</c> body strictly: a body shorter or longer than the single word
    /// throws (kr-1256 sub_56FB50 writes one little-endian word after the opcode). The word's meaning is not
    /// proven, so it is returned as the client sent it.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is read.</param>
    /// <param name="body">The decrypted packet body, without the framing header.</param>
    /// <returns>The word the client sent.</returns>
    /// <exception cref="PacketFormatException">The body is truncated or has trailing bytes.</exception>
    internal static int ReadMoveStage(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var values = PacketCodec.ReadScalars(tables, build, "MoveStageRequest", body);
        return (int)values["unknown_0"];
    }

    /// <summary>
    /// Writes the three-word <c>gspRepMoveInto</c> body. The second word is the channel the client stores at
    /// its connection's channel slot (kr-1256 sub_9A6D30, the same slot S2C gspRepLogin fills); the third word
    /// keeps its default because no reader for it is traced. A zero result is the client's failure path and
    /// shuts the connection down, so the caller supplies a nonzero result for a successful move.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <param name="result">The move outcome; zero makes the client close the connection.</param>
    /// <param name="channelId">The channel the client moves to.</param>
    /// <returns>The twelve-byte body.</returns>
    internal static byte[] WriteMoveInto(PacketTables tables, string build, int result, int channelId) =>
        PacketCodec.Write(tables, build, "gspRepMoveInto", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = result,
            ["channel_id"] = channelId,
        });

    /// <summary>
    /// Writes an empty <c>gspRepDynamicConfig</c> category mask; kr-1256 sub_9D2FF0 case 1500 reads no blobs
    /// when no category bit is set.
    /// </summary>
    /// <returns>The four-byte body.</returns>
    internal static byte[] WriteDynamicConfigEmpty(PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "DynamicConfigReply", new Dictionary<string, object?> { ["mask"] = 0 });

    /// <summary>
    /// Writes the empty <c>gspSessionSignatureData</c> body the main-menu probe is answered with: a zero u16, a
    /// zero u8 and an empty counted byte vector (consumed by kr-1256 sub_9EBD80 -&gt; sub_1155B00).
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <returns>The seven-byte body.</returns>
    /// <exception cref="ConfigException">The build's tables do not describe the structure.</exception>
    internal static byte[] WriteSessionSignatureEmpty(PacketTables tables, string build)
    {
        RequireSessionSignature(tables, build);
        return PacketCodec.Write(tables, build, "SessionSignatureData", new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Writes the round's <c>gspSessionSignatureData</c> body: the room's game mode, a zero byte and an empty
    /// counted byte vector. kr-1256 sub_1155B00 stores the mode word; kr-1256 sub_9616A9 requires it to equal
    /// the room mode before the relay join.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is written.</param>
    /// <param name="gameMode">The round's game mode.</param>
    /// <returns>The seven-byte body.</returns>
    /// <exception cref="ConfigException">The build's tables do not describe the structure.</exception>
    internal static byte[] WriteSessionSignature(PacketTables tables, string build, ushort gameMode)
    {
        RequireSessionSignature(tables, build);
        return PacketCodec.Write(tables, build, "SessionSignatureData", new Dictionary<string, object?>
        {
            ["unknown_4"] = gameMode,
        });
    }

    /// <summary>
    /// Requires the two user-state structures the login burst writes: the money snapshot's two balance words
    /// and the pc-room snapshot's whole default body (two words, a counted string and a u16). Both are single
    /// tables, so every build states them.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <exception cref="ConfigException">A consumed field is missing or has another type.</exception>
    internal static void RequireUserState(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "UserMoneyStat", "Login burst",
            ("unknown_16", "u32"), ("unknown_20", "u32"));
        PacketLayoutContract.RequireSignature(tables, build, "UserPcroomStat", "Login burst",
            "unknown_0:u32,unknown_4:u32,unknown_8:wstr,unknown_32:u16");
    }

    /// <summary>
    /// Requires the count-first center list the empty body presumes: the modern builds state the counted
    /// <c>gates</c> list, and a build whose table leaves the structure empty is refused.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <exception cref="ConfigException">The build does not state the counted gate list.</exception>
    internal static void RequireCenterList(PacketTables tables, string build) =>
        PacketLayoutContract.RequireFields(tables, build, "gspCenterList", "Login burst", ("gates", "list<GateInfo>"));

    /// <summary>Requires the one-word move request and the three-word move reply the handshake reads and writes.</summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <exception cref="ConfigException">A consumed field is missing or has another type.</exception>
    internal static void RequireMoveStage(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "MoveStageRequest", "Login burst", ("unknown_0", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "gspRepMoveInto", "Login burst",
            ("result", "u32"), ("channel_id", "u32"));
    }

    /// <summary>Requires the session-signature body the main-menu probe is answered with.</summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The client build whose table is checked.</param>
    /// <exception cref="ConfigException">A consumed field is missing or has another type.</exception>
    internal static void RequireSessionSignature(PacketTables tables, string build) =>
        PacketLayoutContract.RequireFields(tables, build, "SessionSignatureData", "Login burst",
            ("unknown_4", "u16"), ("unknown_6", "u8"), ("unknown_8", "list<u8>"));
}
