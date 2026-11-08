using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks bomb operation ownership and native transitions without replacing client operation timers.</summary>
public sealed class BombRoundStateTests
{
    /// <summary>Removing the exact operator cancels unfinished work while preserving a live bomb and its original countdown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperatorDepartureCancelsOnlyItsUnfinishedOperation(bool defusing)
    {
        var (bomb, planter, defuser) = Armed();
        if (defusing) Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
        else
        {
            bomb.Reset();
            Assert.True(Apply(bomb, RoundBombState.Planting, planter));
        }
        var owner = defusing ? defuser : planter;
        Assert.Null(bomb.CancelDepartedOperator([planter, defuser]));
        var replacement = Seat(owner.Slot, owner.UserNum, owner.Team);
        var changed = Assert.IsType<RoundBombStateChanged>(bomb.CancelDepartedOperator([replacement]));
        Assert.Equal(defusing ? RoundBombState.DefuseInterrupted : RoundBombState.Reset, changed.State);
        Assert.Equal(owner.UserNum, changed.Operator);
        Assert.False(changed.TimerAligned);
        Assert.Equal(defusing ? RoundBombState.Countdown : RoundBombState.Reset, bomb.State);
        Assert.Equal(defusing ? 100L : (long?)null, bomb.CountdownStartedAtMilliseconds);
        Assert.Null(bomb.CancelDepartedOperator([replacement]));
        Assert.Equal(defusing ? planter.UserNum : -1, bomb.Planter);
    }

    /// <summary>A planter leaving after arming does not disarm the bomb or replace its recorded operator.</summary>
    [Fact]
    public void ArmedBombSurvivesItsPlanterLeaving()
    {
        var (bomb, planter, defender) = Armed();
        Assert.Null(bomb.CancelDepartedOperator([defender]));
        Assert.True(bomb.IsArmed);
        Assert.Equal(planter.UserNum, bomb.Planter);
        Assert.Equal(100L, bomb.CountdownStartedAtMilliseconds);
    }

    /// <summary>Plant cancellation clears the operation; only a later completion arms the countdown.</summary>
    [Fact]
    public void PlantingIsNotArmedAndCancellationAllowsAnotherAttempt()
    {
        var bomb = new BombRoundState();
        var planter = Seat(0, 11, 1);
        Assert.True(bomb.TryApply(new(RoundBombState.Planting, 11, 3.5f, -1.25f, 42), planter, true, 100));
        Assert.False(bomb.IsArmed);
        Assert.Null(bomb.CountdownStartedAtMilliseconds);
        Assert.True(Apply(bomb, RoundBombState.Reset, planter));
        Assert.Equal(-1, bomb.Planter);
        Assert.Equal(0, bomb.PositionX);
        Assert.True(Apply(bomb, RoundBombState.Planting, planter));
        Assert.True(Apply(bomb, RoundBombState.Countdown, planter, now: 200));
        Assert.True(bomb.IsArmed);
        Assert.Equal(200L, bomb.CountdownStartedAtMilliseconds);
    }

    /// <summary>Interrupted defusing resumes the same countdown and permits a different defender to try.</summary>
    [Fact]
    public void DefuseInterruptionDoesNotRestartCountdown()
    {
        var (bomb, planter, defuser) = Armed();
        var anotherDefuser = Seat(2, 33, 2);
        Assert.True(Apply(bomb, RoundBombState.Defusing, defuser, now: 200));
        Assert.True(Apply(bomb, RoundBombState.DefuseInterrupted, defuser, now: 300));
        Assert.Equal(RoundBombState.Countdown, bomb.State);
        Assert.Equal(-1, bomb.Defuser);
        Assert.True(Apply(bomb, RoundBombState.Defusing, anotherDefuser, now: 400));
        Assert.Equal(100L, bomb.CountdownStartedAtMilliseconds);
        Assert.False(Apply(bomb, RoundBombState.Defused, defuser));
        Assert.True(Apply(bomb, RoundBombState.Defused, anotherDefuser));
        Assert.Equal(-1, bomb.Planter);
        Assert.Equal(anotherDefuser.UserNum, bomb.Defuser);
        Assert.False(bomb.IsArmed);
        Assert.False(Apply(bomb, RoundBombState.Exploded, planter, true));
    }

