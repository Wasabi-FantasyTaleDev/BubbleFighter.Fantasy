using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins CN2011's native experience-bar values and occupied-row display gates.</summary>
public sealed class Cn279ResultProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewardedRowSuppliesTheNativeProgressBarAndLevelAnimation(bool leveledUp)
    {
        var reward = new RoundRewardRow(12, 100, 123, 200, 21, 43, leveledUp, []);
        var body = Write(reward);
        Assert.Equal(153, body.Length);
        // cn-279 sub_448C80: the two-unit nickname shifts scalar wire offsets by four bytes.
        Assert.Equal(100, BitConverter.ToInt32(body, 96));
        Assert.Equal(123, BitConverter.ToInt32(body, 100));
        Assert.Equal(200, BitConverter.ToInt32(body, 104));
        Assert.Equal(1, BitConverter.ToInt32(body, 116));
        Assert.Equal(leveledUp ? 1 : 0, BitConverter.ToInt32(body, 120));
        // Earned EXP/Lucci still require their own column evidence; don't borrow later-build meanings.
        Assert.Equal(new byte[8], body[108..116]);
    }

    [Fact]
    public void InitialOccupiedRowShowsZeroCountersWithoutRewardOrLevelAnimation()
    {
        var body = Write(null);
        Assert.Equal(153, body.Length);
        Assert.Equal(new byte[20], body[96..116]);
        Assert.Equal(1, BitConverter.ToInt32(body, 116));
        Assert.Equal(new byte[4], body[120..124]);
    }

    [Fact]
    public void VacantRowDoesNotExposeStatsOrProgress()
    {
        var body = PlayRoomCodec.WriteScoreRowBody(TestBuilds.Packets, "cn-279",
            TestBuilds.Packets.Resolve("cn-279", "ScoreRow"), null);
        Assert.Equal(149, body.Length);
        Assert.Equal(-1, BitConverter.ToInt32(body));
        Assert.Equal(new byte[28], body[92..120]);
    }

    private static byte[] Write(RoundRewardRow? reward) => PlayRoomCodec.WriteScoreRowBody(
        TestBuilds.Packets, "cn-279", TestBuilds.Packets.Resolve("cn-279", "ScoreRow"),
        new RoundScoreboardRow(0, "AB", 2, 12, new RoundSeatStats(0, 0, 0, 0, 0), reward));
}
