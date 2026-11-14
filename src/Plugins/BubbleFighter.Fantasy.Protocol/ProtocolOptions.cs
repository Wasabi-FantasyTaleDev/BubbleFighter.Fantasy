using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>Configuration for the <see cref="ProtocolPlugin"/> row.</summary>
public sealed class ProtocolOptions
{
    /// <summary>Gets or sets the path of the opcode table YAML file.</summary>
    [Required]
    public string OpcodeTablePath { get; set; } = "profiles/kr-1256/opcodes.yaml";

    /// <summary>
    /// Gets or sets the opcode table name of the client's mandatory first packet, the patch-request
    /// slot. kr-1256 names that slot <c>gcpReqGamePatch</c>; every legacy build names the same slot
    /// <c>gcpFirstConnect</c>. The handshake handler and the first-frame gate resolve it by this name.
    /// </summary>
    [Required]
    public string PatchRequestName { get; set; } = "gcpReqGamePatch";

    /// <summary>Gets or sets whether an existing codec may use a logged unique catalog candidate when its exact name is absent from the runtime table.</summary>
    public bool UseCatalogCandidates { get; set; } = true;

    /// <summary>Gets or sets the research catalog path. Empty looks beside the runtime table and tolerates an absent catalog.</summary>
    public string OpcodeCatalogPath { get; set; } = "";

    /// <summary>Gets or sets the maximum rows loaded from this profile's fallback catalog.</summary>
    [Range(1, 65536)]
    public int MaxCatalogRows { get; set; } = 4096;

    /// <summary>Gets or sets the maximum catalog bytes accepted before parsing.</summary>
    [Range(1024, 16777216)]
    public int MaxCatalogFileBytes { get; set; } = 4194304;

    /// <summary>Gets or sets the status word sent in the bootstrap greeting.</summary>
    [Range(0, 65535)]
    public int BootstrapStatus { get; set; } = 4;

    /// <summary>Gets or sets the command-line string sent in the bootstrap greeting.</summary>
    public string BootstrapCommandLine { get; set; } = "";

    /// <summary>
    /// Gets or sets the largest frame payload accepted from a client, in bytes; a header that declares more closes
    /// the connection before any of the payload is buffered. The default is the most a frame with a 16-bit length
    /// can carry, which is every build but the ones with the 8-byte frame; the largest frame a supported client is
    /// known to send is its login, about 2 KB. The row refuses a value that does not fit
    /// <see cref="InboundBufferBytes"/> together with the dialect's frame header.
    /// </summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxPayloadBytes { get; set; } = ushort.MaxValue;

    /// <summary>
    /// Gets or sets the maximum decrypted-but-unparsed inbound bytes buffered per connection; the socket pump
    /// pauses when the frame parser falls this far behind. It is also the most memory a client can make the server
    /// hold before it has sent one complete frame. The default holds one largest frame and as much again that
    /// arrived behind it.
    /// </summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int InboundBufferBytes { get; set; } = 2 * (ushort.MaxValue + 1);

