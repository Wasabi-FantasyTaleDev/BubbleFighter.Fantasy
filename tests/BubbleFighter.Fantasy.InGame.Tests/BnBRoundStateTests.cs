using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks generation ownership, item grants, chained blasts and publication-isolated round transitions.</summary>
public sealed class BnBRoundStateTests
{
    private const int First = 1000000;
    private const int Second = 1000001;
    private static readonly BnBRoundRules Rules = new(3000, 500, 700, 6, 6, 6, 0.2f, 2f);

    /// <summary>An occupied cell consumes no generation, and a candidate's changes cannot reach its committed source.</summary>
    [Fact]
    public void OccupiedPlacementAndCloneKeepGenerationExact()
    {
        var original = Round(BnBTestData.Map(3, 1));
        var candidate = original.Clone();
        Assert.True(candidate.TryPlace(First, 0, 0, out var first));
        Assert.Equal(new BnBBombIdentity(0, 0), first);
        Assert.False(candidate.TryPlace(Second, 0, 19, out _));
        Assert.True(candidate.TryPlace(Second, 1, 20, out var second));
        Assert.Equal(new BnBBombIdentity(1, 1), second);
        Assert.Equal(2, candidate.NextGeneration);
        Assert.Equal(0, original.NextGeneration);
        Assert.Equal(BnBBombState.Empty, original.GetCell(0).Bomb);
    }

