using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Verifies native definition reuse after resolution without loading replacement files from disk.</summary>
public sealed class PluginResolvedNativeExecutionTests
{
    /// <summary>A resolved native descriptor reacquires its newer type without changing the original factory.</summary>
    [Fact]
    public async Task ResolvedNativeFactoryReusesTheResolvedTypeWithoutRefreshing()
    {
        using var firstFixture = NativeDefinition("FirstImplementation", 1);
        using var secondFixture = NativeDefinition("SecondImplementation", 2);
        var first = firstFixture.Descriptor;
        var second = secondFixture.Descriptor;
        await using var updated = await second.ExecutionFactory.CreateAsync(reload: false, CancellationToken.None);

        var resolved = first.ResolveExecution(updated);
        await using var reused = await resolved.ExecutionFactory.CreateAsync(reload: false, CancellationToken.None);
        await using var original = await first.ExecutionFactory.CreateAsync(reload: false, CancellationToken.None);

        Assert.Same(second.Type, resolved.Type);
        Assert.Same(second.Type, reused.ImplementationType);
        Assert.Equal(second.Version, reused.Version);
        Assert.Same(first.Type, original.ImplementationType);
        Assert.Equal(first.Version, original.Version);
        Assert.NotSame(first.ExecutionFactory, resolved.ExecutionFactory);
        Assert.Equal(first.CoordinationKey, resolved.CoordinationKey);
    }

    /// <summary>A backend that reports a native type retains its own external acquisition factory.</summary>
    [Fact]
    public async Task ResolvedExternalFactoryIsPreservedWhenItsExecutionReportsANativeType()
    {
        using var fixture = NativeDefinition("ExternalImplementation", 2);
        var native = fixture.Descriptor;
        var factory = new ExternalFactory(native.ExecutionFactory);
        var definition = new PluginDescriptor(native.Declaration, factory, "resolved-native-test", "1.0.0", native.ContractVersions);
        await using var execution = await factory.CreateAsync(reload: false, CancellationToken.None);

        var resolved = definition.ResolveExecution(execution);

        Assert.Same(factory, resolved.ExecutionFactory);
        Assert.Same(native.Type, resolved.Type);
        Assert.Equal(factory.CoordinationKey, resolved.CoordinationKey);
    }

    private sealed class NativeFixture(AssemblyLoadContext context, PluginDescriptor descriptor) : IDisposable
    {
        internal PluginDescriptor Descriptor { get; } = descriptor;

        /// <summary>Releases the isolated fixture context after its executions have been disposed.</summary>
        public void Dispose() => context.Unload();
    }

    private static NativeFixture NativeDefinition(string typeName, int version)
    {
        var context = new AssemblyLoadContext($"resolved-native-fixture-{Guid.NewGuid():N}", isCollectible: true);
        using var reflection = context.EnterContextualReflection();
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ResolvedNativeExecution_{Guid.NewGuid():N}") { Version = new Version(version, 0, 0, 0) },
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("Implementation");
        var builder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), [typeof(IPlugin)]);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(PluginAttribute).GetConstructor([typeof(string)])!, ["test.native.resolved"]));
        var activate = builder.DefineMethod(nameof(IPlugin.ActivateAsync),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(ValueTask), [typeof(IPluginContext), typeof(CancellationToken)]);
        var il = activate.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(ValueTask).GetProperty(nameof(ValueTask.CompletedTask))!.GetMethod!);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(activate, typeof(IPlugin).GetMethod(nameof(IPlugin.ActivateAsync))!);
        var type = builder.CreateType()!;
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(type.Assembly));
        return new NativeFixture(context, new PluginDescriptor("test.native.resolved", null, type, "resolved-native-test",
            Path.Combine(Path.GetTempPath(), "resolved-native-execution.dll")));
    }

    private sealed class ExternalFactory(IPluginExecutionFactory inner) : IPluginExecutionFactory
    {
        /// <inheritdoc />
        public string? CoordinationKey => inner.CoordinationKey;

        /// <inheritdoc />
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            inner.CreateAsync(reload, cancellationToken);
    }
}
