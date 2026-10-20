using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Requests the connected center's display identity and occupancy.</summary>
public sealed record CenterStatusRequested : IMessage;

/// <summary>Describes one native center selector entry on its own connection.</summary>
public sealed record CenterStatusReplied(int CenterId, string Name, int Users, int Capacity) : IMessage;

/// <summary>Checks whether the player's auxiliary center connection may remain listed.</summary>
public sealed record CenterAvailabilityRequested(int UserNum) : IMessage;

/// <summary>Reports whether the connected center must be removed from the native selector.</summary>
public sealed record CenterAvailabilityReplied(bool Unavailable) : IMessage;
