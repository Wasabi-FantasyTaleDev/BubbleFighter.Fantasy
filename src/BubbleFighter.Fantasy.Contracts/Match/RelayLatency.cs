using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Requests an acknowledgement of a native relay delay probe.</summary>
/// <param name="ClientTick">All 32 bits of the client's local send tick; it is not a server timestamp.</param>
public sealed record RelayLatencyRequested(uint ClientTick) : IMessage;

/// <summary>Acknowledges a relay probe with the opaque word consumed by the client.</summary>
/// <param name="Unknown4">Configured response word whose value the audited client handlers discard.</param>
public sealed record RelayLatencyAcknowledged(uint Unknown4) : IMessage;
