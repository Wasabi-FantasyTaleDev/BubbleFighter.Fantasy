using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies entry parent validation, realm isolation and stable scope reuse.</summary>
public sealed class PluginEntryTreeTests
{
    private interface IFirst { }
    private interface ISecond { }

    /// <summary>Invalid parent graphs fail before even an earlier valid row allocates a realm.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("cycle")]
    public void InvalidParentsDoNotAllocateRealms(string failure)
    {
        PluginRow[] invalid = failure switch
        {
            "missing" => [Row("invalid", "absent")],
            "self" => [Row("invalid", "invalid")],
            _ => [Row("invalid", "second"), Row("second", "invalid")],
        };
        var runtime = new ServiceRuntime(16);
        var before = runtime.NewActivation("before").Generation;
        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(
            [Row("valid", isolate: [typeof(IFirst)]), .. invalid], runtime, Catalog(typeof(IFirst))));

        Assert.Contains("parent_id", error.Message);
        Assert.Contains("invalid", error.Message);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
    }

    /// <summary>Entry identities and isolation declarations cannot contain duplicates.</summary>
    [Fact]
    public void DuplicateIdsAndIsolationNamesAreRejected()
    {
        var runtime = new ServiceRuntime(16);
        var catalog = Catalog(typeof(IFirst));
        Assert.Contains("id", Assert.Throws<ConfigException>(() =>
            PluginEntryTree.Build([Row("same"), Row("same")], runtime, catalog)).Message);
        Assert.Contains("isolated_services[1]", Assert.Throws<ConfigException>(() =>
            PluginEntryTree.Build([Row("duplicate", isolate: [typeof(IFirst), typeof(IFirst)])], runtime, catalog)).Message);
    }

    /// <summary>Unknown service names fail during validation, before preceding scopes are allocated.</summary>
    [Fact]
    public void UnknownIsolationDoesNotAllocateRealms()
    {
        var runtime = new ServiceRuntime(16);
        var before = runtime.NewActivation("before").Generation;
        var unknown = Row("unknown") with { IsolatedServices = ["Unknown.Contract"] };
        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(
            [Row("valid", isolate: [typeof(IFirst)]), unknown], runtime, Catalog(typeof(IFirst))));

        Assert.Contains("unknown", error.Message);
        Assert.Contains("isolated_services[0]", error.Message);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
    }

    /// <summary>Two distinct interface identities with the same full name cannot select a realm implicitly.</summary>
    [Fact]
    public void AmbiguousInterfaceNamesAreRejected()
    {
        using var first = EmitContract();
        using var second = EmitContract();
        var runtime = new ServiceRuntime(16);
        var before = runtime.NewActivation("before").Generation;
        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(
            [Row("valid", isolate: [typeof(IFirst)]), Row("ambiguous", isolate: [first.Contract])],
            runtime, Catalog(typeof(IFirst), first.Contract, second.Contract)));

        Assert.Contains("ambiguous", error.Message);
        Assert.Contains("isolated_services[0]", error.Message);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
    }

    /// <summary>Sibling isolation creates distinct keys while descendants inherit every nonisolated contract.</summary>
    [Fact]
    public void SiblingsHaveSeparateRealmsAndChildrenInheritOtherServices()
    {
        var runtime = new ServiceRuntime(16);
        var tree = PluginEntryTree.Build(
            [Row("child", "left", [typeof(ISecond)]), Row("right", isolate: [typeof(IFirst)]), Row("left", isolate: [typeof(IFirst)])],
            runtime, Catalog(typeof(IFirst), typeof(ISecond)));
        var left = tree.Entries["left"];
        var right = tree.Entries["right"];
        var child = tree.Entries["child"];

        Assert.NotEqual(left.Scope.Resolve(typeof(IFirst)), right.Scope.Resolve(typeof(IFirst)));
        Assert.Equal(left.Scope.Resolve(typeof(IFirst)), child.Scope.Resolve(typeof(IFirst)));
        Assert.NotEqual(left.Scope.Resolve(typeof(ISecond)), child.Scope.Resolve(typeof(ISecond)));
        Assert.Equal(runtime.RootScope.Resolve(typeof(ISecond)), left.Scope.Resolve(typeof(ISecond)));
        Assert.Same(left.Scope, child.ParentScope);
        Assert.Same(runtime.RootScope, left.ParentScope);
    }

    /// <summary>Reordering rows, isolation names or profile lineage does not replace unchanged runtime nodes.</summary>
    [Fact]
    public void EquivalentStructureReusesNodesScopesAndIdentities()
    {
        var runtime = new ServiceRuntime(16);
        var catalog = Catalog(typeof(IFirst), typeof(ISecond));
        var parent = Row("parent", isolate: [typeof(ISecond), typeof(IFirst)]);
        var child = Row("child", "parent");
        var previous = PluginEntryTree.Build([child, parent], runtime, catalog);
        var before = runtime.NewActivation("before").Generation;
        var current = PluginEntryTree.Build([
            parent with { IsolatedServices = parent.IsolatedServices.Reverse().ToArray(), Lineage = ["unrelated-profile"], Config = new Dictionary<string, object?> { ["value"] = 2 } },
            child with { Lineage = ["other-profile"] },
        ], runtime, catalog, previous);

        Assert.Same(previous.Entries["parent"], current.Entries["parent"]);
        Assert.Same(previous.Entries["child"], current.Entries["child"]);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
        Assert.Equal(parent.IsolatedServices.Order(StringComparer.Ordinal), current.Entries["parent"].IsolatedServices);
    }

    /// <summary>Moving a parent changes descendant scopes without changing stable entry identities.</summary>
    [Fact]
    public void MovingAnEntryRebuildsDescendantScopesAndPreservesIdentity()
    {
        var runtime = new ServiceRuntime(16);
        var catalog = Catalog(typeof(IFirst), typeof(ISecond));
        PluginRow[] rows = [Row("left", isolate: [typeof(IFirst)]), Row("right", isolate: [typeof(IFirst)]),
            Row("moving", "left", [typeof(ISecond)]), Row("descendant", "moving")];
        var previous = PluginEntryTree.Build(rows, runtime, catalog);
        var current = PluginEntryTree.Build(rows.Select(row => row.Id == "moving" ? row with { ParentId = "right" } : row).ToArray(), runtime, catalog, previous);

        foreach (var id in new[] { "moving", "descendant" })
        {
            Assert.Same(previous.Entries[id].Identity, current.Entries[id].Identity);
            Assert.NotSame(previous.Entries[id].Scope, current.Entries[id].Scope);
            Assert.Equal(current.Entries["right"].Scope.Resolve(typeof(IFirst)), current.Entries[id].Scope.Resolve(typeof(IFirst)));
        }
        Assert.Same(previous.Entries["left"], current.Entries["left"]);
        Assert.Same(previous.Entries["right"], current.Entries["right"]);
        Assert.Same(current.Entries["moving"].Scope, current.Entries["descendant"].ParentScope);
        Assert.NotEqual(previous.Entries["moving"].Scope.Resolve(typeof(ISecond)), current.Entries["moving"].Scope.Resolve(typeof(ISecond)));
    }

    /// <summary>Changing one isolation selection creates a new scope but retains the entry's identity.</summary>
    [Fact]
    public void ChangedIsolationRebuildsTheScopeWithoutReplacingEntryIdentity()
    {
        var runtime = new ServiceRuntime(16);
        var catalog = Catalog(typeof(IFirst), typeof(ISecond));
        var previous = PluginEntryTree.Build([Row("entry", isolate: [typeof(IFirst)])], runtime, catalog);
        var current = PluginEntryTree.Build([Row("entry", isolate: [typeof(ISecond)])], runtime, catalog, previous);

        Assert.Same(previous.Entries["entry"].Identity, current.Entries["entry"].Identity);
        Assert.NotSame(previous.Entries["entry"].Scope, current.Entries["entry"].Scope);
        Assert.Equal(runtime.RootScope.Resolve(typeof(IFirst)), current.Entries["entry"].Scope.Resolve(typeof(IFirst)));
        Assert.NotEqual(runtime.RootScope.Resolve(typeof(ISecond)), current.Entries["entry"].Scope.Resolve(typeof(ISecond)));
    }

    /// <summary>Neither source arrays nor mutable collection interfaces can alter an admitted tree.</summary>
    [Fact]
    public void TreeCollectionsRemainDetachedAndReadOnly()
    {
        var names = new[] { typeof(IFirst).FullName! };
        var tree = PluginEntryTree.Build([Row("entry") with { IsolatedServices = names }], new ServiceRuntime(16), Catalog(typeof(IFirst)));
        names[0] = typeof(ISecond).FullName!;
        var entry = tree.Entries["entry"];

        Assert.Equal(typeof(IFirst).FullName, Assert.Single(entry.IsolatedServices));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)entry.IsolatedServices)[0] = typeof(ISecond).FullName!);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, PluginRuntimeEntry>)tree.Entries).Remove("entry"));
    }

    /// <summary>Inherited isolation capacity is validated before allocating even the first valid parent realm.</summary>
    [Fact]
    public void InheritedCapacityFailureDoesNotAllocateRealms()
    {
        var runtime = new ServiceRuntime(1);
        var before = runtime.NewActivation("before").Generation;
        var error = Assert.Throws<ConfigException>(() => PluginEntryTree.Build(
            [Row("parent", isolate: [typeof(IFirst)]), Row("child", "parent", [typeof(ISecond)])],
            runtime, Catalog(typeof(IFirst), typeof(ISecond))));

        Assert.Contains("child", error.Message);
        Assert.Contains("isolated_services", error.Message);
        Assert.Equal(before + 1, runtime.NewActivation("after").Generation);
    }

    /// <summary>Host bridge contracts and declared closed generic interfaces can be isolated by full name.</summary>
    [Fact]
    public void BuiltInAndClosedGenericContractsAreKnown()
    {
        var runtime = new ServiceRuntime(16);
        Type[] contracts = [typeof(IServiceRegistry), typeof(IEventBus), typeof(IPluginHost), typeof(IEnumerable<int>)];
        var tree = PluginEntryTree.Build([Row("entry", isolate: contracts)], runtime, Catalog(typeof(IEnumerable<int>)));

        foreach (var contract in contracts)
            Assert.NotEqual(runtime.RootScope.Resolve(contract), tree.Entries["entry"].Scope.Resolve(contract));
    }

    /// <summary>Deep parent chains are ordered iteratively, including when input is reversed.</summary>
    [Fact]
    public void DeepReversedHierarchyDoesNotRequireRecursiveTraversal()
    {
        var rows = Enumerable.Range(0, 4096).Select(index => Row($"entry-{index}", index == 0 ? null : $"entry-{index - 1}")).Reverse().ToArray();
        var runtime = new ServiceRuntime(16);
        var tree = PluginEntryTree.Build(rows, runtime, Catalog());

        Assert.Equal(rows.Length, tree.Entries.Count);
        Assert.Same(tree.Entries["entry-4094"].Scope, tree.Entries["entry-4095"].ParentScope);
        Assert.Equal(runtime.RootScope.Resolve(typeof(IEventBus)), tree.Entries["entry-4095"].Scope.Resolve(typeof(IEventBus)));
    }

    private static PluginRow Row(string id, string? parent = null, Type[]? isolate = null) => new()
    {
        Id = id, Plugin = "tree", Source = "entry-tree-test", ParentId = parent,
        IsolatedServices = (isolate ?? []).Select(type => type.FullName!).ToArray(),
    };

    private static PluginCatalog Catalog(params Type[] contracts) => PluginCatalog.FromDefinitions([new PluginDescriptor(
        new PluginAttribute("tree") { Provides = contracts }, new Factory(), "entry-tree-test", "1.0.0",
        new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() })]);

    private sealed class Factory : IPluginExecutionFactory
    {
        /// <summary>Keeps structural test definitions independent of executable backend coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Rejects execution because entry tree construction must not acquire a backend.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Entry tree tests must not acquire an execution.");
    }

    private sealed class ContractFixture(AssemblyLoadContext context, Type contract) : IDisposable
    {
        internal Type Contract { get; } = contract;
        /// <summary>Releases the test-only contract assembly after tree validation is finished.</summary>
        public void Dispose() => context.Unload();
    }

    private static ContractFixture EmitContract()
    {
        var context = new AssemblyLoadContext($"entry-tree-contract-{Guid.NewGuid():N}", isCollectible: true);
        using var reflection = context.EnterContextualReflection();
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"EntryTreeContract-{Guid.NewGuid():N}"), AssemblyBuilderAccess.RunAndCollect);
        var contract = assembly.DefineDynamicModule("contract").DefineType("TreeFixture.IDuplicate", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(contract.Assembly));
        return new ContractFixture(context, contract);
    }
}
