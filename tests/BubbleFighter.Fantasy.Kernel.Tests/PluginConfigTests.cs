using System.Collections.Generic;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Tests for <see cref="PluginConfig"/> binding of a row's YAML mapping onto a POCO.</summary>
public sealed class PluginConfigTests
{
    private sealed class ReadOnlySequenceOptions
    {
        public IReadOnlyList<int> ModelItemIds { get; set; } = [];

        public int Plain { get; set; }
    }

    private static PluginConfig Config(params (string Key, object Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            map[key] = value;
        return new PluginConfig(new PluginRow
        {
            Id = "test",
            Plugin = "test.readonly",
            Source = "test",
            Config = map,
        });
    }

    [Fact]
    public void AReadOnlySequencePropertyBindsFromAYamlSequence()
    {
        // Options POCOs expose their sequences as IReadOnlyList<T> so callers
        // cannot mutate them. A sequence that bound only to List<T> and T[] left
        // those rows unbindable with a "cannot bind a value of type 'List`1'"
        // error, which is how the InGame model_item_ids row was found broken.
        var options = Config(("model_item_ids", new List<object> { 301001, 301002 }), ("plain", 7))
            .Bind<ReadOnlySequenceOptions>();

        Assert.Equal([301001, 301002], options.ModelItemIds);
        Assert.Equal(7, options.Plain);
    }

    [Fact]
    public void AnEmptySequenceBindsToAnEmptyReadOnlyList()
    {
        var options = Config(("model_item_ids", new List<object>())).Bind<ReadOnlySequenceOptions>();

        Assert.Empty(options.ModelItemIds);
    }

    [Fact]
    public void AnUnknownKeyStillFailsLoud()
    {
        var exception = Assert.Throws<ConfigException>(
            () => Config(("no_such_key", 1)).Bind<ReadOnlySequenceOptions>());

        Assert.Contains("unknown key 'no_such_key'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScalarWhereASequenceIsExpectedFailsLoud()
    {
        var exception = Assert.Throws<ConfigException>(
            () => Config(("model_item_ids", 301001)).Bind<ReadOnlySequenceOptions>());

        Assert.Contains("expected a list", exception.Message, StringComparison.Ordinal);
    }
}
