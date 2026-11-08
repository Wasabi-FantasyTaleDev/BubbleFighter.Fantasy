using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Lobby;

/// <summary>Requests the connected center's display identity and occupancy.</summary>
public sealed record CenterStatusRequested : IMessage;

/// <summary>Describes one native center selector entry on its own connection.</summary>
public sealed record CenterStatusReplied(int CenterId, string Name, int Users, int Capacity) : IMessage;

/// <summary>Checks the player's uniqueness through an unselected auxiliary center connection.</summary>
public sealed record CenterAvailabilityRequested(int UserNum) : IMessage;

/// <summary>Reports successful uniqueness cleanup when true; false enters the native login-failure path.</summary>
/// <param name="Unavailable">Whether the unselected auxiliary may be retired after a successful check, independent of server availability.</param>
public sealed record CenterAvailabilityReplied(bool Unavailable) : IMessage;
