using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Reports the host's start or completion of one NPC wave.</summary>
/// <param name="Wave">The client wave index.</param>
/// <param name="Started">True for a starting wave, false for a cleared wave.</param>
public sealed record RoundWaveReported(int Wave, bool Started) : IMessage;

/// <summary>Announces an NPC wave transition to every member of its round.</summary>
/// <param name="Wave">The client wave index.</param>
/// <param name="Started">True for a starting wave, false for a cleared wave.</param>
public sealed record RoundWaveChanged(int Wave, bool Started) : IMessage;
