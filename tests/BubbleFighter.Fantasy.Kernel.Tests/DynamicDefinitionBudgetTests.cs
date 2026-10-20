using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies immutable startup grants for dynamic definition sources and catalog identities.</summary>
public sealed class DynamicDefinitionBudgetTests
{
    /// <summary>Existing startup definitions consume catalog capacity without implicitly granting dynamic source slots.</summary>
    [Fact]
    public void DefaultsCaptureOnlyBootstrapDefinitions()
    {
        var budget = PluginRuntimeBudget.Capture([Row("entry")], 7);
        Assert.Equal(7, budget.DefinitionCapacity);
        Assert.Equal(0, budget.DefinitionSourceCapacity);
        Assert.Equal(0, PluginRuntimeBudget.Capture([]).DefinitionCapacity);
    }

    /// <summary>Removing donors neither shrinks the fixed grant nor allows newly inserted rows to mint capacity.</summary>
    [Fact]
    public void RemovedDonorRetainsFixedBudgetsAndNewRowsCannotGrantCapacity()
    {
        var donor = Row("donor", 3, 8);
        var budget = PluginRuntimeBudget.Capture([donor], 2);
        budget.Validate([], [], []);
        Assert.Equal(3, budget.DefinitionSourceCapacity);
        Assert.Equal(10, budget.DefinitionCapacity);
        budget.Validate([Row("replacement")], [], []);
        Assert.Throws<ConfigException>(() => budget.Validate([Row("replacement", 1)], [], []));
        Assert.Throws<ConfigException>(() => budget.Validate([Row("replacement", definitions: 1)], [], []));
    }

    /// <summary>Reintroduced startup identities must retain both definition grants rather than claiming a new allocation.</summary>
    [Fact]
    public void ReintroducedDonorMustMatchItsOriginalGrantPair()
    {
        var donor = Row("donor", 2, 4);
        var budget = PluginRuntimeBudget.Capture([donor]);
        budget.Validate([], [], []);
        budget.Validate([donor], [], []);
        Assert.Throws<ConfigException>(() => budget.Validate([Row("donor", 3, 4)], [], []));
        Assert.Throws<ConfigException>(() => budget.Validate([Row("donor", 2, 3)], [], []));
        Assert.Throws<ConfigException>(() => budget.Validate([Row("donor")], [], []));
    }

    /// <summary>Both reserve fields reject negative or individually excessive grants before capacity capture.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1000001, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1000001)]
    public void InvalidIndividualGrantsAreRejected(int sources, int definitions) =>
        Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([Row("entry", sources, definitions)]));

    /// <summary>Aggregate catalog overflow and a negative bootstrap count fail with configuration diagnostics.</summary>
    [Fact]
    public void InvalidAggregateCatalogCapacityIsRejected()
    {
        Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([], -1));
        Assert.Throws<ConfigException>(() => PluginRuntimeBudget.Capture([Row("entry", definitions: 1)], int.MaxValue));
        Assert.Equal(int.MaxValue, PluginRuntimeBudget.Capture([], int.MaxValue).DefinitionCapacity);
    }

    private static PluginRow Row(string id, int sources = 0, int definitions = 0) => new()
    {
        Id = id, Plugin = "budget-test", Source = "test", Lifecycle = new()
        {
            EffectCapacity = 1, DefinitionSourceReserve = sources, DefinitionReserve = definitions,
        },
    };
}
