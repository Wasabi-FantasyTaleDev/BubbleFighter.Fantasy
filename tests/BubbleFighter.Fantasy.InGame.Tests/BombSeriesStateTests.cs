using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks best-of-three bomb outcomes and stale-result admission independently of score or reward storage.</summary>
public sealed class BombSeriesStateTests
{
    /// <summary>All possible best-of-three results finish at the second win and swap attackers between rounds.</summary>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 1, 2)]
    public void SeriesStopsAtTheSecondWin(int first, int second, int third)
    {
        var series = new BombSeriesState();
        var winners = third == 0 ? new[] { first, second } : [first, second, third];
        var counts = new int[2];
        for (var index = 0; index < winners.Length; index++)
        {
            Assert.Equal(index, series.RoundIndex);
            Assert.Equal(index % 2 + 1, series.AttackingTeam);
            Assert.Null(series.RoundWinner);
            Assert.False(series.TryContinue(index));
            Assert.True(series.TryCompleteRound(index, winners[index]));
            counts[winners[index] - 1]++;
            Assert.Equal(counts, series.SnapshotWins());
            Assert.Equal(winners[index], series.RoundWinner);
            Assert.False(series.TryCompleteRound(index, 3 - winners[index]));
            if (index == winners.Length - 1)
            {
                Assert.Equal(winners[index], series.MatchWinner);
                Assert.False(series.CanContinue);
                Assert.False(series.TryContinue(index));
            }
            else
            {
                Assert.Null(series.MatchWinner);
                Assert.True(series.CanContinue);
                Assert.True(series.TryContinue(index));
                Assert.False(series.TryCompleteRound(index, winners[index]));
                Assert.False(series.TryContinue(index));
            }
        }
        Assert.False(series.TryCompleteRound(series.RoundIndex + 1, 1));
        Assert.Equal(counts, series.SnapshotWins());
    }

    /// <summary>Invalid team numbers and future or stale indices cannot change the recorded outcome.</summary>
    [Fact]
    public void InvalidCompletionDoesNotMutateSeries()
    {
        var series = new BombSeriesState();
        Assert.False(series.TryCompleteRound(0, 0));
        Assert.False(series.TryCompleteRound(0, 3));
        Assert.False(series.TryCompleteRound(-1, 1));
        Assert.False(series.TryCompleteRound(1, 1));
        Assert.Null(series.RoundWinner);
        Assert.Equal(new[] { 0, 0 }, series.SnapshotWins());
        Assert.True(series.TryCompleteRound(0, 2));
        Assert.False(series.TryContinue(1));
        Assert.True(series.TryContinue(0));
        Assert.False(series.TryCompleteRound(0, 1));
        Assert.Equal(new[] { 0, 1 }, series.SnapshotWins());
    }

    /// <summary>Editing a wire snapshot cannot alter the authoritative round-win record.</summary>
    [Fact]
    public void WinSnapshotsCannotMutateTheMatch()
    {
        var series = new BombSeriesState();
        Assert.True(series.TryCompleteRound(0, 1));
        series.SnapshotWins()[0] = 99;
        Assert.Equal(new[] { 1, 0 }, series.SnapshotWins());
        Assert.Null(series.MatchWinner);
    }
}
