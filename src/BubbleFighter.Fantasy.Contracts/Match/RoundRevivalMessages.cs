using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Requests consumption of one instant-respawn entitlement for the sender's dead character.</summary>
public sealed record RoundInstantRevivalRequested(int Actor, int ActorSeat) : IMessage;

/// <summary>Requests mode-specific revival of the sender's dead PvE character, independently of inventory use.</summary>
public sealed record RoundPveRevivalRequested : IMessage;
