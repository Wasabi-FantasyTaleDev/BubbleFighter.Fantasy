using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks service declaration shapes at backend definition and native discovery boundaries.</summary>
public sealed class PluginDeclarationShapeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Rejects null entries in every service declaration array with the declaring field and index.</summary>
    [Theory]
    [InlineData(nameof(PluginAttribute.Requires))]
    [InlineData(nameof(PluginAttribute.RequiresMany))]
    [InlineData(nameof(PluginAttribute.Optional))]
    [InlineData(nameof(PluginAttribute.OptionalMany))]
    [InlineData(nameof(PluginAttribute.Provides))]
    [InlineData(nameof(PluginAttribute.ProvidesMany))]
    [InlineData(nameof(PluginAttribute.ConditionalProvides))]
    [InlineData(nameof(PluginAttribute.ConditionalProvidesMany))]
    [InlineData(nameof(PluginAttribute.Observes))]
    [InlineData(nameof(PluginAttribute.Decorates))]
    public void NullEntriesIdentifyTheirDeclarationFieldAndIndex(string field)
    {
        var declaration = WithContracts("null-entry", field, [typeof(IDisposable), null!]);

        var failure = Assert.Throws<ConfigException>(() => Definition(declaration));

        Assert.Contains("Plugin 'null-entry'", failure.Message);
        Assert.Contains($"declaration field '{field}[1]'", failure.Message);
    }

    /// <summary>Rejects concrete and open generic types before a backend definition can enter a catalog.</summary>
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(IEnumerable<>))]
    public void NonInterfaceAndOpenGenericEntriesAreRejectedAtDefinitionConstruction(Type invalidContract)
    {
        var declaration = new PluginAttribute("invalid-contract")
        { ConditionalProvidesMany = [typeof(IEnumerable<int>), invalidContract] };

        var failure = Assert.Throws<ConfigException>(() => Definition(declaration));

        Assert.Contains("Plugin 'invalid-contract'", failure.Message);
        Assert.Contains("declaration field 'ConditionalProvidesMany[1]'", failure.Message);
        Assert.Contains("must be a closed interface", failure.Message);
    }

    /// <summary>Rejects repeated contracts within one array at the second occurrence.</summary>
    [Theory]
    [InlineData(nameof(PluginAttribute.Requires))]
    [InlineData(nameof(PluginAttribute.ProvidesMany))]
    [InlineData(nameof(PluginAttribute.Observes))]
    public void DuplicateEntriesIdentifyTheRepeatedIndex(string field)
    {
        var declaration = WithContracts("duplicate-entry", field,
            [typeof(IDisposable), typeof(IEnumerable<int>), typeof(IDisposable)]);

        var failure = Assert.Throws<ConfigException>(() => Definition(declaration));

        Assert.Contains("Plugin 'duplicate-entry'", failure.Message);
        Assert.Contains($"declaration field '{field}[2]'", failure.Message);
        Assert.Contains("duplicates 'System.IDisposable' in the same field", failure.Message);
    }

    /// <summary>Reports an absent array as a field error rather than a null-reference failure.</summary>
    [Fact]
    public void NullArraysIdentifyTheirDeclarationField()
    {
        var declaration = new PluginAttribute("null-array") { OptionalMany = null! };

        var failure = Assert.Throws<ConfigException>(() => Definition(declaration));

        Assert.Equal("Plugin 'null-array' declaration field 'OptionalMany' must not be null.", failure.Message);
    }

    /// <summary>Accepts distinct closed interfaces in each declaration field and preserves them in catalog snapshots.</summary>
    [Fact]
    public void ClosedInterfacesAreAcceptedInEveryDeclarationField()
    {
        var fields = typeof(PluginAttribute).GetProperties()
            .Where(property => property.PropertyType == typeof(Type[])).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(10, fields.Length);
        Type[] contracts = [typeof(IDisposable), typeof(IEnumerable<int>)];

        foreach (var field in fields)
        {
            var declaration = WithContracts($"closed-{field.Name}", field.Name, contracts);
            var definition = Definition(declaration);
            var catalog = PluginCatalog.FromDefinitions([definition]);

            Assert.Same(definition, catalog.Find(declaration.Id));
            Assert.Equal(contracts, Assert.IsType<Type[]>(field.GetValue(definition.Declaration)));
        }
    }

    /// <summary>Retains legal optional reads and publications of the same contract at either service cardinality.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalReadsAndPublicationsMayShareTheSameCardinality(bool multiple)
    {
        var declaration = multiple
            ? new PluginAttribute("matching-multiple") { OptionalMany = [typeof(IValue)], ProvidesMany = [typeof(IValue)] }
            : new PluginAttribute("matching-single") { Optional = [typeof(IValue)], Provides = [typeof(IValue)] };
        var activated = false;
        var execution = new Execution(declaration, context =>
        {
            IValue value = new Value();
            if (multiple)
            {
                Assert.Empty(context.Services.GetAll<IValue>());
                context.Effect(context.Services.RegisterMany<IValue>(value));
                Assert.Same(value, Assert.Single(context.Services.GetAll<IValue>()));
            }
            else
            {
                Assert.False(context.Services.TryGet<IValue>(out _));
                context.Effect(context.Services.Register<IValue>(value));
                Assert.Same(value, context.Services.Get<IValue>());
            }
            activated = true;
        });
        var definition = Definition(declaration, execution);
        var row = new PluginRow
        { Id = declaration.Id, Plugin = declaration.Id, Source = "declaration-shape-test", Lifecycle = new() { EffectCapacity = 16 } };
        var host = new PluginHost([row], PluginCatalog.FromDefinitions([definition]), NullLoggerFactory.Instance);
        try
        {
            await host.LoadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            Assert.True(activated);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, execution.Closes);
    }

    /// <summary>Rejects malformed native attribute metadata while scanning its isolated generated assembly.</summary>
    [Fact]
    public void NativeDiscoveryRejectsMalformedContractsBeforeReturningACatalog()
    {
        using var fixture = NativeAssembly("native-invalid", [typeof(IEnumerable<int>), typeof(string)]);

        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.ScanAssemblies([fixture.Assembly]));

        Assert.Contains("Plugin 'native-invalid'", failure.Message);
        Assert.Contains("declaration field 'Provides[1]'", failure.Message);
        Assert.Contains("must be a closed interface", failure.Message);
    }

    /// <summary>Accepts closed generic interfaces from native attribute metadata through the same discovery path.</summary>
    [Fact]
    public void NativeDiscoveryAcceptsClosedInterfaceContracts()
    {
        using var fixture = NativeAssembly("native-valid", [typeof(IEnumerable<int>)]);

        var definition = Assert.Single(PluginCatalog.ScanAssemblies([fixture.Assembly]).Plugins);

        Assert.Equal("native-valid", definition.Id);
        Assert.Same(fixture.Assembly, definition.Type!.Assembly);
        Assert.Equal(typeof(IEnumerable<int>), Assert.Single(definition.Declaration.Provides));
    }

    private static PluginAttribute WithContracts(string id, string field, Type[] contracts)
    {
        var declaration = new PluginAttribute(id);
        typeof(PluginAttribute).GetProperty(field)!.SetValue(declaration, contracts);
        return declaration;
    }

    private static PluginDescriptor Definition(PluginAttribute declaration, IPluginExecution? execution = null) =>
        new(declaration, new Factory(execution), "declaration-shape-test", "1.0.0", Versions);

    private sealed class NativeFixture(AssemblyLoadContext context, Assembly assembly) : IDisposable
    {
        internal Assembly Assembly { get; } = assembly;
        public void Dispose() => context.Unload();
    }

    private static NativeFixture NativeAssembly(string id, Type[] contracts)
    {
        var context = new AssemblyLoadContext($"declaration-fixture-{Guid.NewGuid():N}", isCollectible: true);
        using var reflection = context.EnterContextualReflection();
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"DeclarationShape-{id}"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("plugin").DefineType("NativePlugin", TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(typeof(IPlugin));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(PluginAttribute).GetConstructor([typeof(string)])!, [id],
            [typeof(PluginAttribute).GetProperty(nameof(PluginAttribute.Provides))!], [contracts]));
        var activate = type.DefineMethod(nameof(IPlugin.ActivateAsync), MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            typeof(ValueTask), [typeof(IPluginContext), typeof(CancellationToken)]);
        var il = activate.GetILGenerator();
        var result = il.DeclareLocal(typeof(ValueTask));
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Initobj, typeof(ValueTask));
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(activate, typeof(IPlugin).GetMethod(nameof(IPlugin.ActivateAsync))!);
        var resultAssembly = type.CreateType()!.Assembly;
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(resultAssembly));
        return new NativeFixture(context, resultAssembly);
    }

    private interface IValue { }
    private sealed class Value : IValue { }

    private sealed class Factory(IPluginExecution? execution) : IPluginExecutionFactory
    {
        /// <summary>Keeps these in-process fixture executions independent of module coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Returns the supplied execution or rejects accidental activation of a declaration-only fixture.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            ValueTask.FromResult(execution ?? throw new InvalidOperationException("This declaration test must not acquire an execution."));
    }

    private sealed class Execution(PluginAttribute declaration, Action<IPluginContext> activate) : IPluginExecution
    {
        internal int Closes;
        /// <summary>Reports the same declaration used to construct the static backend definition.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the fixture release version expected by the descriptor.</summary>
        public string Version => "1.0.0";
        /// <summary>References the shared abstraction version loaded by this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies this fixture as a backend execution without a native plugin class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Exercises service access and publication through the hosted activation context.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            activate(context);
            return ValueTask.CompletedTask;
        }
        /// <summary>Counts completed backend cleanup calls to verify ownership during host shutdown.</summary>
        public ValueTask DisposeAsync()
        {
            Closes++;
            return ValueTask.CompletedTask;
        }
    }
}