    /// <summary>A fuse only becomes reportable; flame and block timers expire independently after an accepted explosion.</summary>
    [Fact]
    public void TimersDoNotInventAnExplosionAndExposeItemsBeforeBlockRetirement()
    {
        var round = Round(BnBTestData.Map(3, 1, [0, 1, 0], [0, 2, 0]));
        Assert.True(round.TryPlace(First, 0, 0, out var bomb));
        Assert.Null(round.NextTimerDeadlineMilliseconds);
        Assert.Equal(3050L, round.NextBombReportDeadlineMilliseconds(50));
        Assert.Empty(round.DueBombs(First, 3049, 50));
        Assert.Equal(new[] { bomb }, round.DueBombs(First, 3050, 50));
        round.AdvanceTimers(3050);
        Assert.Equal(BnBBombState.Installed, round.GetCell(0).Bomb);
        Assert.False(round.HasAvailableItem(1));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [bomb], 3050));
        Assert.True(round.HasAvailableItem(1));
        Assert.Equal(BnBBlockState.Destroying, round.GetCell(1).Block);
        Assert.Equal(3550L, round.NextTimerDeadlineMilliseconds);
        round.AdvanceTimers(3550);
        Assert.Equal(BnBBombState.Empty, round.GetCell(1).Bomb);
        Assert.Equal(BnBBlockState.Destroying, round.GetCell(1).Block);
        Assert.Equal(3750L, round.NextTimerDeadlineMilliseconds);
        round.AdvanceTimers(3750);
        Assert.Equal(BnBBlockState.Empty, round.GetCell(1).Block);
        Assert.True(round.HasAvailableItem(1));
        Assert.Null(round.NextTimerDeadlineMilliseconds);
    }

    /// <summary>A chained bomb uses its placement-time reach, and the incoming ray continues past bomb cells.</summary>
    [Fact]
    public void ChainedBombUsesItsOwnReachBeforeItsFuseIsDue()
    {
        var round = Round(BnBTestData.Map(8, 1, items: [0, 0, 0, 0, 0, 0, 2, 2]));
        Assert.True(round.TryPickup(Second, 6, 0, out _));
        Assert.True(round.TryPickup(Second, 7, 0, out _));
        Assert.True(round.TryPlace(First, 1, 0, out var first));
        Assert.True(round.TryPlace(Second, 2, 20, out var second));
        Assert.Equal(3, round.GetCell(2).Reach);
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [first], 3000));
        Assert.Equal(BnBBombState.Flame, round.GetCell(5).Bomb);
        Assert.Equal(BnBBombState.Empty, round.GetCell(6).Bomb);
        Assert.Equal(0, round.ActiveBombCount(Second));
        Assert.Equal(BnBExplosionResult.Retired, round.TryExplode(Second, [second], 3020));
        Assert.Equal(1, round.NextExplosionEvent);
    }

    /// <summary>The solid blocking cell is included in a flame operation but protects the cell beyond it.</summary>
    [Fact]
    public void SolidCellStopsRayAfterBeingIncluded()
    {
        var round = Round(BnBTestData.Map(4, 1, [0, 0, 2, 0], [2, 0, 0, 0]));
        Assert.True(round.TryPickup(First, 0, 0, out _));
        Assert.True(round.TryPlace(First, 1, 0, out var bomb));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [bomb], 3000));
        Assert.Equal(BnBBlockState.Solid, round.GetCell(2).Block);
        Assert.Equal(BnBBombState.Flame, round.GetCell(2).Bomb);
        Assert.Equal(BnBBombState.Empty, round.GetCell(3).Bomb);
    }

    /// <summary>A horizontal ray at the edge of one row cannot enter the next row.</summary>
    [Fact]
    public void RaysDoNotWrapBetweenRows()
    {
        var round = Round(BnBTestData.Map(3, 2));
        Assert.True(round.TryPlace(First, 2, 0, out var bomb));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [bomb], 3000));
        Assert.Equal(BnBBombState.Flame, round.GetCell(5).Bomb);
        Assert.Equal(BnBBombState.Empty, round.GetCell(3).Bomb);
    }

    /// <summary>A destructible blocker shields the entire chain until traversal finishes and exposes its preselected item.</summary>
    [Fact]
    public void SameChainDoesNotPassThroughNewlyDestroyedBlock()
    {
        var round = Round(BnBTestData.Map(7, 1, [0, 0, 0, 1, 0, 0, 0], [2, 0, 0, 3, 0, 0, 2]));
        Assert.True(round.TryPickup(First, 0, 0, out _));
        Assert.True(round.TryPickup(First, 6, 0, out _));
        Assert.True(round.TryPlace(First, 1, 0, out var bomb));
        Assert.True(round.TryPlace(Second, 2, 0, out _));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [bomb], 3000));
        Assert.Equal(BnBBlockState.Destroying, round.GetCell(3).Block);
        Assert.Equal(BnBItemKind.Roller, round.GetCell(3).Item);
        Assert.Equal(BnBBombState.Empty, round.GetCell(4).Bomb);
    }

    /// <summary>Explicit roots are sequential, allowing the next root to cross a previously destroyed blocker and remove its item.</summary>
    [Fact]
    public void ExplicitRootOrderChangesPropagationAndCanDestroyExposedItem()
    {
        var round = Round(BnBTestData.Map(3, 3, [0, 0, 0, 0, 1, 0, 0, 0, 0], [0, 0, 0, 0, 3, 0, 0, 2, 1]));
        Assert.True(round.TryPickup(First, 7, 0, out _));
        Assert.True(round.TryPickup(First, 8, 0, out _));
        Assert.True(round.TryPlace(First, 1, 0, out var first));
        Assert.True(round.TryPlace(First, 3, 0, out var second));
        var reverse = round.Clone();
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [first, second], 3000));
        Assert.Equal(BnBExplosionResult.Applied, reverse.TryExplode(First, [second, first], 3000));
        Assert.Equal(BnBBombState.Flame, round.GetCell(5).Bomb);
        Assert.Equal(BnBBombState.Empty, reverse.GetCell(5).Bomb);
        Assert.Equal(BnBItemKind.None, round.GetCell(4).Item);
        Assert.Equal(2, round.NextExplosionEvent);
    }

    /// <summary>Early, forged or partially stale reports cannot apply their otherwise valid leading roots.</summary>
    [Fact]
    public void ExplosionValidationIsAtomicAcrossTheWholeReport()
    {
        var round = Round(BnBTestData.Map(4, 1, items: [0, 0, 0, 1]));
        Assert.True(round.TryPickup(First, 3, 0, out _));
        Assert.True(round.TryPlace(First, 0, 0, out var first));
        Assert.True(round.TryPlace(First, 3, 0, out var second));
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(First, [first], 2999));
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(Second, [first], 3000));
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(First, [first, second with { Generation = 99 }], 3000));
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(First, [first, first], 3000));
        Assert.Equal(0, round.NextExplosionEvent);
        Assert.Equal(2, round.ActiveBombCount(First));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [first, second], 3000));
    }

    /// <summary>A fully retired identity remains attributable to its placer after native flame cleanup clears the visible owner.</summary>
    [Fact]
    public void RetiredReportsCannotReapplyFlamesOrCrossIntoReplacementGeneration()
    {
        var round = Round(BnBTestData.Map(2, 1));
        Assert.True(round.TryPlace(First, 0, 0, out var old));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [old], 3000));
        round.AdvanceTimers(3500);
        Assert.Equal(-1, round.GetCell(0).Actor);
        Assert.Equal(BnBExplosionResult.Retired, round.TryExplode(First, [old], 3500));
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(Second, [old], 3500));
        Assert.Null(round.NextTimerDeadlineMilliseconds);
        Assert.True(round.TryPlace(Second, 0, 3500, out var current));
        Assert.Equal(1, current.Generation);
        Assert.Equal(BnBExplosionResult.Rejected, round.TryExplode(First, [old], 6500));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(Second, [current], 6500));
    }

    /// <summary>A bomb placed in flames survives the old flame deadline and snapshots reach before later item collection.</summary>
    [Fact]
    public void ReplacingFlameClearsItsTimerWithoutChangingStoredReach()
    {
        var round = Round(BnBTestData.Map(4, 1, items: [0, 0, 0, 2]));
        Assert.True(round.TryPlace(First, 0, 0, out var old));
        Assert.Equal(BnBExplosionResult.Applied, round.TryExplode(First, [old], 3000));
        Assert.True(round.TryPlace(Second, 0, 3001, out _));
        Assert.True(round.TryPickup(Second, 3, 3002, out _));
        round.AdvanceTimers(3500);
        Assert.Equal(BnBBombState.Installed, round.GetCell(0).Bomb);
        Assert.Equal(1, round.GetCell(0).Reach);
        Assert.Equal(2, round.ExplosionRange(Second));
    }

    /// <summary>Stored pickup counts remain uncapped while effective ability limits and active bomb capacity clamp separately.</summary>
    [Fact]
    public void ItemsGrantOnceAndAbilityCapsDoNotDiscardCounts()
    {
        var round = new BnBRoundState(BnBTestData.Map(6, 1, items: [1, 1, 1, 3, 3, 3]),
            Rules with { MaximumBombCount = 2, MaximumSpeedLevel = 2 }, 0, new[] { First, Second }, 2);
        for (var index = 0; index < 6; index++) Assert.True(round.TryPickup(First, index, 0, out _));
        Assert.False(round.TryPickup(Second, 0, 0, out var empty));
        Assert.Equal(BnBItemKind.None, empty);
        Assert.Equal(3, round.ItemCount(First, BnBItemKind.Bomb));
        Assert.Equal(3, round.ItemCount(First, BnBItemKind.Roller));
        Assert.Equal(2.4f, round.MovementSpeed(First));
        Assert.True(round.TryPlace(First, 0, 0, out _));
        Assert.True(round.TryPlace(First, 2, 0, out _));
        Assert.False(round.TryPlace(First, 4, 0, out _));
        Assert.Equal(2, round.NextGeneration);
        Assert.Equal(0, round.ItemCount(Second, BnBItemKind.Bomb));
    }

    /// <summary>A hidden item and invalid actor cannot be granted by the safe ledger API.</summary>
    [Fact]
    public void HiddenItemsAndUnknownActorsAreRejected()
    {
        var round = Round(BnBTestData.Map(3, 1, [0, 1, 2], [1, 2, 3]));
        Assert.False(round.TryPickup(99, 0, 0, out _));
        Assert.False(round.TryPickup(First, 1, 0, out _));
        Assert.False(round.TryPickup(First, 2, 0, out _));
        Assert.False(round.TryPlace(First, 1, 0, out _));
        Assert.False(round.TryPlace(First, 2, 0, out _));
        Assert.False(round.TryPlace(First, -1, 0, out _));
        Assert.True(round.HasAvailableItem(0));
        Assert.Equal(BnBItemKind.Fire, round.GetCell(1).Item);
        Assert.Equal(0, round.NextGeneration);
    }

    /// <summary>The participant set and clock cannot expand or move backwards within a round.</summary>
    [Fact]
    public void RoundBoundsRejectInvalidParticipantsAndClockRegression()
    {
        var map = BnBTestData.Map(1, 1);
        Assert.Throws<ArgumentException>(() => new BnBRoundState(map, Rules, 0, new[] { First, First }, 2));
        Assert.Throws<ArgumentException>(() => new BnBRoundState(map, Rules, 0, new[] { First, Second }, 1));
        var round = Round(map);
        round.AdvanceTimers(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => round.AdvanceTimers(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => round.TryExplode(First, [], 100, -1));
    }

    private static BnBRoundState Round(BnBMapData map) => new(map, Rules, 422402801, new[] { First, Second }, 2);
}
