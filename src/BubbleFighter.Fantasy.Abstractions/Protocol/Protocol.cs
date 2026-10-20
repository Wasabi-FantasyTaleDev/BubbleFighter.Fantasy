using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Identity of one client build served by the server. Profiles are identified as
/// <c>&lt;region&gt;-&lt;ClientVersionMajor&gt;</c>, for example <c>kr-1256</c>.
/// </summary>
/// <param name="Id">The profile id.</param>
/// <param name="Region">The client region code.</param>
/// <param name="ClientVersionMajor">The client major version reported in the first packet.</param>
/// <param name="ClientVersionMinor">The client minor version.</param>
/// <param name="ResourceVersion">The resource patch level of the client.</param>
/// <param name="Description">A human-readable description.</param>
public sealed record ClientProfile(string Id, string Region, int ClientVersionMajor, int ClientVersionMinor, int ResourceVersion, string Description);

/// <summary>Which side of the wire an opcode travels.</summary>
public enum PacketDirection
{
    /// <summary>Client to server.</summary>
    ClientToServer,

    /// <summary>Server to client.</summary>
    ServerToClient,
}

/// <summary>How firmly an opcode assignment is established.</summary>
public enum Evidence
{
    /// <summary>Verified against the client binary.</summary>
    Verified,

    /// <summary>Inferred from surrounding code with reasonable confidence.</summary>
    Inferred,

    /// <summary>Unverified guess; never relied on for parsing.</summary>
    Unverified,

    /// <summary>
    /// Observed in a recorded test session and matched to the client
    /// function that reads it. The opcode row names the session and frame.
    /// </summary>
    Captured,
}

/// <summary>One opcode assignment within a client profile's table.</summary>
/// <param name="Value">The wire opcode.</param>
/// <param name="Direction">The direction the opcode travels.</param>
/// <param name="Name">The symbolic name; <c>UnknownNNN</c> when no client label exists.</param>
/// <param name="Evidence">How firmly the assignment is established.</param>
/// <param name="Source">The evidence pointer, typically a client function address or label.</param>
/// <param name="Notes">Optional extra notes.</param>
/// <param name="NameSource">
/// Where <paramref name="Name"/> comes from, separate from the opcode's own evidence:
/// <c>label@&lt;profile&gt;</c> for a label found in that client build, <c>mirrored</c> for a
/// name derived from the paired opposite-direction label, <c>coined</c> for a name given to a
/// packet whose purpose the client code proves but no build labels (such names end in <c>_</c>),
/// <c>none</c> for <c>UnknownNNN</c>, or <see langword="null"/> when the row has not been
/// classified yet.
/// </param>
/// <param name="Connection">The connection kind whose numeric space the opcode belongs to.</param>
public sealed record OpcodeEntry(
    ushort Value, PacketDirection Direction, string Name, Evidence Evidence, string? Source, string? Notes,
    string? NameSource = null, OpcodeConnection Connection = OpcodeConnection.Game);

/// <summary>
/// The connection kind an opcode row applies to. The game and relay connections are separate
/// numeric spaces: the same value may name different packets on each.
/// </summary>
public enum OpcodeConnection
{
    /// <summary>The game (lobby) connection; rows that omit <c>connection</c> belong here.</summary>
    Game,

    /// <summary>The in-match relay connection.</summary>
    Relay,

    /// <summary>A packet the client dispatches identically on both connections.</summary>
    Both,
}

/// <summary>
/// One connection's view of a client profile's opcode table. C2S and S2C are separate numeric
/// spaces, and so are the game and relay connections; lookups only see the rows of
/// <see cref="Connection"/> plus rows marked <see cref="OpcodeConnection.Both"/>.
/// </summary>
public interface IOpcodeTable
{
    /// <summary>Gets the profile this table belongs to.</summary>
    ClientProfile Profile { get; }

    /// <summary>Gets the connection this view resolves; <see cref="OpcodeConnection.Game"/> or <see cref="OpcodeConnection.Relay"/>.</summary>
    OpcodeConnection Connection { get; }

    /// <summary>Gets the same profile's view for another connection.</summary>
    /// <param name="connection">The connection; <see cref="OpcodeConnection.Game"/> or <see cref="OpcodeConnection.Relay"/>.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for <see cref="OpcodeConnection.Both"/>.</exception>
    IOpcodeTable ForConnection(OpcodeConnection connection);

    /// <summary>Looks up an opcode.</summary>
    /// <param name="d">The direction.</param>
    /// <param name="value">The wire opcode.</param>
    /// <param name="e">Receives the entry, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the opcode is known.</returns>
    bool TryGet(PacketDirection d, ushort value, out OpcodeEntry? e);

    /// <summary>Gets every entry this connection's view resolves.</summary>
    IEnumerable<OpcodeEntry> All { get; }

    /// <summary>Formats an opcode for logs, for example <c>gspRepGamePatch(2)</c> or <c>Unknown(999)</c>.</summary>
    /// <param name="d">The direction.</param>
    /// <param name="value">The wire opcode.</param>
    /// <returns>A short descriptive string.</returns>
    string Describe(PacketDirection d, ushort value);
}

/// <summary>Registry of opcode tables keyed by client profile id.</summary>
public interface IOpcodeTableRegistry
{
    /// <summary>Registers a table under its profile id.</summary>
    /// <param name="table">The table.</param>
    /// <returns>A disposer that removes the registration.</returns>
    IDisposable Register(IOpcodeTable table);

