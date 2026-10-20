using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Selects the remaining request layout and the modern response family.
/// Legacy response fields and widths are defined in packets/login.yaml.
/// </summary>
/// <param name="RequestTrailer">
/// Whether <c>gcpReqLogin</c> ends with <c>u32 unknown2, u32 unknown3</c> (kr-812
/// <c>sub_682370</c>; id-400 <c>sub_5E8B10</c> stops after the adapter list).
/// </param>
/// <param name="LoginResponse">
/// Whether <c>gspRepLogin</c> is <c>LoginResponse</c> followed by <c>LoginNational</c>
/// instead of the legacy reply.
/// </param>
/// <param name="RequestExtraWstr">
/// Whether <c>gcpReqLogin</c> ends with one more <c>wstr</c> after the request trailer
/// (cn-774 only; the mid-CN LoginResponse builds keep the cn-717 request).
/// </param>
/// <param name="RequestToken">
/// Whether <c>gcpReqLogin</c> ends with one <c>u32</c> right after the adapter list (id-1809
/// <c>sub_632220</c> writes <c>this[26]</c>, which the login routine <c>sub_633060</c> fills
/// from a four-byte key it XORs with a byte of a client table; id-400 <c>sub_5E8B10</c> has no
/// such field). The value is read and ignored.
/// </param>
internal sealed record LoginShape(
    bool RequestTrailer,
    bool LoginResponse = false,
    bool RequestExtraWstr = false,
    bool RequestToken = false)
{
    /// <summary>Reads the layout from a build's merged <c>login</c> values in <c>packets/builds.yaml</c>.</summary>
    /// <param name="build">The packet-table build, for messages.</param>
    /// <param name="values">The build's values laid over its bases'.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ConfigException">Thrown when a value is missing or not a boolean.</exception>
    public static LoginShape From(string build, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("login", out var raw) || raw is not IReadOnlyDictionary<string, object?> login)
            throw new ConfigException($"Build '{build}' must define login values in packets/builds.yaml.");
        return new(Flag("request_trailer"), Flag("login_response"), Flag("request_extra_wstr"), Flag("request_token"));

        bool Flag(string key) => login.TryGetValue(key, out var value) && value is string text && bool.TryParse(text, out var flag)
            ? flag : throw new ConfigException($"Build '{build}' must define boolean login.{key} in packets/builds.yaml.");
    }
}

/// <summary>The legacy login pair: C2S 3 <c>gcpReqLogin</c> and S2C 3 <c>gspRepLogin</c>.</summary>
internal static class LoginMessageCodec
{
    /// <summary>
    /// The longest string this codec reads. The account field is a 1024-character buffer
    /// (kr-812 <c>sub_8FE2E0</c> sizes it with <c>sub_441A40(0x400)</c> and never trims it).
    /// </summary>
    private const int MaxChars = 1024;

    /// <summary>The largest adapter-address list accepted; the client sends at most 10 (kr-812 <c>sub_B28910</c>).</summary>
    private const int MaxAddresses = 10;

