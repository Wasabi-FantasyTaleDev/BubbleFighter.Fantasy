using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Native;
using Xunit;
using NativeBatch = BubbleFighter.Fantasy.Native.NativeModuleLoadBatch;
using NativeFactory = BubbleFighter.Fantasy.Native.NativePluginExecutionFactory;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises bounded native source preparation and real owned batches without claiming physical code reclamation.</summary>
public sealed class PluginNativeSourceBatchTests
{
    /// <summary>Caller mutations cannot change already prepared operation keys or redirect the acquired module.</summary>
    [Fact]
    public async Task PreparedPathsAndKeysAreIndependentOfCallerCollections()
    {
        var roots = new[] { FixtureRoot() };
        var exclusions = new[] { "right" };
        var plan = NativePluginModules.Prepare(roots, 2, exclusions);
        var expected = Assert.IsType<string>(NativeFactory.GetCoordinationKey(FixturePath("left")));
        roots[0] = Path.Combine(FixtureRoot(), "missing-root");
        exclusions[0] = "left";

        Assert.Equal([expected], plan.CoordinationKeys);
        await using var batch = await plan.LoadAsync(default);
        Assert.Equal(2, batch.Definitions.Count);
        Assert.All(batch.Definitions, definition => Assert.Equal(expected, definition.ExecutionFactory.CoordinationKey));
    }

    /// <summary>An excluded duplicate module contributes neither a reserved key nor definitions to the acquired batch.</summary>
    [Fact]
    public async Task SeedModuleExclusionPrecedesAcquisition()
    {
        var plan = NativePluginModules.Prepare([FixtureRoot()], 2, ["right"]);

        Assert.Equal([Assert.IsType<string>(NativeFactory.GetCoordinationKey(FixturePath("left")))], plan.CoordinationKeys);
        await using var batch = await plan.LoadAsync(default);
        Assert.Equal(["test.multi.alpha", "test.multi.beta"], batch.Definitions.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.All(batch.Definitions, definition =>
            Assert.Equal(FixturePath("left"), Assert.IsType<NativeFactory>(definition.ExecutionFactory).MainAssemblyPath));
    }

    /// <summary>An acquired batch rejects further public module loads before and after its logical ownership is released.</summary>
    [Fact]
    public async Task AcquiredBatchIsSealedAndDisposalIsIdempotent()
    {
        var plan = NativePluginModules.Prepare([FixtureRoot()], 2, ["right"]);
        var batch = Assert.IsType<NativeBatch>(await plan.LoadAsync(default));
        var path = FixturePath("left");
        Assert.Equal(2, batch.Definitions.Count);
        Assert.Throws<InvalidOperationException>(() => batch.Load(path, Path.GetDirectoryName(path)!));

        await batch.DisposeAsync();
        await batch.DisposeAsync();

        Assert.Empty(batch.Definitions);
        Assert.Throws<InvalidOperationException>(() => batch.Load(path, Path.GetDirectoryName(path)!));
    }

    /// <summary>A real two-definition module exceeds a one-definition reservation and retains truthful failure cleanup facts.</summary>
    [Fact]
    public async Task DefinitionOverflowPreservesNativeSourceFailureDiagnostics()
    {
        var plan = NativePluginModules.Prepare([FixtureRoot()], 1, ["right"]);

        var error = await Assert.ThrowsAsync<ConfigException>(async () => { _ = await plan.LoadAsync(default); });

        using var diagnostics = JsonDocument.Parse(Assert.IsType<string>(NativeBatch.GetFailureDiagnostics(error)));
        Assert.Equal("definition-source", diagnostics.RootElement.GetProperty("Operation").GetString());
        var module = Assert.Single(diagnostics.RootElement.GetProperty("Modules").EnumerateArray());
        Assert.Equal("scan", module.GetProperty("Stage").GetString());
        Assert.Equal("ProcessLifetimeRetained", module.GetProperty("Cleanup").GetString());
        Assert.True(module.GetProperty("RestartRequiredForReclamation").GetBoolean());
    }

    /// <summary>Root and module cardinality are rejected during preparation before an acquisition batch exists.</summary>
    [Fact]
    public void PreparationRejectsRootAndModuleCountsBeyondCapacity()
    {
        var root = FixtureRoot();

        var roots = Assert.Throws<ConfigException>(() => NativePluginModules.Prepare([root, root], 1));
        var modules = Assert.Throws<ConfigException>(() => NativePluginModules.Prepare([root], 1));

        Assert.Contains("root count", roots.Message);
        Assert.Contains("module count", modules.Message);
        Assert.Null(NativeBatch.GetFailureDiagnostics(roots));
        Assert.Null(NativeBatch.GetFailureDiagnostics(modules));
    }

    /// <summary>Excluded directories still consume traversal capacity, preventing a no-module scan from escaping its bound.</summary>
    [Fact]
    public void ExcludedDirectoriesStillConsumeScanBudget()
    {
        var error = Assert.Throws<ConfigException>(() => NativePluginModules.Prepare(
            [FixtureRoot()], 2, ["left", "right"], maximumScannedDirectories: 1));

        Assert.Contains("scanned directory capacity", error.Message);
        Assert.Null(NativeBatch.GetFailureDiagnostics(error));
    }

    /// <summary>A cancelled preparation stops before filesystem traversal and before native acquisition.</summary>
    [Fact]
    public void PreparationObservesAlreadyCancelledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => NativePluginModules.Prepare(
            [Path.Combine(FixtureRoot(), "missing-root")], 1, cancellationToken: cancellation.Token));
    }

    private static string FixtureRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-copies");
        Assert.True(File.Exists(Path.Combine(root, "left", "left.dll")));
        Assert.True(File.Exists(Path.Combine(root, "right", "right.dll")));
        return root;
    }

    private static string FixturePath(string name) => Path.Combine(FixtureRoot(), name, name + ".dll");
}
