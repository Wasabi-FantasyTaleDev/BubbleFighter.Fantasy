using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// The one life record both protocol families move: every transition is refused outside the state it starts from,
/// so a duplicate, late or foreign report changes nothing, and a legacy death and a modern reset share one state.
/// </summary>
public sealed class ParticipantLifeTests
{
    private static readonly RecordingSession LinkA = new(1);
    private static readonly RecordingSession LinkB = new(2);

    [Fact]
    public void BubbleReleaseAndBurstOnlyApplyFromTheirOwnState()
    {
        var life = new ParticipantLife();
        Assert.False(life.Release());
        Assert.False(life.Burst());
        Assert.True(life.Bubble());
        Assert.False(life.Bubble());
        Assert.False(life.Die());
        Assert.True(life.Release());
        Assert.Equal(SeatLife.Alive, life.State);
        Assert.True(life.Bubble());
        Assert.True(life.Burst());
        Assert.Equal(SeatLife.Dead, life.State);
        Assert.False(life.DeathCleaned);
        Assert.False(life.Burst());
        Assert.False(life.Die());
    }

    [Fact]
    public void AResetIsGrantedOnlyAfterCleanupAndConfirmedOnlyOnItsLink()
    {
        var life = new ParticipantLife();
        Assert.False(life.CleanDeath());
        Assert.True(life.Die());
        Assert.False(life.CanGrantReset);
        Assert.True(life.CleanDeath());
        Assert.True(life.CanGrantReset);
        life.Grant(LinkA);
        Assert.False(life.RevivalOffered);
        Assert.False(life.CanGrantReset);
        Assert.False(life.ConfirmsGrant(LinkB));
        Assert.True(life.ConfirmsGrant(LinkA));
        life.Revive();
        Assert.Equal(SeatLife.Alive, life.State);
        Assert.False(life.DeathCleaned);
        Assert.Null(life.GrantLink);
        Assert.False(life.ConfirmsGrant(LinkA));
    }

    [Fact]
    public void AStaleOrUndeliveredGrantReturnsToDeadAndCanBeGrantedAgain()
    {
        var life = new ParticipantLife();
        life.Die();
        life.CleanDeath();
        life.Grant(LinkA);
        life.WithdrawStaleGrant(LinkA);
        Assert.Equal(SeatLife.ResetPending, life.State);
        life.WithdrawStaleGrant(LinkB);
        Assert.Equal(SeatLife.Dead, life.State);
        Assert.True(life.CanGrantReset);
        life.Grant(LinkB);
        life.GrantFailed();
        Assert.Equal(SeatLife.Dead, life.State);
        Assert.True(life.CanGrantReset);
        life.GrantFailed();
        Assert.Equal(SeatLife.Dead, life.State);
    }

    [Fact]
    public void ALegacyDeathRefreshesUntilItsRevivalIsOffered()
    {
        var life = new ParticipantLife();
        Assert.True(life.RecordDeath(1_000_000, serverTimed: false));
        Assert.Equal((SeatLife.Dead, 1_000_000, false), (life.State, life.DeathActor, life.ServerTimedWait));
        Assert.True(life.RecordDeath(1_000_001, serverTimed: true));
        Assert.Equal((SeatLife.Dead, 1_000_001, true), (life.State, life.DeathActor, life.ServerTimedWait));
        life.Grant(link: null);
        Assert.True(life.RevivalOffered);
        Assert.False(life.RecordDeath(1_000_002, serverTimed: false));
        Assert.Equal((1_000_001, true), (life.DeathActor, life.ServerTimedWait));
        life.Revive();
        Assert.Equal((SeatLife.Alive, (int?)null, false, false), (life.State, life.DeathActor, life.ServerTimedWait, life.RevivalOffered));
    }

    [Fact]
    public void AnEarlyRevivalIsTakenOnceAndSurvivesAConversionButNotARevival()
    {
        var life = new ParticipantLife();
        life.RequestRevivalEarly();
        life.RecordDeath(1_000_000, serverTimed: true);
        life.EndDeathByConversion();
        Assert.Equal((SeatLife.Alive, (int?)null, false), (life.State, life.DeathActor, life.ServerTimedWait));
        Assert.True(life.RevivalRequestedEarly);
        Assert.True(life.TakeEarlyRevival());
        Assert.False(life.TakeEarlyRevival());
        life.RequestRevivalEarly();
        life.Revive();
        Assert.False(life.RevivalRequestedEarly);
    }

    [Fact]
    public void ARoundResetClearsEveryPendingOperation()
    {
        var life = new ParticipantLife();
        life.RequestRevivalEarly();
        life.RecordDeath(1_000_000, serverTimed: true);
        life.CleanDeath();
        life.Grant(LinkA);
        life.Reset();
        Assert.Equal((SeatLife.Alive, false, (object?)null, (int?)null, false, false),
            (life.State, life.DeathCleaned, (object?)life.GrantLink, life.DeathActor, life.ServerTimedWait, life.RevivalRequestedEarly));
    }
}
