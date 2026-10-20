using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Legacy C2S 106 <c>gcpReqGotoLobby</c> and S2C 109 <c>gspGotoLobby</c>.
/// MainMenu <c>btnMultiplay</c> sends the empty request; the reply is one bool32.
/// This is not <c>gcpReqMatch</c> / <c>gspNotifyMatch</c>.
/// </summary>
internal static class GotoLobbyCodec
{
    /// <summary>
    /// Requires a zero-length body. Send sites: id-400 <c>0x5F18D0</c>, cn-279
    /// <c>0x420E40</c>, cn-324 <c>0x451B80</c>, kr-812 <c>0x806D00</c>, cn-717 <c>0x88AA70</c>.
    /// </summary>
    /// <param name="payload">The decrypted C2S body.</param>
    /// <exception cref="PacketFormatException">Thrown when any byte is present.</exception>
    public static void ReadRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0)
            throw new PacketFormatException($"gcpReqGotoLobby must be empty, got {payload.Length} byte(s).", 0);
    }

    /// <summary>
    /// Writes the notice flag as bool32 0 (silent enter). Readers: id-400 <c>0x63B780</c>,
    /// cn-279 <c>0x44FC80</c>, cn-324 <c>0x798810</c>, kr-812 <c>0x80C160</c>,
    /// cn-717 <c>0x8C3780</c>. Handlers (cn-717 <c>0x8A6BE0</c>, id-400 <c>0x7A84E0</c>,
    /// kr-812 <c>0x81FF00</c>, cn-279 <c>0x6F3B20</c>, cn-324 <c>0x707600</c>) show the
    /// client "party moved to lobby" string only when the flag is nonzero; LobbyStage
    /// (argument 3) still runs when the connection-ready byte is set.
    /// </summary>
    /// <returns>A 4-byte little-endian body with value 0.</returns>
    public static byte[] WriteSuccess() => BitConverter.GetBytes(0);
}
