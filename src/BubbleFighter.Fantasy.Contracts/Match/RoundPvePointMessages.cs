using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Reports the resource score-unit index of an NPC killed by the sending player.</summary>
/// <param name="UnitIndex">The NPC's serverPangScoreUnitnum, rather than an entity or seat reference.</param>
public sealed record RoundPvePangReported(int UnitIndex) : IMessage;

/// <summary>One player's independent cooperative points and its current rank.</summary>
/// <param name="Slot">The nonnegative native player seat.</param>
/// <param name="Points">The total awarded by accepted cooperative score-unit reports.</param>
/// <param name="Rank">One-based competition rank, with equal totals sharing a rank.</param>
public sealed record RoundPvePointRow(int Slot, int Points, int Rank);

/// <summary>Publishes a cooperative point delta and the complete current ranking to every room member.</summary>
/// <param name="Slot">The player whose point balance changes.</param>
/// <param name="Amount">The positive increment for an accepted NPC report, or zero for ranking initialization without a kill.</param>
/// <param name="Rows">The active native seats and their current totals and ranks.</param>
public sealed record RoundPvePointsChanged(int Slot, int Amount, IReadOnlyList<RoundPvePointRow> Rows) : IMessage;
