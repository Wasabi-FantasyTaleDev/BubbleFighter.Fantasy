using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Tracks the single bomb and its operation owners during one playable round, under the room gate.</summary>
internal sealed class BombRoundState
{
    private RoomSeat? _planter;
    private RoomSeat? _defuser;

    internal RoundBombState State { get; private set; }
    internal int AttackingTeam { get; private set; } = 1;
    internal int DefendingTeam => 3 - AttackingTeam;
    internal int Planter => _planter?.UserNum ?? -1;
    internal int Defuser => _defuser?.UserNum ?? -1;
    internal float PositionX { get; private set; }
    internal float PositionY { get; private set; }
    internal float PositionZ { get; private set; }
    internal long? CountdownStartedAtMilliseconds { get; private set; }
    internal bool IsArmed => State is RoundBombState.Countdown or RoundBombState.Defusing;

    /// <summary>Cancels only an unfinished operation whose exact owning seat has left; an armed bomb retains its planter.</summary>
    internal RoundBombStateChanged? CancelDepartedOperator(IReadOnlyCollection<RoomSeat> seats)
    {
        var owner = State == RoundBombState.Planting ? _planter : State == RoundBombState.Defusing ? _defuser : null;
        if (owner is null || seats.Any(seat => ReferenceEquals(seat, owner))) return null;
        var state = State == RoundBombState.Planting ? RoundBombState.Reset : RoundBombState.DefuseInterrupted;
        var change = new RoundBombStateChanged(state, owner.UserNum, PositionX, PositionY, PositionZ);
        if (state == RoundBombState.Reset) Reset(AttackingTeam);
        else
        {
            _defuser = null;
            State = RoundBombState.Countdown;
        }
        return change;
    }

    /// <summary>Checks the master's result against the objective or a team eliminated in this round.</summary>
    internal bool MatchesResult(RoundBombMissionOverReported report, Func<int, bool> teamEliminated)
    {
        if (report.WinningTeam is not (1 or 2)) return false;
        if (State == RoundBombState.Exploded)
            return report.WinningTeam == AttackingTeam && report.Planter == Planter && report.Defuser == -1;
        if (State == RoundBombState.Defused)
            return report.WinningTeam == DefendingTeam && report.Planter == -1 && report.Defuser == Defuser;
        // kr-812 sub_52A640 -> kr-812 sub_4256C0 applies state 3 locally after the last defender falls.
        if (IsArmed)
            return report.WinningTeam == AttackingTeam && report.Planter == Planter && report.Defuser == -1
                && teamEliminated(DefendingTeam);
        // kr-812 sub_52A640 passes two cleared operator references for pre-arm elimination.
        return report.Planter == -1 && report.Defuser == -1 && teamEliminated(3 - report.WinningTeam);
    }

    internal void Reset(int attackingTeam = 1)
    {
        if (attackingTeam is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(attackingTeam));
        AttackingTeam = attackingTeam;
        State = RoundBombState.Reset;
        _planter = _defuser = null;
        PositionX = PositionY = PositionZ = 0;
        CountdownStartedAtMilliseconds = null;
    }

    /// <summary>Accepts only the native next transition from its exact operation owner, or the countdown master.</summary>
    internal bool TryApply(RoundBombStateReported report, RoomSeat reporter, bool isMaster, long nowMilliseconds)
    {
        if (!float.IsFinite(report.PositionX) || !float.IsFinite(report.PositionY) || !float.IsFinite(report.PositionZ))
            return false;

        // kr-812 sub_423AE0 stores the plant position; kr-812 sub_422850 reuses it for subsequent reports.
        if (report.State != RoundBombState.Planting
            && (report.PositionX != PositionX || report.PositionY != PositionY || report.PositionZ != PositionZ))
            return false;

        switch (report.State)
        {
            case RoundBombState.Planting:
                if (State != RoundBombState.Reset || !CanOperate(reporter, report.Operator, AttackingTeam))
                    return false;
                _planter = reporter;
                PositionX = report.PositionX;
                PositionY = report.PositionY;
                PositionZ = report.PositionZ;
                break;
            case RoundBombState.Reset:
                // kr-812 sub_422850 sends cancellation even when the character can no longer operate.
                if (State != RoundBombState.Planting || !Owns(_planter, reporter, report.Operator))
                    return false;
                Reset(AttackingTeam);
                return true;
            case RoundBombState.Countdown:
                if (State != RoundBombState.Planting || !Owns(_planter, reporter, report.Operator)
                    || !CanOperate(reporter, report.Operator, AttackingTeam))
                    return false;
                CountdownStartedAtMilliseconds = nowMilliseconds;
                break;
            case RoundBombState.Defusing:
                if (State != RoundBombState.Countdown || !CanOperate(reporter, report.Operator, DefendingTeam))
                    return false;
                _defuser = reporter;
                break;
            case RoundBombState.DefuseInterrupted:
                if (State != RoundBombState.Defusing || !Owns(_defuser, reporter, report.Operator))
                    return false;
                // kr-812 sub_4245B0 case 5 returns to state 2 without rearming its countdown.
                _defuser = null;
                State = RoundBombState.Countdown;
                return true;
            case RoundBombState.Defused:
                if (State != RoundBombState.Defusing || !Owns(_defuser, reporter, report.Operator)
                    || !CanOperate(reporter, report.Operator, DefendingTeam))
                    return false;
                // kr-812 sub_4245B0 case 6 clears the planter before the master's mission-over report.
                _planter = null;
                PositionX = PositionY = PositionZ = 0;
                break;
            case RoundBombState.Exploded:
                // kr-812 sub_422850 passes the saved planter, not the master's user number, to kr-812 sub_52A590.
                if (!IsArmed || !isMaster || report.Operator != Planter)
                    return false;
                _defuser = null;
                break;
            default:
                return false;
        }

        State = report.State;
        return true;
    }

    private static bool CanOperate(RoomSeat reporter, int userNumber, int team) =>
        reporter.UserNum == userNumber && reporter.Team == team && reporter.Life.State == SeatLife.Alive;

    private static bool Owns(RoomSeat? owner, RoomSeat reporter, int userNumber) =>
        ReferenceEquals(owner, reporter) && reporter.UserNum == userNumber;
}
