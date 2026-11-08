using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// Keeps a dead player in observation when the round's rules prohibit respawning. The dialect encodes
/// the scene-object reference for its native observer consumer; the message does not choose the game mode.
/// </summary>
/// <param name="Actor">The dead player's scene object reference from its admitted death and respawn requests.</param>
public sealed record RoundPlayerObserved(int Actor) : IMessage;
