using Xunit;

namespace BubbleFighter.Fantasy.WebUi.Tests;

public sealed class BuildReleaseLabelsTests
{
    [Fact]
    public void ConfiguredBuildsResolveAndOthersHaveNoRelease()
    {
        var labels = new BuildReleaseLabels([new BuildReleaseOptions { Build = "cn-735", Release = "CN2016 0.2.70.4" }]);
        Assert.True(labels.TryGetRelease("cn-735", out var release));
        Assert.Equal("CN2016 0.2.70.4", release);
        Assert.False(labels.TryGetRelease("cn-738", out var none));
        Assert.Equal("", none);
    }

    [Fact]
    public void ValidationRejectsDuplicatesAndEmptyNames()
    {
        var options = new WebUiOptions
        {
            Releases =
            [
                new BuildReleaseOptions { Build = "kr-1256", Release = "KR final" },
                new BuildReleaseOptions { Build = "kr-1256", Release = "KR again" },
                new BuildReleaseOptions { Build = "cn-735", Release = " " },
                new BuildReleaseOptions { Build = "", Release = "x" },
            ],
        };
        var messages = options.Validate().Select(r => r.ErrorMessage).ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Contains(messages, m => m!.Contains("listed twice"));
        Assert.Contains(messages, m => m!.Contains("must not be empty"));
        Assert.Contains(messages, m => m!.Contains("must name a client build id"));
    }

    [Fact]
    public void AnEmptyConfigurationIsValid()
    {
        Assert.Empty(new WebUiOptions().Validate());
    }
}
