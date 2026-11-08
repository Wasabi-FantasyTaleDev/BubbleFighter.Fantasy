using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the zombie banner's shared cause word independently of each recipient's reward outcome.</summary>
public sealed class ZombieResultCauseTests
{
    /// <summary>CN2012 selects escape with two and defeat with three for both allegiances.</summary>
    [Theory]
    [InlineData(RoundEndReason.TimeLimit, 1, RoundOutcome.Won, 2)]
    [InlineData(RoundEndReason.TimeLimit, 14, RoundOutcome.Lost, 2)]
    [InlineData(RoundEndReason.WinCondition, 1, RoundOutcome.Won, 3)]
    [InlineData(RoundEndReason.WinCondition, 14, RoundOutcome.Lost, 3)]
    [InlineData(RoundEndReason.WinCondition, 1, RoundOutcome.Lost, 1)]
    [InlineData(RoundEndReason.WinCondition, 14, RoundOutcome.Won, 1)]
    public void NativeBannerUsesCauseRatherThanRecipientWinLoss(RoundEndReason reason, int team, RoundOutcome outcome, int word)
    {
        var body = RoundResultWire.WriteGameEndForRecipient(TestBuilds.Packets, "cn-324",
            PlayValues.Of(TestBuilds.Packets, "cn-324").Score,
            new RoundEnded(1, outcome, reason) { ModeId = 8, RecipientTeam = team });
        Assert.Equal(8, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
        Assert.Equal(word, BitConverter.ToInt32(body, 4));
    }

    /// <summary>Unaudited builds retain their own existing outcome placement even for zombie mode.</summary>
    [Theory]
    [InlineData("cn-279", RoundOutcome.Won)]
    [InlineData("cn-279", RoundOutcome.Lost)]
    [InlineData("cn-406", RoundOutcome.Won)]
    [InlineData("cn-406", RoundOutcome.Lost)]
    [InlineData("kr-812", RoundOutcome.Won)]
    [InlineData("kr-812", RoundOutcome.Lost)]
    public void OtherBuildsDoNotInheritTheCauseOverride(string build, RoundOutcome outcome)
    {
        var shape = PlayValues.Of(TestBuilds.Packets, build).Score;
        Assert.False(shape.ZombieResultCause);
        var body = RoundResultWire.WriteGameEndForRecipient(TestBuilds.Packets, build, shape,
            new RoundEnded(1, outcome, RoundEndReason.TimeLimit) { ModeId = 8, RecipientTeam = 1 });
        Assert.Equal(8, body.Length);
        Assert.Equal(RoundResultWire.WriteGameEnd(TestBuilds.Packets, build, outcome, shape.OutcomeFirst), body);
    }

    /// <summary>The cause projection rejects an allegiance that cannot identify the winning side.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ZombieCauseRejectsAnUnknownAllegiance(int team)
    {
        Assert.Throws<InvalidOperationException>(() => RoundResultWire.WriteGameEndForRecipient(
            TestBuilds.Packets, "cn-324", PlayValues.Of(TestBuilds.Packets, "cn-324").Score,
            new RoundEnded(1, RoundOutcome.Won, RoundEndReason.WinCondition) { ModeId = 8, RecipientTeam = team }));
    }

    /// <summary>A mode without zombie presentation keeps its per-recipient result value.</summary>
    [Theory]
    [InlineData(RoundOutcome.Won, 3)]
    [InlineData(RoundOutcome.Lost, 2)]
    [InlineData(RoundOutcome.Draw, 1)]
    public void OrdinaryResultsAreUnchanged(RoundOutcome outcome, int word)
    {
        var body = RoundResultWire.WriteGameEndForRecipient(TestBuilds.Packets, "cn-324",
            PlayValues.Of(TestBuilds.Packets, "cn-324").Score,
            new RoundEnded(1, outcome, RoundEndReason.TimeLimit) { ModeId = 1, RecipientTeam = 1 });
        Assert.Equal(8, body.Length);
        Assert.Equal(word, BitConverter.ToInt32(body, 4));
    }
}