    /// <summary>The countdown master may be a defender and reports the planter even while dead.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MasterExplosionCarriesPlanterRatherThanMaster(bool defusing)
    {
        var (bomb, planter, master) = Armed();
        if (defusing) Assert.True(Apply(bomb, RoundBombState.Defusing, master));
        master.Life.Die();
        Assert.False(Apply(bomb, RoundBombState.Exploded, master, true));
        var explosion = new RoundBombStateReported(RoundBombState.Exploded, planter.UserNum,
            bomb.PositionX, bomb.PositionY, bomb.PositionZ);
        Assert.False(bomb.TryApply(explosion, master, false, 200));
        Assert.True(bomb.TryApply(explosion, master, true, 200));
        Assert.Equal(planter.UserNum, bomb.Planter);
        Assert.Equal(-1, bomb.Defuser);
        Assert.False(bomb.IsArmed);
        Assert.False(bomb.TryApply(explosion, master, true, 201));
    }

    /// <summary>A report cannot impersonate another user or reuse the owner's user number on another seat.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationCompletionRequiresExactOwner(bool defusing)
    {
        var (bomb, planter, defuser) = Armed();
        if (defusing) Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
        else
        {
            bomb.Reset();
            Assert.True(Apply(bomb, RoundBombState.Planting, planter));
        }
        var owner = defusing ? defuser : planter;
        var next = defusing ? RoundBombState.Defused : RoundBombState.Countdown;
        var impostor = Seat(2, owner.UserNum, owner.Team);
        var report = new RoundBombStateReported(next, owner.UserNum, bomb.PositionX, bomb.PositionY, bomb.PositionZ);
        Assert.False(bomb.TryApply(report, impostor, true, 200));
        Assert.False(bomb.TryApply(report with { Operator = owner.UserNum + 32 }, owner, true, 200));
        Assert.True(bomb.TryApply(report, owner, false, 200));
    }

    /// <summary>Teams follow the round's attacker selection and neither an observer nor the wrong side may operate.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AttackerAndDefenderAreSelectedPerRound(int attackingTeam)
    {
        var bomb = new BombRoundState();
        bomb.Reset(attackingTeam);
        var attacker = Seat(0, 11, attackingTeam);
        var defender = Seat(1, 22, 3 - attackingTeam);
        var observer = Seat(2, 33, 16);
        Assert.False(Apply(bomb, RoundBombState.Planting, defender));
        Assert.False(Apply(bomb, RoundBombState.Planting, observer));
        Assert.False(bomb.TryApply(new(RoundBombState.Planting, defender.UserNum, 0, 0, 0), attacker, true, 0));
        Assert.True(Apply(bomb, RoundBombState.Planting, attacker));
        Assert.True(Apply(bomb, RoundBombState.Countdown, attacker));
        Assert.False(Apply(bomb, RoundBombState.Defusing, attacker));
        Assert.False(Apply(bomb, RoundBombState.Defusing, observer));
        Assert.True(Apply(bomb, RoundBombState.Defusing, defender));
    }

    /// <summary>Death or bubbling prevents completion but must still allow the owner's cancellation.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DisabledOperatorCanCancelButCannotFinish(bool defusing, bool bubbled)
    {
        var (bomb, planter, defuser) = Armed();
        var owner = defusing ? defuser : planter;
        if (defusing) Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
        else
        {
            bomb.Reset();
            Assert.True(Apply(bomb, RoundBombState.Planting, planter));
        }
        if (bubbled) owner.Life.Bubble(); else owner.Life.Die();
        Assert.False(Apply(bomb, defusing ? RoundBombState.Defused : RoundBombState.Countdown, owner));
        Assert.True(Apply(bomb, defusing ? RoundBombState.DefuseInterrupted : RoundBombState.Reset, owner));
        Assert.False(Apply(bomb, defusing ? RoundBombState.Defusing : RoundBombState.Planting, owner));
    }

