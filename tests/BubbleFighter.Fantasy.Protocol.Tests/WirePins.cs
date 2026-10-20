using System.Buffers.Binary;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Byte-length and hex pins for the kr-1256 greeting, stream cipher, frame, and
/// version/patch handshake. Lengths and named fields come from
/// <c>profiles/opcode-policy.md</c>; unknown trailing payload bytes are left as zeros.
/// </summary>
internal static class WirePins
{
    /// <summary>Bootstrap greeting size: u16 status plus 128 UTF-16LE slots (<c>sub_99EAD0</c>).</summary>
    public const int GreetingSize = 258;

    /// <summary>A client major version that is not the expected one, for mismatch tests.</summary>
    public static readonly byte[] WrongVersion = [0xD0, 0x07, 0x00, 0x00];

    /// <summary>Frame header size finalized by <c>sub_10BB3E0</c>.</summary>
    public const int FrameHeaderSize = 8;

    /// <summary><c>gcpReqGamePatch</c> payload size (builder <c>sub_99DD00</c>).</summary>
    public const int GamePatchPayloadSize = 96;

    /// <summary>Encoded <c>gcpReqGamePatch</c> frame: 8-byte header plus 96-byte payload.</summary>
    public const int GamePatchFrameSize = FrameHeaderSize + GamePatchPayloadSize;

    /// <summary><c>gspRepGamePatch(0, "")</c> payload size (handler <c>sub_9A1250</c>).</summary>
    public const int RepGamePatchPayloadSize = 8;

    /// <summary>Encoded <c>gspRepGamePatch(0, "")</c> frame: 8-byte header plus 8-byte payload.</summary>
    public const int RepGamePatchFrameSize = FrameHeaderSize + RepGamePatchPayloadSize;

    /// <summary>
    /// Plaintext header for a 96-byte opcode-1 frame: length 96, opcode 1, payload_xor
    /// <c>0xEC</c>, header_xor <c>0x8D</c>.
    /// </summary>
    public const string GamePatchPlainHeaderHex = "600000000100EC8D";

    /// <summary>
    /// Documented C2S ciphertext for <see cref="GamePatchPlainHeaderHex"/> (seed
    /// <c>sub_10B9720</c> (0, 1)).
    /// </summary>
    public const string GamePatchC2SHeaderCipherHex = "BF623297C28B3279";

    /// <summary>
    /// C2S ciphertext of the 104-byte <c>gcpReqGamePatch</c> frame whose payload is
    /// <c>ClientVersion_Major</c> 1256 followed by 92 zero bytes.
    /// </summary>
    public const string GamePatchC2SFrameCipherHex =
        "BF623297C28B327945A429C268AC86A0B5B25668382648655F30CDA5E855FE5542D1F7CD7E92A5C13D9BE78841C7677C" +
        "7F833E57723418AB87CF2E9D4E9B79BA6CDA8DCDF19DC16B42830CF2805BE263F0B45F6B59B15FD98D62765E11A6628A" +
        "59A35F6332E5C17D";

    /// <summary>
    /// Plaintext <c>gspRepGamePatch(0, "")</c> frame: header
    /// <c>u32 8, u16 2, payload_xor 0, header_xor 0x0A</c> plus eight zero payload bytes.
    /// </summary>
    public const string RepGamePatchPlainFrameHex = "080000000200000A0000000000000000";

    /// <summary>
    /// S2C ciphertext of <see cref="RepGamePatchPlainFrameHex"/> (seed <c>sub_10B9720</c> (5, 3)).
    /// </summary>
    public const string RepGamePatchS2CFrameCipherHex = "206B87D0308E4EE479FA6CE88D8AF15D";

    /// <summary>Empty <c>gspNotifyFirstMoveChannel</c> header (opcode 163, label <c>0x15B7140</c>).</summary>
    public const string NotifyFirstMoveChannelHeaderHex = "00000000A30000A3";

    /// <summary>
    /// Stream-cipher table A hex from <c>sub_7B4DE0</c>/<c>sub_7B4E50</c>. The dump is 49
    /// bytes (98 hex chars); the protocol note labels it 48.
    /// </summary>
    public const string TableAHex =
        "5F3E3272C11846875D2E294E6879866CB58D56F138C148425F0CCD80E8E2FEF0425FF7597E5FA58D3D76E711416267597F";

    /// <summary>Stream-cipher table B hex: 24 bytes from the same client functions.</summary>
    public const string TableBHex = "230024002500730065007200740033003400250077006500";

    /// <summary>C2S ciphertext of 16 zero bytes (seed (0, 1)).</summary>
    public const string ClientToServerZeros16Hex = "5F623297C18B46EC5DA029C268AC86A0";

    /// <summary>S2C ciphertext of 16 zero bytes (seed (5, 3)).</summary>
    public const string ServerToClientZeros16Hex = "186B87D02E8E4EDA79FA6CE88D8AF15D";

    /// <summary>Default greeting: status 4 and an empty command line (258 zero-padded bytes).</summary>
    public const string EmptyGreetingPrefixHex = "0400";

    /// <summary>Greeting prefix for status 4 and command line <c>run</c>.</summary>
    public const string RunGreetingPrefixHex = "0400720075006E00";

    /// <summary>
    /// Builds the 96-byte <c>gcpReqGamePatch</c> payload: first <c>u32</c> is
    /// <c>ClientVersion_Major</c>, remaining 92 bytes left zero (unknown).
    /// </summary>
    public static byte[] GamePatchPayload(int clientVersionMajor = 1256)
    {
        var payload = new byte[GamePatchPayloadSize];
        BinaryPrimitives.WriteInt32LittleEndian(payload, clientVersionMajor);
        return payload;
    }
}
