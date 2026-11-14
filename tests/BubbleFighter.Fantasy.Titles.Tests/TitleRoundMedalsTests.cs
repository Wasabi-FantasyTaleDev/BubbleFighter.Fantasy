using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Titles.Tests;

/// <summary>Checks pure terminal medal plans against native identities and explicit deployment thresholds.</summary>
public sealed class TitleRoundMedalsTests
{
    /// <summary>Disjoint thresholds, mode admission and human participant limits select only the configured medal tier.</summary>
    [Theory]
    [InlineData(3, 2, 4, 0)]
    [InlineData(3, 2, 5, 30007)]
    [InlineData(3, 2, 10, 30008)]
    [InlineData(3, 1, 10, 0)]
    [InlineData(1, 2, 10, 0)]
    public void TerminalCountersSelectConfiguredTier(int mode, int participants, int knockdowns, int medal)
    {
        var options = Options();
        var policy = new TitleRoundMedals(options, new TitleTestCatalog().Load());
        var request = new RoundTitleMedalsPreparing("titles/game", "match-1", 7, mode, RoundOutcome.Won,
            RoundEndReason.WinCondition, new(0, 2, knockdowns, 4, 1), participants);
        var result = policy.Prepare(request);
        if (medal == 0) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.Equal(new TitleMedalCount(medal, 1), Assert.Single(result.Credits));
            Assert.Equal("round:match-1", result.GrantKey);
            Assert.Equal("round", result.Source);
            Assert.Equal(7, result.UserNum);
            Assert.Equal("titles", result.CatalogScope);
        }
    }

    /// <summary>A running policy owns copies of its modes and thresholds, so subsequent configuration edits cannot alter a frozen plan.</summary>
    [Fact]
    public void PolicyCopiesMutableConfiguration()
    {
        var options = Options();
        var policy = new TitleRoundMedals(options, new TitleTestCatalog().Load());
        options.RoundMedalRules[0].ModeIds[0] = 99;
        options.RoundMedalRules[0].Minimum = 100;
        var request = new RoundTitleMedalsPreparing("titles/game", "match-2", 7, 3, RoundOutcome.Won,
            RoundEndReason.WinCondition, new(0, 0, 5, 0, 0), 2);
        Assert.Equal(30007, Assert.Single(policy.Prepare(request)!.Credits).MedalId);
    }

    /// <summary>An unknown catalogue medal fails activation instead of recording an identity the client cannot display.</summary>
    [Fact]
    public void UnknownNativeMedalFails()
    {
        var options = Options();
        options.RoundMedalRules[0].MedalId = 999;
        Assert.Throws<ConfigException>(() => new TitleRoundMedals(options, new TitleTestCatalog().Load()));
    }

    /// <summary>Repeated medal identities and missing explicit automatic-award policy fail row validation.</summary>
    [Fact]
    public void InvalidConfiguredRulesFailValidation()
    {
        var options = Options();
        options.RoundMedalRules[1].MedalId = 30007;
        Assert.NotEmpty(options.Validate());
        options.RoundMedalRules = [];
        Assert.NotEmpty(options.Validate());
    }

    /// <summary>Rule and mode memory budgets follow row configuration and reject excess entries before activation.</summary>
    [Fact]
    public void ConfiguredResourceBudgetsBoundRulesAndModes()
    {
        var options = Options();
        options.MaxRoundMedalRules = 1;
        Assert.NotEmpty(options.Validate());
        options.MaxRoundMedalRules = 2;
        options.MaxModesPerRoundMedalRule = 1;
        options.RoundMedalRules[0].ModeIds = [1, 3];
        Assert.NotEmpty(options.Validate());
        options.MaxModesPerRoundMedalRule = 2;
        Assert.Empty(options.Validate());
    }

    private static TitlesOptions Options() => new()
    {
        Enabled = true, BuildKey = "titles", CatalogSchema = "Legacy", RoundMedalsEnabled = true,
        RoundMedalRules =
        [
            new() { MedalId = 30007, ModeIds = [3], Metric = "Knockdowns", Minimum = 5, Maximum = 9 },
            new() { MedalId = 30008, ModeIds = [3], Metric = "Knockdowns", Minimum = 10 },
        ],
    };
}
