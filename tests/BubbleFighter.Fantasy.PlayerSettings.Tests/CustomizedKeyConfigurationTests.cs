using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.PlayerSettings.Tests;

/// <summary>Checks that nested storage budgets fail at configuration binding before any repository is constructed.</summary>
public sealed class CustomizedKeyConfigurationTests
{
    [Theory]
    [InlineData(128, 128, true)]
    [InlineData(248, 128, true)]
    [InlineData(249, 128, false)]
    [InlineData(31, 1024, true)]
    [InlineData(32, 1024, false)]
    public void CombinedPacketBudgetIsValidatedWhileBindingNestedOptions(int mappings, int nameCharacters, bool valid)
    {
        var config = Config(new Dictionary<string, object?>
        {
            ["max_mappings"] = mappings,
            ["max_name_characters"] = nameCharacters,
        });
        if (valid)
        {
            var options = config.Bind<MemoryPersistenceOptions>();
            Assert.Equal(mappings, options.CustomizedKeys.MaxMappings);
            Assert.Equal(nameCharacters, options.CustomizedKeys.MaxNameCharacters);
        }
        else
        {
            var error = Assert.Throws<ConfigException>(() => config.Bind<MemoryPersistenceOptions>());
            Assert.Contains("settings-config", error.Message, StringComparison.Ordinal);
            Assert.Contains("customized_keys", error.Message, StringComparison.Ordinal);
            Assert.Contains("65,535", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExplicitNullLimitsFailBindingInsteadOfReachingRepositoryConstruction()
    {
        var error = Assert.Throws<ConfigException>(() => Config(null).Bind<MemoryPersistenceOptions>());
        Assert.Contains("settings-config", error.Message, StringComparison.Ordinal);
        Assert.Contains("CustomizedKeys", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("max_scopes", 0)]
    [InlineData("max_scopes", 65)]
    [InlineData("max_mappings", 0)]
    [InlineData("max_mappings", 513)]
    [InlineData("max_name_characters", 0)]
    [InlineData("max_name_characters", 1025)]
    public void IndividualLimitsAreValidatedWhileBindingNestedOptions(string field, int value)
    {
        var config = Config(new Dictionary<string, object?> { [field] = value });
        var error = Assert.Throws<ConfigException>(() => config.Bind<MemoryPersistenceOptions>());
        Assert.Contains("settings-config", error.Message, StringComparison.Ordinal);
        Assert.Contains("customized_keys", error.Message, StringComparison.Ordinal);
    }

    private static PluginConfig Config(object? limits) => new(new PluginRow
    {
        Id = "settings-config", Plugin = "persistence.memory", Source = "settings-test",
        Config = new Dictionary<string, object?> { ["customized_keys"] = limits },
    });
}
