using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Protects isolated service realms when replacement code reuses interface names with distinct type identities.</summary>
public sealed class PluginEntryTreeTypeRevisionTests
{
    private interface IStableContract { }

    /// <summary>A same-name interface from a new collectible assembly cannot escape an entry's configured isolation.</summary>
    [Fact]
    public void SameNameNewTypeGetsNewScopeWhileOldScopeRetainsItsOriginalType()
    {
        using var oldCode = EmitContract();
        using var newCode = EmitContract();
        Assert.Equal(oldCode.Contract.FullName, newCode.Contract.FullName);
        Assert.NotSame(oldCode.Contract, newCode.Contract);
        Assert.NotSame(oldCode.Contract.Assembly, newCode.Contract.Assembly);
        var runtime = new ServiceRuntime(32);
        PluginRow[] rows = [Row("isolated", oldCode.Contract), Row("child", parent: "isolated"), Row("root-peer")];
        var previous = PluginEntryTree.Build(rows, runtime, Catalog(oldCode.Contract));
        var heldOldEntry = previous.Entries["isolated"];
        var heldOldScope = heldOldEntry.Scope;
        var oldKey = heldOldScope.Resolve(oldCode.Contract);

        var current = PluginEntryTree.Build(rows, runtime, Catalog(newCode.Contract), previous);

        Assert.NotSame(heldOldEntry, current.Entries["isolated"]);
        Assert.Same(heldOldEntry.Identity, current.Entries["isolated"].Identity);
        Assert.Same(oldCode.Contract, Assert.Single(heldOldEntry.IsolatedContracts));
        Assert.Same(newCode.Contract, Assert.Single(current.Entries["isolated"].IsolatedContracts));
        Assert.NotEqual(runtime.RootScope.Resolve(newCode.Contract), current.Entries["isolated"].Scope.Resolve(newCode.Contract));
        Assert.Equal(current.Entries["isolated"].Scope.Resolve(newCode.Contract), current.Entries["child"].Scope.Resolve(newCode.Contract));
        Assert.NotSame(previous.Entries["child"].Scope, current.Entries["child"].Scope);
        Assert.Equal(runtime.RootScope.Resolve(newCode.Contract), current.Entries["root-peer"].Scope.Resolve(newCode.Contract));
        Assert.Equal(oldKey, heldOldScope.Resolve(oldCode.Contract));
        Assert.NotEqual(runtime.RootScope.Resolve(oldCode.Contract), oldKey);
        Assert.Equal(runtime.RootScope.Resolve(newCode.Contract), heldOldScope.Resolve(newCode.Contract));
        Assert.Equal(runtime.RootScope.Resolve(oldCode.Contract), current.Entries["isolated"].Scope.Resolve(oldCode.Contract));
    }

    /// <summary>Replacing a descriptor while retaining the same actual interface type preserves entry and descendant scope identity.</summary>
    [Fact]
    public void SameActualTypePreservesTheExistingScopeAcrossNewCatalogSnapshots()
    {
        using var code = EmitContract();
        var runtime = new ServiceRuntime(16);
        PluginRow[] rows = [Row("isolated", code.Contract), Row("child", parent: "isolated")];
        var previous = PluginEntryTree.Build(rows, runtime, Catalog(code.Contract));

        var current = PluginEntryTree.Build(rows, runtime, Catalog(code.Contract), previous);

        Assert.Same(previous.Entries["isolated"], current.Entries["isolated"]);
        Assert.Same(previous.Entries["child"], current.Entries["child"]);
        var exposed = Assert.IsAssignableFrom<IList<Type>>(current.Entries["isolated"].IsolatedContracts);
        Assert.Throws<NotSupportedException>(() => exposed[0] = typeof(IStableContract));
        Assert.Same(code.Contract, Assert.Single(current.Entries["isolated"].IsolatedContracts));
    }

    /// <summary>Only a branch whose resolved contract identity changed needs new scopes; unrelated realms remain stable.</summary>
    [Fact]
    public void NewTypeRevisionDoesNotReplaceAnUnrelatedStableBranch()
    {
        using var oldCode = EmitContract();
        using var newCode = EmitContract();
        var runtime = new ServiceRuntime(32);
        PluginRow[] rows = [Row("changing", oldCode.Contract), Row("stable", typeof(IStableContract)), Row("stable-child", parent: "stable")];
        var previous = PluginEntryTree.Build(rows, runtime, Catalog(oldCode.Contract, typeof(IStableContract)));

        var current = PluginEntryTree.Build(rows, runtime, Catalog(newCode.Contract, typeof(IStableContract)), previous);

        Assert.NotSame(previous.Entries["changing"].Scope, current.Entries["changing"].Scope);
        Assert.Same(previous.Entries["stable"], current.Entries["stable"]);
        Assert.Same(previous.Entries["stable-child"], current.Entries["stable-child"]);
        Assert.NotEqual(runtime.RootScope.Resolve(typeof(IStableContract)), current.Entries["stable-child"].Scope.Resolve(typeof(IStableContract)));
    }

    private static PluginRow Row(string id, Type? isolate = null, string? parent = null) => new()
    {
        Id = id, Plugin = "revision-tree", Source = "type-revision-test", ParentId = parent,
        IsolatedServices = isolate is null ? [] : [isolate.FullName!],
    };

    private static PluginCatalog Catalog(params Type[] contracts) => PluginCatalog.FromDefinitions([new PluginDescriptor(
        new PluginAttribute("revision-tree") { Provides = contracts }, new Factory(), "type-revision-test", "1.0.0",
        new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() })]);

    private sealed class Factory : IPluginExecutionFactory
    {
        /// <summary>Leaves structural tree tests independent of executable backend coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Rejects accidental execution while metadata-only entry scopes are being rebuilt.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Type revision tree tests must not acquire an execution.");
    }

    private sealed class ContractFixture(AssemblyLoadContext context, Type contract) : IDisposable
    {
        internal Type Contract { get; } = contract;
        /// <summary>Requests release of the collectible test context after all isolation assertions finish.</summary>
        public void Dispose() => context.Unload();
    }

    private static ContractFixture EmitContract()
    {
        var context = new AssemblyLoadContext($"entry-type-revision-{Guid.NewGuid():N}", isCollectible: true);
        using var reflection = context.EnterContextualReflection();
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"EntryTypeRevision-{Guid.NewGuid():N}"), AssemblyBuilderAccess.RunAndCollect);
        var contract = assembly.DefineDynamicModule("contract")
            .DefineType("EntryTypeRevision.ISharedName", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(contract.Assembly));
        Assert.True(contract.Assembly.IsCollectible);
        return new ContractFixture(context, contract);
    }
}
