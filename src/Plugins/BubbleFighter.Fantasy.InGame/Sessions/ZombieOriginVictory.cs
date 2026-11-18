using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>A prototype is confirmed only after it becomes a zombie; selecting a recipient or sending its roll is insufficient.</summary>
internal enum ZombieOriginRole { Unassigned, Fighter, Prototype, ConvertedFighter }

/// <summary>Only a confirmed pop counts towards Perfect Win. A missing connection or a pending life report is not a pop.</summary>
internal enum ZombieOriginCondition { Unknown, InPlay, Popped, Absent }

/// <summary>One entry per original participant, retained on departure; a confirmed conversion keeps the fighter's origin.</summary>
internal readonly record struct ZombieOriginParticipant(ZombieOriginRole Role, ZombieOriginCondition Condition);

/// <summary>Evaluates the objectives shown by the KR-1256 Zombie Origin loading guides 02 and 03.</summary>
internal static class ZombieOriginVictory
{
    /// <summary>Uses confirmed facts for the complete opening roster at one instant; the caller owns deadlines and event order.</summary>
    internal static ZombieOriginOutcome Decide(ReadOnlySpan<ZombieOriginParticipant> openingRoster, bool timeExpired)
    {
        var hasPrototype = false;
        var hasOriginalFighter = false;
        var hasUnconvertedFighter = false;
        var hasSurvivingFighter = false;
        var allZombiesPopped = true;
        foreach (var participant in openingRoster)
        {
            switch (participant.Role)
            {
                case ZombieOriginRole.Fighter:
                    hasOriginalFighter = hasUnconvertedFighter = true;
                    hasSurvivingFighter |= participant.Condition == ZombieOriginCondition.InPlay;
                    break;
                case ZombieOriginRole.Prototype:
                    hasPrototype = true;
                    allZombiesPopped &= participant.Condition == ZombieOriginCondition.Popped;
                    break;
                case ZombieOriginRole.ConvertedFighter:
                    hasOriginalFighter = true;
                    allZombiesPopped &= participant.Condition == ZombieOriginCondition.Popped;
                    break;
                default:
                    return ZombieOriginOutcome.Undecided;
            }
        }

        if (!hasPrototype || !hasOriginalFighter) return ZombieOriginOutcome.Undecided;
        if (!hasUnconvertedFighter) return ZombieOriginOutcome.ZombiesConvertedAllFighters;
        if (!hasSurvivingFighter) return ZombieOriginOutcome.Undecided;
        if (allZombiesPopped) return ZombieOriginOutcome.FightersPerfectWin;
        return timeExpired ? ZombieOriginOutcome.FightersSurvivedTimeout : ZombieOriginOutcome.Undecided;
    }
}
