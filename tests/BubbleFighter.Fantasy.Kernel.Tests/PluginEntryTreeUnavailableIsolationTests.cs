using System.Reflection;
using System.Reflection.Emit;
using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks pending isolation metadata when validated contracts leave and later return to the catalog.</summary>
public sealed class PluginEntryTreeUnavailableIsolationTests
{
    /// <summary>Withdrawal mode retains validated labels and propagates unavailability without retaining removed contract types.</summary>
    [Fact]
    public void WithdrawalRetainsValidatedNamesAndMarksDescendantsUnavailable()
    {
        var runtime = new ServiceRuntime(16);
        PluginRow[] rows = [Row("parent", typeof(IFirst)), Row("child", parent: "parent"), Row("stable", typeof(ISecond))];
        var original = PluginEntryTree.Build(rows, runtime, Catalog(typeof(IFirst), typeof(ISecond)));
        Assert.Throws<ConfigException>(() => PluginEntryTree.Build(rows, runtime, Catalog(typeof(ISecond)), original));

        var pending = PluginEntryTree.Build(rows, runtime, Catalog(typeof(ISecond)), original, allowUnavailableIsolation: true);

        Assert.False(pending.Entries["parent"].IsNamespaceReady);
        Assert.False(pending.Entries["child"].IsNamespaceReady);
        Assert.Equal([typeof(IFirst).FullName!], pending.Entries["parent"].UnavailableIsolatedServices);
        Assert.Equal(pending.Entries["parent"].UnavailableIsolatedServices, pending.Entries["child"].UnavailableIsolatedServices);
        Assert.Equal([typeof(IFirst).FullName!], pending.Entries["parent"].IsolatedServices);
        Assert.Empty(pending.Entries["parent"].IsolatedContracts);
        Assert.Empty(pending.Entries["child"].IsolatedContracts);
        Assert.Same(original.Entries["parent"].Identity, pending.Entries["parent"].Identity);
        Assert.NotSame(original.Entries["parent"].Scope, pending.Entries["parent"].Scope);
        Assert.NotSame(original.Entries["child"].Scope, pending.Entries["child"].Scope);
        Assert.Same(original.Entries["stable"], pending.Entries["stable"]);
        Assert.True(pending.Entries["stable"].IsNamespaceReady);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)pending.Entries["parent"].UnavailableIsolatedServices).Clear());
    }

    /// <summary>Ordinary reconciliation preserves an already pending label and does not allocate replacement scopes for a no-op.</summary>
    [Fact]
    public void OrdinaryReconciliationCanRetainExistingUnavailableLabel()
    {
        var runtime = new ServiceRuntime(16);
        PluginRow[] rows = [Row("entry", typeof(IFirst))];
        var original = PluginEntryTree.Build(rows, runtime, Catalog(typeof(IFirst)));
        var pending = PluginEntryTree.Build(rows, runtime, Catalog(), original, allowUnavailableIsolation: true);

        var unchanged = PluginEntryTree.Build(rows, runtime, Catalog(), pending);

        Assert.Same(pending.Entries["entry"], unchanged.Entries["entry"]);
        Assert.False(unchanged.Entries["entry"].IsNamespaceReady);
        var changed = rows[0] with { IsolatedServices = [typeof(IFirst).FullName!, typeof(ISecond).FullName!] };
        Assert.Throws<ConfigException>(() => PluginEntryTree.Build([changed], runtime, Catalog(), pending));
    }

    /// <summary>A previously validated label cannot authorize a different entry or a newly invented label.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithdrawalModeNeverInventsUnavailableDeclarations(bool differentEntry)
    {
        var runtime = new ServiceRuntime(16);
        var original = PluginEntryTree.Build([Row("entry", typeof(IFirst))], runtime, Catalog(typeof(IFirst)));
        var row = differentEntry ? Row("new-entry", typeof(IFirst)) : Row("entry", typeof(ISecond));

        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build([row], runtime, Catalog(), original, allowUnavailableIsolation: true));

        Assert.Contains("unknown closed service interface", error.Message);
    }

    /// <summary>Unavailable inherited names still consume flattening capacity before a new realm is allocated.</summary>
    [Fact]
    public void UnavailableNamesCannotMintIsolationCapacity()
    {
        var runtime = new ServiceRuntime(1);
        PluginRow[] rows = [Row("parent", typeof(IFirst)), Row("child", parent: "parent")];
        var original = PluginEntryTree.Build(rows, runtime, Catalog(typeof(IFirst)));
        var pending = PluginEntryTree.Build(rows, runtime, Catalog(), original, allowUnavailableIsolation: true);
        var before = runtime.NewActivation("before").Generation;

        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(
            [rows[0], Row("child", typeof(ISecond), "parent")], runtime, Catalog(typeof(ISecond)), pending));

        Assert.Contains("inherited service isolation metadata", error.Message);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
    }

    /// <summary>A returning same-name contract uses its actual new type and rebuilds the pending branch with stable entry identities.</summary>
    [Fact]
    public void RestorationUsesNewActualTypeAndReactivatableNamespaces()
    {
        var oldType = EmitContract();
        var newType = EmitContract();
        var runtime = new ServiceRuntime(16);
        PluginRow[] rows = [Row("parent", oldType), Row("child", parent: "parent")];
        var original = PluginEntryTree.Build(rows, runtime, Catalog(oldType));
        var pending = PluginEntryTree.Build(rows, runtime, Catalog(), original, allowUnavailableIsolation: true);

        var restored = PluginEntryTree.Build(rows, runtime, Catalog(newType), pending);

        Assert.True(restored.Entries["parent"].IsNamespaceReady);
        Assert.True(restored.Entries["child"].IsNamespaceReady);
        Assert.Empty(restored.Entries["parent"].UnavailableIsolatedServices);
        Assert.Empty(restored.Entries["child"].UnavailableIsolatedServices);
        Assert.Same(newType, Assert.Single(restored.Entries["parent"].IsolatedContracts));
        Assert.Same(original.Entries["parent"].Identity, restored.Entries["parent"].Identity);
        Assert.Same(original.Entries["child"].Identity, restored.Entries["child"].Identity);
        Assert.NotSame(pending.Entries["parent"].Scope, restored.Entries["parent"].Scope);
        Assert.NotSame(pending.Entries["child"].Scope, restored.Entries["child"].Scope);
        Assert.NotEqual(runtime.RootScope.Resolve(newType), restored.Entries["child"].Scope.Resolve(newType));
    }

    /// <summary>A pending name never hides ambiguity when distinct same-name contract types appear.</summary>
    [Fact]
    public void AmbiguousRestorationStillFailsInWithdrawalMode()
    {
        var first = EmitContract();
        var second = EmitContract();
        var runtime = new ServiceRuntime(16);
        PluginRow[] rows = [Row("entry", first)];
        var original = PluginEntryTree.Build(rows, runtime, Catalog(first));
        var pending = PluginEntryTree.Build(rows, runtime, Catalog(), original, allowUnavailableIsolation: true);

        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(rows, runtime, Catalog(first, second), pending, allowUnavailableIsolation: true));

        Assert.Contains("ambiguous", error.Message);
    }

    private static PluginRow Row(string id, Type? isolated = null, string? parent = null) => new()
    {
        Id = id, Plugin = "tree-unavailable", Source = "unavailable-tree-test", ParentId = parent,
        IsolatedServices = isolated is null ? [] : [isolated.FullName!],
    };

    private static PluginCatalog Catalog(params Type[] contracts) => PluginCatalog.FromDefinitions([new PluginDescriptor(
        new PluginAttribute("tree-unavailable") { Provides = contracts }, new Factory(), "unavailable-tree-test", "1.0.0",
        new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() })]);

    private static Type EmitContract() => AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName($"UnavailableTree-{Guid.NewGuid():N}"), AssemblyBuilderAccess.RunAndCollect)
        .DefineDynamicModule("contract").DefineType("UnavailableTree.IContract",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;

    private sealed class Factory : IPluginExecutionFactory
    {
        /// <summary>Leaves structural namespace metadata independent of execution coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Rejects accidental backend execution while the entry tree is being inspected.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Structural isolation tests must not acquire an execution.");
    }

    /// <summary>Identifies the first independently isolated namespace used by withdrawal tests.</summary>
    public interface IFirst { }
    /// <summary>Identifies an unrelated namespace that remains available during withdrawal.</summary>
    public interface ISecond { }
}
