using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Pins which catalog layout each legacy build ends up with through its base in packets/builds.yaml.</summary>
public sealed class LegacyLayoutInheritanceTests
{
    [Theory]
    [InlineData("cn-717", "kr-812")]
    [InlineData("cn-735", "kr-812")]
    [InlineData("cn-404", "id-400")]
    [InlineData("cn-406", "id-400")]
    [InlineData("cn-706", "id-1809")]
    public void BuildsShareTheirBasesLayout(string build, string sameAs) =>
        Assert.Equal(LegacyLayouts.Of(sameAs), LegacyLayouts.Of(build));

    [Fact]
    public void TheBaselineAndTheCn279DifferencesAreStated()
    {
        Assert.Equal(new EnterShopReplyLayout(true, false, LegacyCategoryTree.Ordered, false, LegacyCatalogTrailer.IdsMain),
            LegacyLayouts.Of("kr-812"));
        Assert.Equal(new EnterShopReplyLayout(false, true, LegacyCategoryTree.Landing, true, LegacyCatalogTrailer.None),
            LegacyLayouts.Of("cn-279"));
    }
}
