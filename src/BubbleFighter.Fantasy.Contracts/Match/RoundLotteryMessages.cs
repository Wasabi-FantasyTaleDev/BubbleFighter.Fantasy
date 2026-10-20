using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A player chooses one of the ten native cooperative reward cards.</summary>
/// <param name="Card">Zero-based card index.</param>
/// <param name="Seat">The client's claimed seat, checked against the server roster.</param>
public sealed record RoundLotteryRequested(int Card, int Seat) : IMessage;

/// <summary>Opens the native cooperative reward-card dialog after the reward scoreboard.</summary>
public sealed record RoundLotteryOpened : IMessage;

/// <summary>Ends the native result scene before the server returns participants to the waiting room.</summary>
public sealed record RoundLotteryClosed : IMessage;

/// <summary>Confirms a committed reward card, or rejects a stale or occupied selection.</summary>
/// <param name="Card">The selected card.</param>
/// <param name="Seat">The selecting player.</param>
/// <param name="Rejected">Whether no reward was committed.</param>
public sealed record RoundLotterySelected(int Card, int Seat, bool Rejected = false) : IMessage;

/// <summary>Reveals native sale identities on unclaimed cards after selection ends.</summary>
/// <param name="Items">Exactly ten Stock identities in card order, with zero for already claimed cards.</param>
public sealed record RoundLotteryRevealed(IReadOnlyList<int> Items) : IMessage;
