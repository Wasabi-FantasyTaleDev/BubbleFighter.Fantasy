using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public class RoundRewardTests
{
    private static readonly LevelTable Levels = new([0, 35, 115, 226, 360]);

    [Theory]
    [InlineData(13, 5, 2, 27)]
    [InlineData(9, 7, 3, 29)]
    [InlineData(8, 8, 16, 56)]
    public void PersonalScoreIncludesTwoPointsPerCompletedRescue(int knockdowns, int kills, int rescues, int score)
    {
        var scoring = new RoundScoring(2);
        for (var i = 0; i < knockdowns; i++) scoring.AddKnockdown(0);
        for (var i = 0; i < kills; i++) scoring.AddKill(0, 1);
        for (var i = 0; i < rescues; i++) scoring.AddRescue(0);
        Assert.Equal(score, scoring.Stats(0).Score);
        Assert.Equal(rescues, scoring.Stats(0).Rescues);
        Assert.Equal(kills, scoring.TeamTotals[0]);
    }

    [Fact]
    public void IndependentRewardFactorsChangeOnlyTheirOwnCurrencyAndPreserveStoredExperience()
    {
        var levels = new LevelTable([0, 100, 500], 3);
        var options = new InGameOptions { ScoreBasedRewards = true, RewardExpMultiplier = 100, RewardLucciMultiplier = 100 };
        var reward = RoundReward.ComputeConfigured(levels, 123, true, 5, 27, options);
        Assert.Equal(2100, reward.ExpEarned);
        Assert.Equal(4200, reward.LucciEarned);
        Assert.Equal(2223, reward.Exp);
        options.RewardLucciMultiplier = 1;
        var coinsOnly = RoundReward.ComputeConfigured(levels, 123, true, 5, 27, options);
        Assert.Equal(reward.ExpEarned, coinsOnly.ExpEarned);
        Assert.Equal(42, coinsOnly.LucciEarned);
        var noContribution = RoundReward.ComputeConfigured(levels, 123, false, 0, 0, options);
        Assert.Equal(1800, noContribution.ExpEarned);
        Assert.Equal(28, noContribution.LucciEarned);
    }

    [Fact]
    public void FullSharedExperienceTableIncludesEveryLevelAndCapsAwardedLevels()
    {
        var options = new InGameOptions { LevelExpTablePath = Path.Combine(AppContext.BaseDirectory, "data", "level-experience.csv") };
        LevelTable.LoadConfiguredFile(options);
        Assert.Equal(240, options.LevelExpThresholds.Count);
        Assert.Equal(240, options.MaxLevel);
        var table = new LevelTable(options.LevelExpThresholds, options.MaxLevel);
        Assert.Equal(5259518, table.Start(240));
        for (var level = 1; level <= 240; level++)
        {
            Assert.Equal(level, table.LevelFor(table.Start(level)));
            if (level > 1) Assert.Equal(level - 1, table.LevelFor(table.Start(level) - 1));
        }
        Assert.Equal(240, table.LevelFor(long.MaxValue));
        var earlierBuild = new LevelTable(options.LevelExpThresholds, 120);
        Assert.Equal(120, earlierBuild.LevelFor(table.Start(240)));
        Assert.Equal(table.Start(121), earlierBuild.Start(121));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LevelTable(options.LevelExpThresholds, 241));
    }

    [Theory]
    [InlineData("level,total_exp\n1,0\n3,35\n")]
    [InlineData("level,total_exp\n1,0\n2,0\n")]
    [InlineData("level,total_exp\n1,0\n2,-35\n")]
    [InlineData("level,exp\n1,0\n2,35\n")]
    public void MalformedExperienceFilesCannotLoad(string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            Assert.ThrowsAny<Exception>(() => LevelTable.LoadConfiguredFile(new() { LevelExpTablePath = path }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LevelsFollowTheTableAndRepeatTheLastStepPastIt()
    {
        Assert.Equal(1, Levels.LevelFor(0));
        Assert.Equal(1, Levels.LevelFor(34));
        Assert.Equal(2, Levels.LevelFor(35));
        Assert.Equal(4, Levels.LevelFor(359));
        Assert.Equal(5, Levels.LevelFor(360));
        Assert.Equal(5, Levels.LevelFor(493));
        Assert.Equal(6, Levels.LevelFor(494));
        Assert.Equal(494, Levels.Start(6));
        Assert.Equal(226, Levels.Start(4));
    }

    [Fact]
    public void ARewardFillsTheResultRowFigures()
    {
        // A level-4 player at 226 exp who wins: (150 + 0) x 2 = 300 exp, 35 % of it in Lucci.
        var reward = RoundReward.Compute(Levels, 226, won: true, kills: 0, baseExp: 150, expPerKill: 10, winMultiplier: 2, lucciPercent: 35);
        Assert.Equal(new RoundReward(6, 494, 526, 628, 300, 105), reward);
        var loser = RoundReward.Compute(Levels, 0, won: false, kills: 3, baseExp: 150, expPerKill: 10, winMultiplier: 2, lucciPercent: 35);
        Assert.Equal(new RoundReward(3, 115, 180, 226, 180, 63), loser);
    }

    [Fact]
    public void ATableMustStartAtZeroAndIncrease()
    {
        Assert.Throws<ArgumentException>(() => new LevelTable([5, 10]));
        Assert.Throws<ArgumentException>(() => new LevelTable([0, 10, 10]));
        Assert.Throws<ArgumentException>(() => new LevelTable([0]));
        Assert.NotEmpty(new InGameOptions { LevelExpThresholds = [0, 10, 5] }.Validate());
    }
}
