using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A local Rhea character's request to enter or leave its charged transformation.</summary>
/// <param name="Actor">The character's scene object handle, distinct from its room seat.</param>
/// <param name="Active">Whether the character requests its transformed form.</param>
public sealed record RoundCharacterFormRequested(int Actor, bool Active) : IMessage;

/// <summary>Applies an accepted Rhea form to the named character on the receiving client.</summary>
/// <param name="Actor">The authenticated character's scene object handle.</param>
/// <param name="Active">Whether the transformed form is active.</param>
public sealed record RoundCharacterFormChanged(int Actor, bool Active) : IMessage;
