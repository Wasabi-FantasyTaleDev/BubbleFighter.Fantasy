using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Fighters.Tests;

public sealed class CharacterOptionsTests
{
    /// <summary>
    /// Keys the row carried before creation moved to the shared adapter are gone; a patch that still sets
    /// one must fail loudly, naming the key, instead of being ignored (the binder rejects unknown keys).
    /// </summary>
    [Theory]
    [InlineData("fighter_config_xml")]
    [InlineData("protocol")]
    [InlineData("client_profile")]
    [InlineData("version_major")]
    [InlineData("max_tracked_sessions")]
    public void RemovedOptionsFailToBind(string key)
    {
        var row = new PluginRow
        {
            Id = "fighters",
            Plugin = "fighters",
            Source = "test",
            Config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fighter_config_client_path"] = "Config/FighterConfig.xml",
                [key] = "old",
            },
        };
        var error = Assert.Throws<ConfigException>(() => new PluginConfig(row).Bind<FightersOptions>());
        Assert.Contains($"unknown key '{key}'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The catalog path and its size cap are the row's only tuning after the creation move.</summary>
    [Fact]
    public void TheCatalogPathAndCapAreTheOnlyDefaults()
    {
        var options = new FightersOptions();
        Assert.Equal("Config/FighterConfig.xml", options.FighterConfigClientPath);
        Assert.Equal(134_217_728, options.MaxFighterConfigBytes);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CatalogPathIsRequired(string path)
    {
        var options = new FightersOptions { FighterConfigClientPath = path };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(FightersOptions.FighterConfigClientPath)));
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200_000_001)]
    public void CatalogSizeCapRejectsInvalidBounds(long limit)
    {
        var options = new FightersOptions { MaxFighterConfigBytes = limit };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(FightersOptions.MaxFighterConfigBytes)));
    }
}
