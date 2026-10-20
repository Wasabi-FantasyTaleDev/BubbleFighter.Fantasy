using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Localization;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class LocalizationConfigTests
{
    private static LocalizationOptions Bind(params (string Region, string Language)[] mappings) =>
        new PluginConfig(new PluginRow
        {
            Id = "localization",
            Plugin = "localization",
            Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["region_languages"] = mappings.Select(mapping => (object)new Dictionary<string, object?>
                {
                    ["region"] = mapping.Region,
                    ["language"] = mapping.Language,
                }).ToList(),
            },
        }).Bind<LocalizationOptions>();

    [Fact]
    public void RegionSequenceBindsThroughTheRealPluginConfiguration()
    {
        var options = Bind(("cn", "zh-Hans"), ("tw", "zh-Hant"), ("kr", "ko"), ("id", "id"));
        Assert.Equal(4, options.RegionLanguages.Length);
        Assert.Equal("zh-Hans", options.RegionLanguages[0].Language);
        Assert.Equal("cn", options.RegionLanguages[0].Region);
    }

    [Fact]
    public void RepeatedRegionsAreRejectedRegardlessOfCase() =>
        Assert.Throws<ConfigException>(() => Bind(("cn", "zh-Hans"), ("CN", "en")));

    [Fact]
    public void MissingLanguageIsRejected() =>
        Assert.Throws<ConfigException>(() => Bind(("cn", "")));

    [Fact]
    public void ExcessiveRegionMappingsAreRejected() =>
        Assert.Throws<ConfigException>(() => Bind(Enumerable.Range(0, 65).Select(i => ($"region{i}", "en")).ToArray()));
}
