using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A local player's contact with a map-defined portal or launch region.</summary>
/// <param name="PortalId">The map's native region key.</param>
/// <param name="X">The reported contact position's first component.</param>
/// <param name="Y">The reported contact position's second component.</param>
/// <param name="Z">The reported contact position's third component.</param>
public sealed record RoundPortalMoveRequested(ushort PortalId, float X, float Y, float Z) : IMessage;

/// <summary>Authorizes the client's native map-defined movement for one authenticated seat.</summary>
/// <param name="PortalId">The map region whose own bounds and kind determine the movement.</param>
/// <param name="Seat">The room seat whose character touched the region.</param>
public sealed record RoundPortalMoved(ushort PortalId, int Seat) : IMessage;
