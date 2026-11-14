using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Contracts.Titles;

/// <summary>
/// Commits a prepared round's medal ledger and immutable event receipt in the same account transaction as
/// its experience, wallet and match receipt. A provider without this capability must not accept medal plans.
/// </summary>
public interface IRoundTitleMedalSettlementRepository : IPlayerProgressRepository
{
}