    /// <summary>No state outside the native transition graph may mutate the current operation.</summary>
    [Theory]
    [InlineData(RoundBombState.Reset, RoundBombState.Reset)]
    [InlineData(RoundBombState.Reset, RoundBombState.Countdown)]
    [InlineData(RoundBombState.Reset, RoundBombState.Defusing)]
    [InlineData(RoundBombState.Reset, RoundBombState.Defused)]
    [InlineData(RoundBombState.Reset, RoundBombState.Exploded)]
    [InlineData(RoundBombState.Planting, RoundBombState.Planting)]
    [InlineData(RoundBombState.Planting, RoundBombState.Defusing)]
    [InlineData(RoundBombState.Planting, RoundBombState.Exploded)]
    [InlineData(RoundBombState.Countdown, RoundBombState.Countdown)]
    [InlineData(RoundBombState.Countdown, RoundBombState.Reset)]
    [InlineData(RoundBombState.Countdown, RoundBombState.Planting)]
    [InlineData(RoundBombState.Countdown, RoundBombState.Defused)]
    [InlineData(RoundBombState.Defusing, RoundBombState.Defusing)]
    [InlineData(RoundBombState.Defusing, RoundBombState.Countdown)]
    [InlineData(RoundBombState.Defusing, RoundBombState.Reset)]
    [InlineData(RoundBombState.Defused, RoundBombState.Defused)]
    [InlineData(RoundBombState.Defused, RoundBombState.Planting)]
    [InlineData(RoundBombState.Exploded, RoundBombState.Exploded)]
    [InlineData(RoundBombState.Exploded, RoundBombState.Planting)]
    [InlineData(RoundBombState.Countdown, (RoundBombState)7)]
    public void InvalidTransitionLeavesStateUntouched(RoundBombState previous, RoundBombState next)
    {
        var (bomb, planter, defuser) = Armed();
        switch (previous)
        {
            case RoundBombState.Reset: bomb.Reset(); break;
            case RoundBombState.Planting:
                bomb.Reset();
                Assert.True(Apply(bomb, RoundBombState.Planting, planter));
                break;
            case RoundBombState.Defusing:
            case RoundBombState.Defused:
                Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
                if (previous == RoundBombState.Defused) Assert.True(Apply(bomb, previous, defuser));
                break;
            case RoundBombState.Exploded: Assert.True(Apply(bomb, previous, planter, true)); break;
        }
        var snapshot = (bomb.State, bomb.Planter, bomb.Defuser, bomb.CountdownStartedAtMilliseconds);
        var reporter = next is RoundBombState.Defusing or RoundBombState.Defused or RoundBombState.DefuseInterrupted
            ? defuser : planter;
        Assert.False(Apply(bomb, next, reporter, true));
        Assert.Equal(snapshot, (bomb.State, bomb.Planter, bomb.Defuser, bomb.CountdownStartedAtMilliseconds));
    }

    /// <summary>Malformed coordinates cannot enter the state, and later packets cannot move the planted bomb.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void CoordinatesMustRemainFiniteAndFixed(float invalid)
    {
        var bomb = new BombRoundState();
        var planter = Seat(0, 11, 1);
        foreach (var position in new[] { (invalid, 0f, 0f), (0f, invalid, 0f), (0f, 0f, invalid) })
            Assert.False(bomb.TryApply(new(RoundBombState.Planting, 11, position.Item1, position.Item2, position.Item3),
                planter, false, 0));
        Assert.True(bomb.TryApply(new(RoundBombState.Planting, 11, 3.5f, -1.25f, 42), planter, false, 0));
        Assert.False(bomb.TryApply(new(RoundBombState.Countdown, 11, invalid, -1.25f, 42), planter, false, 100));
        Assert.False(bomb.TryApply(new(RoundBombState.Countdown, 11, 4, -1.25f, 42), planter, false, 100));
        Assert.Equal(RoundBombState.Planting, bomb.State);
        Assert.Null(bomb.CountdownStartedAtMilliseconds);
    }

