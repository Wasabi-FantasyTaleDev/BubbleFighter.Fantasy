using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;
using static BubbleFighter.Fantasy.InGame.Sessions.ZombieOriginCondition;
using static BubbleFighter.Fantasy.InGame.Sessions.ZombieOriginRole;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises KR-1256 mode 43 victory independently of role selection, transport and score presentation.</summary>
public sealed class ZombieOriginVictoryTests
{
    /// <summary>The last confirmed conversion wins; a missing fighter or a reported fighter pop does not prove infection.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryOriginalFighterMustBeConfirmedConverted(bool expired)
    {
        ZombieOriginParticipant[] roster = [new(Prototype, InPlay), new(ConvertedFighter, Popped), new(Fighter, Absent)];
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, expired));
        roster[2] = new(Fighter, Popped);
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, expired));
        roster[2] = new(ConvertedFighter, InPlay);
        Assert.Equal(ZombieOriginOutcome.ZombiesConvertedAllFighters, ZombieOriginVictory.Decide(roster, expired));
    }

    /// <summary>A fighter must remain in play at the deadline; a departed fighter does not survive on behalf of the team.</summary>
    [Fact]
    public void DeadlineAwardsSurvivalOnlyWithAPresentFighter()
    {
        ZombieOriginParticipant[] roster = [new(Prototype, InPlay), new(Fighter, Absent), new(Fighter, InPlay)];
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, false));
        Assert.Equal(ZombieOriginOutcome.FightersSurvivedTimeout, ZombieOriginVictory.Decide(roster, true));
        roster[2] = new(Fighter, Unknown);
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, true));
    }

    /// <summary>Every zombie counts, including converts. An absent or unconfirmed zombie prevents Perfect Win, even at time.</summary>
    [Fact]
    public void PerfectRequiresPositivePopsForPrototypesAndConvertedFighters()
    {
        ZombieOriginParticipant[] roster = [new(Fighter, InPlay), new(Prototype, Popped), new(ConvertedFighter, InPlay)];
        foreach (var remaining in new[] { InPlay, Absent, Unknown })
        {
            roster[2] = new(ConvertedFighter, remaining);
            Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, false));
            Assert.Equal(ZombieOriginOutcome.FightersSurvivedTimeout, ZombieOriginVictory.Decide(roster, true));
        }
        roster[2] = new(ConvertedFighter, Popped);
        Assert.Equal(ZombieOriginOutcome.FightersPerfectWin, ZombieOriginVictory.Decide(roster, false));
        Assert.Equal(ZombieOriginOutcome.FightersPerfectWin, ZombieOriginVictory.Decide(roster, true));
        roster[1] = new(Prototype, Absent);
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(roster, false));
        Assert.Equal(ZombieOriginOutcome.FightersSurvivedTimeout, ZombieOriginVictory.Decide(roster, true));
    }

    /// <summary>An empty, one-sided or partly assigned opening cannot finish even if its deadline has elapsed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningMustNameBothSidesAndEveryParticipant(bool expired)
    {
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide([], expired));
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide([new(Fighter, InPlay)], expired));
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide([new(Prototype, Popped)], expired));
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide([new(ConvertedFighter, InPlay)], expired));
        Assert.Equal(ZombieOriginOutcome.Undecided, ZombieOriginVictory.Decide(
            [new(Fighter, InPlay), new(Prototype, Popped), new(Unassigned, Absent)], expired));
    }
}