    /// <summary>
    /// Gets or sets how long a new connection may take to send its first complete frame, in seconds, before it is
    /// closed. Every supported client sends its first frame as soon as it has the greeting, so only a connection
    /// that is not a game client waits this long; without the limit such a connection holds its socket, its
    /// session and its buffer for as long as it likes. The deadline is one timer per connection that has not
    /// spoken yet and nothing afterwards.
    /// </summary>
    [Range(1, 3600)]
    public int FirstFrameSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum frames queued for one session and not yet written. Sends return
    /// once queued; a client that stops reading until its queue is full is disconnected with
    /// reason <c>send queue full</c> instead of holding up the room that sends to it.
    /// </summary>
    [Range(1, 65536)]
    public int SendQueueCapacity { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the retained outbound payload bytes allowed per session, including the frame currently being
    /// written. Exhaustion closes the connection, so this is the most memory a client that stops reading can make
    /// the server hold. It must exceed the largest frame the build sends, or that frame can never be sent: the
    /// largest any build sends is the kr-1256 shop catalog at about 112 KB, and a frame with a 16-bit length cannot
    /// exceed 64 KB. The default is 64 such full frames. With debug logging each session reports the most it queued
    /// when it ends, which is the number to size this from.
    /// </summary>
    [Range(1, 1073741824)]
    public int SendQueueBytes { get; set; } = 64 * (ushort.MaxValue + 1);

    /// <summary>
    /// Gets or sets the outbound bytes all sessions of this protocol row may hold queued together. Each session stops
    /// at <see cref="SendQueueBytes"/>, but many clients that stop reading at once could each hold that much; when a
    /// frame would cross this ceiling the session holding the most queued bytes is closed and no sender waits. It must
    /// be at least <see cref="SendQueueBytes"/>. Sessions log their peak queue at Debug when they end, which is the
    /// number to size it from.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long TotalSendQueueBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Gets or sets the maximum simultaneous typed state associations per connection.</summary>
    [Range(1, 4096)]
    public int MaxSessionFeatures { get; set; } = 64;

    /// <summary>
    /// Gets or sets the maximum number of live connections whose patch-handshake
    /// verification this row retains. The table refuses a new mark beyond the cap
    /// instead of growing without bound; size it at or above the game listener's
    /// connection limit.
    /// </summary>
    [Range(1, 65536)]
    public int MaxTrackedConnections { get; set; } = 1024;

    /// <summary>Gets or sets whether a mismatched client version closes the connection.</summary>
    public bool EnforceClientVersion { get; set; } = true;

    /// <summary>Gets or sets whether the game listener requires the patch handshake as its first frame; the relay listener has its own authentication.</summary>
    public bool RequirePatchHandshakeFirst { get; set; } = true;

    /// <summary>
    /// Gets or sets the C2S opcode names a client may send before the patch handshake without
    /// being closed, such as a build's crash report sent first on the next launch. They are
    /// dispatched as usual and the handshake is still required after them unless their handler
    /// uses the packet-scoped IInitialHandshakeContinuation after validating an application handshake.
    /// Such a continuation grants no account authentication or client-version verification.
    /// </summary>
    public IReadOnlyList<string> PreHandshakeOpcodes { get; set; } = [];

    /// <summary>Gets or sets one exact ordered alternative admission sequence of one to eight distinct C2S opcode names, or an empty list to disable it. Every packet must have an empty payload; completing the sequence admits the connection without verifying its client version.</summary>
    public IReadOnlyList<string> AlternateHandshakeSequence { get; set; } = [];

    /// <summary>
    /// Gets or sets the empty C2S markers, by name, that this client build sends first on every auxiliary
    /// connection before its center status query or retained move (cn-324 <c>sub_7001F0</c> sends
    /// <c>gcpStartGPK</c>); empty for builds that send the request directly. Build data, read only when
    /// <see cref="AnswerCenterStatus"/> or <see cref="AcceptRetainedMoves"/> derives a sequence.
    /// </summary>
    public IReadOnlyList<string> AuxiliaryPrefix { get; set; } = [];

    /// <summary>
    /// Gets or sets whether auxiliary center status queries are admitted. When
    /// <see cref="AlternateHandshakeSequence"/> is empty, the sequence becomes <see cref="AuxiliaryPrefix"/>
    /// followed by <c>gcpReqCenterUserCnt</c>, resolved per build.
    /// </summary>
    public bool AnswerCenterStatus { get; set; }

    /// <summary>
    /// Gets or sets whether a migrated player's retained move (<c>gcpReqMoveInto</c>) may open a connection
    /// before the patch handshake. It is added to <see cref="PreHandshakeOpcodes"/>; a build with an
    /// <see cref="AuxiliaryPrefix"/> also gets the derived center status sequence, because the client sends
    /// the move on that same auxiliary path.
    /// </summary>
    public bool AcceptRetainedMoves { get; set; }

    /// <summary>Gets or sets the removed unauthenticated greeting flag; true is rejected with migration guidance.</summary>
    public bool SendInGameGreeting { get; set; }

    /// <summary>
    /// Gets or sets the gates announced in <c>gspCenterList</c> right after the patch reply.
    /// Builds whose connect loop waits for that list (kr-812, <c>sub_8F3970</c>) need at least
    /// one; empty sends nothing, which is what builds without the packet want.
    /// </summary>
    public IReadOnlyList<CenterGateOptions> CenterGates { get; set; } = [];
}

/// <summary>
/// One <c>Lambda::GateInfo</c> entry of <c>gspCenterList</c> (reader <c>sub_845430</c>:
/// four <c>u32</c> then a <c>u16</c>). <c>sub_8E6A40</c> groups the list by
/// <see cref="Key"/> and then <see cref="SubKey"/>; an entry whose address and port equal
/// the connection the client already holds becomes its current gate.
/// </summary>
public sealed class CenterGateOptions
{
    /// <summary>Gets or sets the outer grouping key (GateInfo +4).</summary>
    public int Key { get; set; }

    /// <summary>Gets or sets the inner grouping key (GateInfo +8).</summary>
    public int SubKey { get; set; }

    /// <summary>Gets or sets GateInfo +12, meaning unknown; 100 by default.</summary>
    public int Unknown1 { get; set; } = 100;

    /// <summary>Gets or sets the IPv4 address the client connects to for this gate.</summary>
    [Required]
    public string Address { get; set; } = "";

    /// <summary>Gets or sets the TCP port of the gate.</summary>
    [Range(1, 65535)]
    public int Port { get; set; }
}