    /// <summary>A new round clears both owners and the previous countdown, and rejects a late completion.</summary>
    [Fact]
    public void ResetClearsPreviousRoundOperation()
    {
        var (bomb, planter, defuser) = Armed();
        Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
        bomb.Reset(2);
        Assert.Equal(-1, bomb.Planter);
        Assert.Equal(-1, bomb.Defuser);
        Assert.Equal(0, bomb.PositionX);
        Assert.Null(bomb.CountdownStartedAtMilliseconds);
        Assert.False(Apply(bomb, RoundBombState.Countdown, planter));
        Assert.False(Apply(bomb, RoundBombState.Defused, defuser));
        Assert.True(Apply(bomb, RoundBombState.Planting, defuser));
        Assert.Throws<ArgumentOutOfRangeException>(() => bomb.Reset(0));
    }

    /// <summary>Objective results retain precisely the operator references the native terminal state leaves behind.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectiveResultMustMatchWinnerAndRemainingOperator(bool defused)
    {
        var (bomb, planter, defuser) = Armed();
        if (defused)
        {
            Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
            Assert.True(Apply(bomb, RoundBombState.Defused, defuser));
        }
        else Assert.True(Apply(bomb, RoundBombState.Exploded, planter, true));
        var report = defused ? new RoundBombMissionOverReported(2, -1, 22) : new(1, 11, -1);
        Assert.True(bomb.MatchesResult(report, _ => false));
        Assert.False(bomb.MatchesResult(report with { WinningTeam = 3 - report.WinningTeam }, _ => true));
        Assert.False(bomb.MatchesResult(report with { Planter = report.Planter + 1 }, _ => true));
        Assert.False(bomb.MatchesResult(report with { Defuser = report.Defuser + 1 }, _ => true));
    }

    /// <summary>Before arming, elimination reports clear both operator references even during a plant attempt.</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void PreArmEliminationRequiresAnEliminatedOpponent(bool planting, int winner)
    {
        var bomb = new BombRoundState();
        if (planting) Assert.True(Apply(bomb, RoundBombState.Planting, Seat(0, 11, 1)));
        var report = new RoundBombMissionOverReported(winner, -1, -1);
        Assert.False(bomb.MatchesResult(report, _ => false));
        Assert.False(bomb.MatchesResult(report, team => team == winner));
        Assert.True(bomb.MatchesResult(report, team => team == 3 - winner));
        Assert.False(bomb.MatchesResult(report with { Planter = 11 }, _ => true));
        Assert.False(bomb.MatchesResult(report with { Defuser = 22 }, _ => true));
        Assert.False(bomb.MatchesResult(report with { WinningTeam = 3 }, _ => true));
    }

    /// <summary>An armed bomb outlives the attackers; only defender elimination permits a result without state 3.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArmedEliminationPreservesTheBombObjective(bool defusing)
    {
        var (bomb, _, defuser) = Armed();
        if (defusing) Assert.True(Apply(bomb, RoundBombState.Defusing, defuser));
        Assert.False(bomb.MatchesResult(new(2, -1, -1), team => team == 1));
        Assert.False(bomb.MatchesResult(new(1, 11, -1), _ => false));
        Assert.True(bomb.MatchesResult(new(1, 11, -1), team => team == 2));
        Assert.False(bomb.MatchesResult(new(1, 11, 22), team => team == 2));
    }

    private static (BombRoundState Bomb, RoomSeat Planter, RoomSeat Defuser) Armed()
    {
        var bomb = new BombRoundState();
        var planter = Seat(0, 11, 1);
        var defuser = Seat(1, 22, 2);
        Assert.True(bomb.TryApply(new(RoundBombState.Planting, 11, 3.5f, -1.25f, 42), planter, false, 0));
        Assert.True(Apply(bomb, RoundBombState.Countdown, planter, now: 100));
        return (bomb, planter, defuser);
    }

    private static RoomSeat Seat(int slot, int userNumber, int team) =>
        new(slot, new RecordingSession(userNumber), RoomRegistry.NewKey(), userNumber, "player") { Team = (byte)team };

    private static bool Apply(BombRoundState bomb, RoundBombState state, RoomSeat reporter, bool master = false, long now = 0) =>
        bomb.TryApply(new(state, reporter.UserNum, bomb.PositionX, bomb.PositionY, bomb.PositionZ), reporter, master, now);
}