    /// <summary>Gets every registered table.</summary>
    IReadOnlyList<IOpcodeTable> All { get; }

    /// <summary>Resolves the table for a profile; use <see cref="OpcodeNames.ForProtocol"/> to get a connection's view.</summary>
    /// <param name="profileId">The client profile id.</param>
    /// <returns>The registered table, as the game connection's view.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no table is registered for the profile.</exception>
    IOpcodeTable Get(string profileId);

    /// <summary>Attempts to resolve a table without throwing.</summary>
    /// <param name="profileId">The client profile id.</param>
    /// <param name="table">Receives the table, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a table was registered.</returns>
    bool TryGet(string profileId, out IOpcodeTable? table);
}

/// <summary>Registry of client profiles keyed by profile id.</summary>
public interface IClientProfileRegistry
{
    /// <summary>Registers a client profile.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>A disposer that removes the registration.</returns>
    IDisposable Register(ClientProfile profile);

    /// <summary>Gets every registered profile.</summary>
    IReadOnlyList<ClientProfile> All { get; }

    /// <summary>Resolves a profile by id.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>The registered profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no profile is registered under the id.</exception>
    ClientProfile Get(string id);
}

/// <summary>One inbound frame body with its opcode.</summary>
/// <param name="Opcode">The wire opcode.</param>
/// <param name="Payload">The frame payload.</param>
public readonly record struct InboundPacket(ushort Opcode, ReadOnlyMemory<byte> Payload);

/// <summary>
/// A protocol-level session bound to a connection and a client profile.
/// Implementations supporting typed state expose <see cref="ISessionWithFeatures"/>;
/// <see cref="Items"/> remains the legacy migration surface.
/// </summary>
public interface ISession
{
    /// <summary>Gets the session id.</summary>
    long Id { get; }

    /// <summary>Gets the underlying connection.</summary>
    IConnection Connection { get; }

    /// <summary>Gets the client profile the session belongs to.</summary>
    ClientProfile Profile { get; }

    /// <summary>Gets legacy per-session state keyed by name; new features use ISessionWithFeatures.</summary>
    ConcurrentDictionary<string, object> Items { get; }

    /// <summary>
    /// Queues a frame for this session. The call completes once the frame is queued, not
    /// written; frames reach the wire in the order they were queued on this session. A send
    /// after the session closed, or into a full queue, fails.
    /// </summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="payload">The frame payload; copied before the call returns.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>Registry of live sessions. Provided by the kernel built-in row
/// <c>core.session-registry</c>.</summary>
public interface ISessionRegistry
{
    /// <summary>Adds a session.</summary>
    /// <param name="session">The session.</param>
    void Add(ISession session);

    /// <summary>Removes a session.</summary>
    /// <param name="session">The session.</param>
    void Remove(ISession session);

    /// <summary>Gets a snapshot of every live session.</summary>
    IReadOnlyList<ISession> All { get; }
}

/// <summary>Handles one inbound packet for a session.</summary>
/// <param name="session">The session the packet arrived on.</param>
/// <param name="packet">The inbound packet.</param>
/// <param name="ct">Cancellation token.</param>
public delegate ValueTask PacketHandler(ISession session, InboundPacket packet, CancellationToken ct);

/// <summary>
/// Routes inbound packets to handlers keyed by (protocol name, opcode). Provided by the
/// kernel built-in row <c>core.packet-router</c>. Unknown opcodes are never silently
/// dropped: <see cref="PacketReceivedEvent"/> is emitted with <c>HasHandler</c> false.
/// </summary>
public interface IPacketRouter
{
    /// <summary>Registers a handler for one opcode of one named protocol.</summary>
    /// <param name="protocolName">The connection protocol name, for example <c>kr-1256/game</c>.</param>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>A disposer that removes the registration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the pair is already registered.</exception>
    IDisposable Register(string protocolName, ushort opcode, PacketHandler handler);

    /// <summary>Awaits serial observers of <see cref="PacketReceivedEvent"/> then invokes the registered handler, if any.</summary>
    /// <param name="protocolName">The connection protocol name.</param>
    /// <param name="session">The session the packet arrived on.</param>
    /// <param name="packet">The inbound packet.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct);
}

/// <summary>Emitted by the packet router before dispatching an inbound packet.</summary>
/// <param name="Session">The session the packet arrived on.</param>
/// <param name="ProtocolName">The connection protocol name.</param>
/// <param name="Packet">The inbound packet.</param>
/// <param name="HasHandler">Whether a handler is registered for the opcode.</param>
public sealed record PacketReceivedEvent(ISession Session, string ProtocolName, InboundPacket Packet, bool HasHandler) : IEvent;

/// <summary>Emitted when a session sends a frame.</summary>
/// <param name="Session">The sending session.</param>
/// <param name="ProtocolName">The connection protocol name.</param>
/// <param name="Opcode">The wire opcode.</param>
/// <param name="PayloadLength">The payload byte length.</param>
public sealed record PacketSentEvent(ISession Session, string ProtocolName, ushort Opcode, int PayloadLength) : IEvent
{
    /// <summary>Gets the unencrypted payload, valid during synchronous event observation; retaining it requires a copy.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>Emitted when a client's reported version does not match the expected version.</summary>
/// <param name="Session">The session.</param>
/// <param name="Reported">The version the client reported.</param>
/// <param name="Expected">The version the profile requires.</param>
public sealed record ClientVersionMismatchEvent(ISession Session, int Reported, int Expected) : IEvent;