    /// <summary>
    /// Reads <c>gcpReqLogin</c>: <c>wstr account, wstr unknown1, wstr unknown2, u32 count,
    /// count x u32 adapter IPv4</c>, then <c>u32 unknown3, u32 unknown4</c> when the shape has
    /// the request trailer, or one <c>u32</c> token when it has the request token. Each wstr is a <c>u32</c> character count plus UTF-16LE; the
    /// account is padded with NULs to the buffer length.
    /// </summary>
    /// <param name="payload">The C2S 3 body.</param>
    /// <param name="shape">The build's login layout.</param>
    /// <returns>The login request.</returns>
    /// <exception cref="PacketFormatException">Thrown when the body does not have this shape.</exception>
    public static LoginRequested ReadRequest(ReadOnlySpan<byte> payload, LoginShape shape)
    {
        var reader = new PacketReader(payload);
        var account = ReadString(ref reader);
        ReadString(ref reader);
        ReadString(ref reader);
        var count = reader.ReadUInt32();
        if (count > MaxAddresses)
            throw new PacketFormatException($"gcpReqLogin lists {count} adapter addresses; the client sends at most {MaxAddresses}", reader.Offset);
        reader.ReadBytes(checked((int)count * 4));
        if (shape.RequestTrailer)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
        }
        if (shape.RequestToken)
            reader.ReadUInt32();
        if (shape.RequestExtraWstr)
            ReadString(ref reader);
        if (reader.Remaining != 0)
            throw new PacketFormatException($"gcpReqLogin has {reader.Remaining} trailing bytes", reader.Offset);
        var end = account.IndexOf('\0');
        return new LoginRequested((end < 0 ? account : account[..end]).Trim());
    }

    /// <summary>The first character item; a character's type index is its item id minus this base.</summary>
    private const int CharacterItemBase = 300000;

    /// <summary>
    /// Writes an accepted <c>gspRepLogin</c>: <c>u32 result 0, wstr account, u32 userNumber,
    /// wstr nickname, u32 characterType, u32 unknown2, u32 unknown3, u32 unknown4, f64 server
    /// time (OLE date), wstr unknown5</c>, optionally <c>u32 unknown6</c>, then <c>u8 unknown7, wstr unknown8</c>
    /// when the shape has the reply trailer. The kr-812 handler <c>sub_818680</c> stores the
    /// user number through <c>sub_812470</c> into the protected int at <c>dword_E82888+64</c>,
    /// the identity <c>sub_6CC6F0</c> matches against the room slots to find the local player,
    /// and the character type at <c>dword_E82888+96</c>, which <c>sub_9AFBE0</c> hands to the
    /// model builder <c>sub_9B3D70</c>. The type is the FighterConfig key (<c>chrTypeIdx</c>):
    /// cn-324 <c>Config/FighterConfig.xml</c> keys 1.. match the 300001.. character items one
    /// to one; the client's reader decides the order.
    /// </summary>
    /// <param name="accepted">The accepted login.</param>
    /// <param name="shape">The build's login layout.</param>
    /// <param name="tables">The packet structures loaded for this row.</param>
    /// <param name="build">The nearest build named in the packet tables.</param>
    /// <returns>The S2C 3 body.</returns>
    public static byte[] WriteAccepted(LoginAccepted accepted, LoginShape shape, PacketTables tables, string build) => shape.LoginResponse
        ? WriteModernAccepted(accepted)
        : Write(tables, build, 0, accepted.Account, accepted.UserNumber, accepted.Nickname, accepted.CharacterItemId - CharacterItemBase,
            accepted.ServerTime.ToOADate(), "LOCAL");

    /// <summary>
    /// Writes an accepted <c>LoginResponse</c> + <c>LoginNational</c> reply: <c>i32 result 0,
    /// wstr account, i32 userNumber, wstr nickname, i32 characterType, i32 unknown2,
    /// i32 unknown3, i32 unknown4, i64 unknown5, i32 unknown6</c>, then <c>i32 flag 1</c>,
    /// which leaves out the national block. The two identity fields sit where the legacy reply
    /// has them.
    /// </summary>
    internal static byte[] WriteModernAccepted(LoginAccepted accepted)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed(accepted.Account);
        writer.WriteInt32(accepted.UserNumber);
        writer.WriteWideStringPrefixed(accepted.Nickname);
        writer.WriteInt32(accepted.CharacterItemId - CharacterItemBase);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt64(0);
        writer.WriteInt32(0);
        writer.WriteInt32(1);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes a refused <c>gspRepLogin</c>: the result alone is read before the handler branches,
    /// and result 1 shows "authentication failed, check the id and password" (kr-812
    /// <c>dword_D69F3C</c>). The remaining fields are written empty so the reader finds them.
    /// </summary>
    /// <param name="rejected">The refusal.</param>
    /// <param name="shape">The build's login layout.</param>
    /// <param name="tables">The packet structures loaded for this row.</param>
    /// <param name="build">The nearest build named in the packet tables.</param>
    /// <returns>The S2C 3 body.</returns>
    public static byte[] WriteRejected(LoginRejected rejected, LoginShape shape, PacketTables tables, string build)
    {
        if (shape.LoginResponse)
            return WriteModernRejected(rejected);
        return Write(tables, build, RejectionResult(rejected), "", 0, "", 0, 0, "");
    }

    /// <summary>
    /// Writes an accepted <c>gspRepLogin</c> in the kr-1256 family's table form: the fields come from the
    /// build's <c>LoginReply</c> row (kr-1256 resolves <c>LoginReply@kr-1256</c>), and the values are the ones
    /// the modern login codec wrote before this slice: a server-issued session identity string, the Dao
    /// character type, the initial channel that arms the client's room-list polling (<c>kr-1256 sub_8530C0</c>
    /// never arms it for zero) and a zero server time.
    /// </summary>
    /// <param name="accepted">The accepted login.</param>
    /// <param name="sessionId">The server-issued session identity written in the account field.</param>
    /// <param name="tables">The packet structures loaded for this row.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <param name="initialChannelId">The nonzero channel stored at <c>ServerConnection+2508</c>.</param>
    /// <returns>The S2C 3 body.</returns>
    internal static byte[] WriteTableModernAccepted(LoginAccepted accepted, string sessionId, PacketTables tables,
        string build, int initialChannelId)
    {
        if (initialChannelId == 0)
            throw new ArgumentOutOfRangeException(nameof(initialChannelId), "The initial channel must arm native room-list polling.");
        return PacketCodec.Write(tables, build, "LoginReply", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = 0,
            ["account"] = sessionId,
            ["user_number"] = accepted.UserNumber,
            ["nickname"] = accepted.Nickname,
            // The kr-1256 login reply carries the Dao character type, not the equipped character's item id.
            ["character_type"] = DaoCharacterType,
            ["channel_id"] = initialChannelId,
            ["server_time"] = 0.0,
        });
    }

    /// <summary>The Dao character type the modern login reply carries (<c>kr-1256 sub_4BD3E0</c> maps 0 to 1).</summary>
    internal const int DaoCharacterType = 1;

    /// <summary>
    /// Writes a refused <c>gspRepLogin</c> in the kr-1256 family's table form: the build's <c>LoginRefusal</c>
    /// row with the result word only; its defaults and absent optional tag complete the body.
    /// </summary>
    /// <param name="rejected">The refusal.</param>
    /// <param name="tables">The packet structures loaded for this row.</param>
    /// <param name="build">The build whose layout is written.</param>
    /// <returns>The S2C 3 body.</returns>
    internal static byte[] WriteTableModernRejected(LoginRejected rejected, PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "LoginRefusal", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["result"] = RejectionResult(rejected),
        });

    /// <summary>Writes the modern refusal without requiring a legacy packet-table ancestor.</summary>
    internal static byte[] WriteModernRejected(LoginRejected rejected)
    {
        // cn-774 sub_105C740 reads one more i32 after a non-zero result, then LoginNational.
        var buffer = new ArrayBufferWriter<byte>(12);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(RejectionResult(rejected));
        writer.WriteInt32(0);
        writer.WriteInt32(1);
        return buffer.WrittenSpan.ToArray();
    }

    private static int RejectionResult(LoginRejected rejected) => rejected.Reason switch
    {
        LoginRejection.AccountNotFound => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(rejected), rejected.Reason, "No legacy login result for this reason."),
    };

    private static byte[] Write(PacketTables tables, string build, int result, string account, int userNumber,
        string nickname, int characterType, double serverTime, string unknownString) =>
        PacketCodec.Write(tables, build, "LoginReply", new Dictionary<string, object?>
        {
            ["result"] = result,
            ["account"] = account,
            ["user_number"] = userNumber,
            ["nickname"] = nickname,
            ["character_type"] = characterType,
            ["server_time"] = serverTime,
            ["unknown_96"] = unknownString,
            ["unknown_48"] = unknownString,
        });

    private static string ReadString(ref PacketReader reader)
    {
        var chars = reader.ReadUInt32();
        if (chars > MaxChars)
            throw new PacketFormatException($"gcpReqLogin string of {chars} characters exceeds {MaxChars}", reader.Offset);
        return System.Text.Encoding.Unicode.GetString(reader.ReadBytes(checked((int)chars * 2)));
    }
}
