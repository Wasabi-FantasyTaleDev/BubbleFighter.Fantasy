using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.ItemEnhancement.Tests;

/// <summary>Checks actual profile interpolation and option binding without inheriting the developer's environment or credentials.</summary>
public sealed class EnhancementProfileTests
{
    /// <summary>The default KR1256 profile enables enhancement with its stated server recipe policy.</summary>
    [Fact]
    public void DefaultProfileEnablesEnhancementWithItsServerPolicy()
    {
        var options = new PluginConfig(Compose(new())).Bind<ItemEnhancementOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(5000, options.SuccessBasisPoints);
        Assert.Equal(10000, options.AdditiveSuccessBasisPoints);
        Assert.Equal(1, options.CardQuantity);
        Assert.Equal(2, options.KitQuantity);
        Assert.Equal(1, options.AdditiveQuantity);
    }

    /// <summary>An operator who clears any single recipe value with the unset marker is refused rather than given a default.</summary>
    [Theory]
    [InlineData("BFF_ENHANCEMENT_SUCCESS_BASIS_POINTS")]
    [InlineData("BFF_ENHANCEMENT_ADDITIVE_SUCCESS_BASIS_POINTS")]
    [InlineData("BFF_ENHANCEMENT_CARD_QUANTITY")]
    [InlineData("BFF_ENHANCEMENT_KIT_QUANTITY")]
    [InlineData("BFF_ENHANCEMENT_ADDITIVE_QUANTITY")]
    public void EnabledProfileRequiresEveryPolicyValue(string missing)
    {
        var variables = Policy(10000);
        variables[missing] = "-1";
        var error = Assert.Throws<ConfigException>(() => new PluginConfig(Compose(variables)).Bind<ItemEnhancementOptions>());
        Assert.Contains("requires explicit", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Explicit zero and certain success remain legitimate policies and preserve all configured material quantities.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void EnabledProfilePreservesExplicitPolicy(int success)
    {
        var options = new PluginConfig(Compose(Policy(success))).Bind<ItemEnhancementOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(success, options.SuccessBasisPoints);
        Assert.Equal(1, options.CardQuantity);
        Assert.Equal(2, options.KitQuantity);
        Assert.Equal(1, options.AdditiveQuantity);
    }

    /// <summary>Only the reserved absent-policy marker is normalized; malformed or other negative values remain errors.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("-2")]
    public void InvalidEnvironmentPolicyIsStillRejected(string value)
    {
        var variables = Policy(10000);
        variables["BFF_ENHANCEMENT_CARD_QUANTITY"] = value;
        Assert.Throws<ConfigException>(() => new PluginConfig(Compose(variables)).Bind<ItemEnhancementOptions>());
    }

    private static Dictionary<string, string> Policy(int success) => new(StringComparer.Ordinal)
    {
        ["BFF_ITEM_ENHANCEMENT_ENABLED"] = "true",
        ["BFF_ENHANCEMENT_SUCCESS_BASIS_POINTS"] = success.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["BFF_ENHANCEMENT_ADDITIVE_SUCCESS_BASIS_POINTS"] = "10000",
        ["BFF_ENHANCEMENT_CARD_QUANTITY"] = "1",
        ["BFF_ENHANCEMENT_KIT_QUANTITY"] = "2",
        ["BFF_ENHANCEMENT_ADDITIVE_QUANTITY"] = "1",
    };

    private static PluginRow Compose(Dictionary<string, string> variables)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln"))) continue;
            var composer = new ProfileComposer(Path.Combine(directory.FullName, "profiles"),
                key => variables.GetValueOrDefault(key), unsetReadsEmpty: true);
            return Assert.Single(composer.Compose("kr-1256", []), row => row.Id == "item-enhancement");
        }
        throw new InvalidOperationException("The test must run below the repository root.");
    }
}
