using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Initializes the session signature for the mode selected when a match starts.</summary>
/// <param name="GameMode">The room mode that the client compares with its session signature.</param>
public sealed record MatchSessionSignature(ushort GameMode) : IMessage;
