using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Installs the ordered native cooperative item slots before the round begins.</summary>
public sealed record RoundCountedItemsPrepared(IReadOnlyList<int> Kinds) : IMessage;

/// <summary>Requests consumption of one counted item through its native slot index.</summary>
public sealed record RoundCountedItemUseRequested(int ItemSlot) : IMessage;

/// <summary>Confirms a counted item use by the player controlling the specified seat.</summary>
public sealed record RoundCountedItemUsed(int Seat, int Kind) : IMessage;
