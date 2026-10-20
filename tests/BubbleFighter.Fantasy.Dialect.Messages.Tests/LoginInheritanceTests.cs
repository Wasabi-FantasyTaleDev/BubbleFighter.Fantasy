using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins which login layout each legacy build ends up with through its base in packets/builds.yaml.</summary>
public sealed class LoginInheritanceTests
{
    [Theory]
    [InlineData("cn-404", "id-400")]
    [InlineData("cn-406", "id-400")]
    [InlineData("cn-279", "cn-324")]
    [InlineData("cn-717", "kr-812")]
    public void BuildsShareTheirBasesLayout(string build, string sameAs) =>
        Assert.Equal(TestLogin.Of(sameAs), TestLogin.Of(build));

    [Fact]
    public void TheStatedDifferencesHold()
    {
        Assert.Equal(new LoginShape(true), TestLogin.Of("kr-812"));
        Assert.Equal(new LoginShape(false), TestLogin.Of("id-400"));
        Assert.Equal(new LoginShape(false, RequestToken: true), TestLogin.Of("id-1809"));
        Assert.Equal(new LoginShape(false, RequestToken: true), TestLogin.Of("cn-706"));
        Assert.Equal(new LoginShape(true, LoginResponse: true), TestLogin.OfLineage(["cn-742", "cn-735", "cn-717"]));
    }
}
