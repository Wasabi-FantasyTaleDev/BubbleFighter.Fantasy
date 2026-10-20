using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Native;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

/// <summary>Protects the independent native runtime and ordinary loader plugin dependency boundaries.</summary>
public sealed class NativeLoaderBoundaryTests
{
    /// <summary>The native runtime consumes only shared abstractions, without depending on host, kernel or game assemblies.</summary>
    [Fact]
    public void NativeBackendReferencesOnlyTheLanguageNeutralAbstractions()
    {
        var assembly = typeof(NativePluginModules).Assembly;

        Assert.Equal("BubbleFighter.Fantasy.Native", assembly.GetName().Name);
        Assert.Equal(["BubbleFighter.Fantasy.Abstractions"], ProjectReferences(assembly));
        Assert.True(typeof(IPluginExecutionFactory).IsAssignableFrom(typeof(BubbleFighter.Fantasy.Native.NativePluginExecutionFactory)));
        Assert.True(typeof(IPluginModuleSource).IsAssignableFrom(typeof(BubbleFighter.Fantasy.Native.NativePluginExecutionFactory)));
        Assert.True(typeof(IPluginDefinitionBatch).IsAssignableFrom(typeof(BubbleFighter.Fantasy.Native.NativeModuleLoadBatch)));
    }

    /// <summary>The isolated loader declares the public host service and never references another plugin or privileged runtime internals.</summary>
    [Fact]
    public void NativeLoaderUsesThePublicHostServiceWithoutKernelOrGameDependencies()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugins", "BubbleFighter.Fantasy.Loader.Native");
        var definitions = PluginCatalog.LoadModule(
            Path.Combine(directory, "BubbleFighter.Fantasy.Loader.Native.dll"), directory);
        var definition = Assert.Single(definitions);
        var implementation = Assert.IsAssignableFrom<Type>(definition.Type);

        Assert.Equal("loader.native", definition.Id);
        Assert.True(typeof(IPlugin).IsAssignableFrom(implementation));
        Assert.Equal(["BubbleFighter.Fantasy.Abstractions", "BubbleFighter.Fantasy.Native"],
            ProjectReferences(implementation.Assembly));
        Assert.Equal([typeof(IPluginHost)], definition.Declaration.Requires);
        Assert.Empty(definition.Declaration.Provides);
        Assert.Empty(definition.Declaration.Overrides);
    }

    private static string[] ProjectReferences(Assembly assembly) => assembly.GetReferencedAssemblies()
        .Select(reference => reference.Name!)
        .Where(name => name.StartsWith("BubbleFighter.", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();
}
