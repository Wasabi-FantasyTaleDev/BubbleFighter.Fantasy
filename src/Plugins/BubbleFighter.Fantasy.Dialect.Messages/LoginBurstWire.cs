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
        if (tables.Resolve(build, "gspCenterList").Count == 0)
            throw new ConfigException($"Build '{build}' has no gspCenterList layout; only the modern count-first form is audited.");
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
    /// Writes the empty <c>gspRepDynamicConfig</c> body (S2C 1500): a u32 byte count of zero, so no compressed
    /// configuration blobs follow (handler kr-1256 sub_65F370 reads the count and then that many bytes).
    /// </summary>
    /// <returns>The four-byte body.</returns>
    internal static byte[] WriteDynamicConfigEmpty()
    {
        var w = PacketWriter.Create();
        w.WriteUInt32(0);
        return ((System.Buffers.ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }

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
        if (tables.Resolve(build, "SessionSignatureData").Count == 0)
            throw new ConfigException($"Build '{build}' has no gspSessionSignatureData layout; the main-menu probe has no audited reply.");
        return PacketCodec.Write(tables, build, "SessionSignatureData", new Dictionary<string, object?>(StringComparer.Ordinal));
    }
}
