namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// Wire opcode constants used by the protocol plugin. Every constant mirrors a row in
/// the profile opcode table (the current profile ships <c>profiles/kr-1256/opcodes.yaml</c>);
/// the test suite asserts the two cannot drift. Client-to-server and server-to-client
/// are separate numeric spaces.
/// </summary>
public static class WireOpcodes
{
    /// <summary>Client-to-server opcodes.</summary>
    public static class C2S
    {
        /// <summary>
        /// <c>gcpReqGamePatch</c> (verified, builder <c>sub_99DD00</c>): the patch/version
        /// check request and the mandatory first packet on the game protocol. The 96-byte
        /// payload's first <c>u32</c> is <c>ClientVersion_Major</c>.
        /// </summary>
        public const ushort GamePatch = 1;

        /// <summary>
        /// <c>Unknown521</c> (inferred, source <c>sub_937560</c>): sent twice by the
        /// <c>MainMenuStage</c> constructor <c>sub_88D670</c>; purpose unknown.
        /// </summary>
        public const ushort Unknown521 = 521;
    }

    /// <summary>Server-to-client opcodes.</summary>
    public static class S2C
    {
        /// <summary>
        /// <c>gspDisconnect</c> (verified, label <c>0x15B6D20</c>, dispatcher
        /// <c>sub_9D2FF0</c>): tells the client the connection is closing.
        /// </summary>
        public const ushort Disconnect = 1;

        /// <summary>
        /// <c>gspRepGamePatch</c> (verified, label <c>0x15B699C</c>, handler
        /// <c>sub_9A1250</c>): <c>i32 requiresPatch</c> plus a <c>u32</c>-prefixed UTF-16LE
        /// command line; the server replies <c>(0, "")</c>.
        /// </summary>
        public const ushort RepGamePatch = 2;

        /// <summary>
        /// <c>gspNotifyFirstMoveChannel</c> (verified, label <c>0x15B7140</c>, dispatcher
        /// <c>sub_9D2FF0</c> case): empty payload; notifies the client of the initial channel.
        /// </summary>
        public const ushort NotifyFirstMoveChannel = 163;
    }
}
