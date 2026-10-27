using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the four-byte native cooperative stage result without assigning meaning to other values.</summary>
public sealed class StageEndTests
{
    /// <summary>Separates the horde manager's objective flag from ordinary and zombie Boss result gates.</summary>
    [Theory]
    [InlineData(101, RoundOutcome.Won, "gspHordeStageCleared_")]
    [InlineData(101, RoundOutcome.Lost, "gspHordeStageFailed_")]
    [InlineData(102, RoundOutcome.Won, "gspHordeStageCleared_")]
    [InlineData(103, RoundOutcome.Lost, "gspHordeStageFailed_")]
    [InlineData(16, RoundOutcome.Won, null)]
    [InlineData(0, RoundOutcome.Won, null)]
    public void HordeResultsSelectTheirNativeManagerNotification(int mode, RoundOutcome outcome, string? name)
    {
        var message = new RoundEnded(1, outcome, RoundEndReason.WavesCleared) { ModeId = mode };
        Assert.Equal(name, PlayRoomCodec.HordeOutcomeNotification(new PlayValues.ScoreValues(HordeStageOutcome: true), message));
        Assert.Null(PlayRoomCodec.HordeOutcomeNotification(new PlayValues.ScoreValues(), message));
    }

    /// <summary>Preserves recognized result flags and rejects bodies that cannot hold the native word.</summary>
    [Fact]
    public void StageResultRetainsSuccessFailureAndUnknownValues()
    {
        Assert.False(PlayRoomCodec.ReadStageEnd(new byte[4]).StageSucceeded);
        Assert.True(PlayRoomCodec.ReadStageEnd(BitConverter.GetBytes(1u)).StageSucceeded);
        Assert.Null(PlayRoomCodec.ReadStageEnd(BitConverter.GetBytes(2u)).StageSucceeded);
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadStageEnd(new byte[3]));
        Assert.Throws<PacketFormatException>(() => PlayRoomCodec.ReadStageEnd(new byte[8]));
    }
}
