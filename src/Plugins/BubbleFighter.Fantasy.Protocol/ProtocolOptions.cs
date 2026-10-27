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

    /// <summary>Gets or sets the maximum accepted frame payload in bytes.</summary>
    [Range(1, 64 * 1024 * 1024)]
    public int MaxPayloadBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Gets or sets the maximum decrypted-but-unparsed inbound bytes buffered per
    /// connection; the socket pump pauses when the frame parser falls this far behind.</summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int InboundBufferBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum frames queued for one session and not yet written. Sends return
    /// once queued; a client that stops reading until its queue is full is disconnected with
    /// reason <c>send queue full</c> instead of holding up the room that sends to it.
    /// </summary>
    [Range(1, 65536)]
    public int SendQueueCapacity { get; set; } = 4096;

    /// <summary>Gets or sets the retained outbound payload bytes allowed per session, including the frame currently being written. Exhaustion closes the connection.</summary>
    [Range(1, 1073741824)]
    public int SendQueueBytes { get; set; } = 16 * 1024 * 1024;

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
    /// Gets or sets the C2S opcodes a client may send before the patch handshake without
    /// being closed, such as a build's crash report sent first on the next launch. They are
    /// dispatched as usual and the handshake is still required after them.
    /// </summary>
    public IReadOnlyList<ushort> PreHandshakeOpcodes { get; set; } = [];

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
