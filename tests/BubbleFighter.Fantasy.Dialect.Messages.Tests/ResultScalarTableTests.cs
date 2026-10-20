using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins result gates and byte totals while preserving the existing outcome policy.</summary>
public sealed class ResultScalarTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void EveryNativeEndGateRetainsTwoWordsAndTheExistingOutcome(string build)
    {
        var shape = TestBuilds.Of(build).Play.Score!;
        Assert.False(shape.OutcomeFirst);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 3, 0, 0, 0 },
            PlayRoomCodec.WriteGameEnd(TestBuilds.Packets, build, RoundOutcome.Won));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 2, 0, 0, 0 },
            PlayRoomCodec.WriteGameEnd(TestBuilds.Packets, build, RoundOutcome.Lost));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 },
            PlayRoomCodec.WriteGameEnd(TestBuilds.Packets, build, RoundOutcome.Draw));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void NativeByteTotalsKeepSaturationAndAbsentTeamBehavior(string build)
    {
        Assert.Equal(new byte[] { 11, 1 },
            PlayRoomCodec.WriteTimeOverTotals(TestBuilds.Packets, build, [11, 1]));
        Assert.Equal(new byte[] { 0, 255 },
            PlayRoomCodec.WriteTimeOverTotals(TestBuilds.Packets, build, [-1, 256]));
        Assert.Equal(new byte[] { 5, 0 },
            PlayRoomCodec.WriteTimeOverTotals(TestBuilds.Packets, build, [5]));
    }
}
