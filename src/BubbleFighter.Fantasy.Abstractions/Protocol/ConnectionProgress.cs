namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>Emitted after the native greeting has been flushed to the outbound transport pipe, without its contents.</summary>
/// <param name="ConnectionId">The transport connection identifier.</param>
/// <param name="ProfileId">The configured client profile identifier.</param>
/// <param name="ProtocolName">The owning connection protocol.</param>
/// <param name="DialectId">The wire dialect used by the connection.</param>
/// <param name="ByteLength">The number of plaintext greeting bytes written.</param>
/// <param name="Status">The configured native greeting status.</param>
public sealed record BootstrapGreetingWrittenEvent(long ConnectionId, string ProfileId, string ProtocolName,
    string DialectId, int ByteLength, ushort Status) : IEvent;

/// <summary>Emitted once when the first inbound frame is decoded, before handshake admission; contains no payload.</summary>
/// <param name="ConnectionId">The transport connection identifier.</param>
/// <param name="ProfileId">The configured client profile identifier.</param>
/// <param name="ProtocolName">The owning connection protocol.</param>
/// <param name="Opcode">The decoded client opcode.</param>
/// <param name="PayloadLength">The payload byte count, excluding the frame header.</param>
public sealed record FirstInboundFrameEvent(long ConnectionId, string ProfileId, string ProtocolName,
    ushort Opcode, int PayloadLength) : IEvent;
