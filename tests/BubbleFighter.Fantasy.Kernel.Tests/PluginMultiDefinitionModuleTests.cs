using System.Collections.Concurrent;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises real native modules that export multiple independently selected plugin definitions.</summary>
public sealed class PluginMultiDefinitionModuleTests
{
    private const string Alpha = "test.multi.alpha";
    private const string Beta = "test.multi.beta";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>A single module load returns both definitions with the same actual assembly and code revision identity.</summary>
    [Fact]
    public void ModuleDefinitionsShareOneActualAssemblyAndCodeIdentity()
    {
        var path = FixturePath();

        var definitions = PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!);

        Assert.Equal(2, definitions.Count);
        Assert.Equal([Alpha, Beta], definitions.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray());
        var first = definitions.Single(item => item.Id == Alpha);
        var second = definitions.Single(item => item.Id == Beta);
        Assert.Same(first.Type!.Assembly, second.Type!.Assembly);
        Assert.NotSame(first.Type, second.Type);
        Assert.Same(first.ModuleCode!.Identity, second.ModuleCode!.Identity);
        Assert.Same(first.Type.Assembly, first.ModuleCode.Identity);
        Assert.Equal(first.ModuleCode.ModuleId, second.ModuleCode.ModuleId);
        Assert.Equal(first.CoordinationKey, second.CoordinationKey);
        Assert.Equal(path, first.MainAssemblyPath);
        Assert.Equal(path, second.MainAssemblyPath);
        Assert.Equal("native-assembly", first.ModuleCode.Attribution);
        Assert.Equal(2, PluginCatalog.FromDefinitions(definitions).Plugins.Count);
    }

    /// <summary>Selection resolves the requested declaration instead of returning whichever native type was discovered first.</summary>
    [Theory]
    [InlineData(Alpha, "AlphaPlugin")]
    [InlineData(Beta, "BetaPlugin")]
    public void LoadDefinitionSelectsTheRequestedPluginId(string id, string typeName)
    {
        var path = FixturePath();

        var descriptor = PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, id);

        Assert.Equal(id, descriptor.Id);
        Assert.Equal(id, descriptor.Declaration.Id);
        Assert.Equal(typeName, descriptor.Type!.Name);
        Assert.Equal(path, descriptor.MainAssemblyPath);
    }

    /// <summary>The id selector handles an export proven not to be the first item of the loaded module.</summary>
    [Fact]
    public void LoadDefinitionCanSelectANonFirstExport()
    {
        var path = FixturePath();
        var definitions = PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!);
        Assert.Equal(2, definitions.Count);
        var requested = definitions[1];
        Assert.NotEqual(definitions[0].Id, requested.Id);

        var selected = PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, requested.Id);

        Assert.Equal(requested.Id, selected.Id);
        Assert.Equal(requested.Type!.FullName, selected.Type!.FullName);
    }

    /// <summary>The legacy single-definition API keeps rejecting ambiguous multi-definition native modules.</summary>
    [Fact]
    public void LegacyLoadDescriptorStillRequiresExactlyOneDefinition()
    {
        var path = FixturePath();

        var error = Assert.Throws<ConfigException>(() => PluginCatalog.LoadDescriptor(path, Path.GetDirectoryName(path)!));

        Assert.Contains(Path.GetFileName(path), error.Message);
    }

    /// <summary>A missing id is a loud selection error rather than a fallback to the first export.</summary>
    [Fact]
    public void MissingDefinitionIdNeverFallsBackToAnotherExport()
    {
        var path = FixturePath();

        var error = Assert.Throws<ConfigException>(() =>
            PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, "test.multi.missing"));

        Assert.Contains("test.multi.missing", error.Message);
    }

    /// <summary>Two conflicting attributes inside one actual native DLL cannot be loaded or selected as a valid definition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateIdsInsideOneNativeModuleAreRejected(bool selectDefinition)
    {
        var path = FixturePath(duplicate: true);

        var error = Assert.Throws<ConfigException>(() =>
        {
            if (selectDefinition) _ = PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, "test.multi.duplicate");
            else _ = PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!);
        });

        Assert.Contains("test.multi.duplicate", error.Message);
    }

    /// <summary>Definitions loaded from real copies in distinct directories still conflict when composed into one catalog.</summary>
    [Fact]
    public void CopiedModulesInDifferentDirectoriesCannotIntroduceDuplicateCatalogIds()
    {
        // Process-lifetime assemblies remain mapped until the test host exits; build/clean owns these fixed artifacts.
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-copies");
        var leftDirectory = Path.Combine(root, "left");
        var rightDirectory = Path.Combine(root, "right");
        var leftPath = Path.Combine(leftDirectory, "left.dll");
        var rightPath = Path.Combine(rightDirectory, "right.dll");
        Assert.True(File.Exists(leftPath));
        Assert.True(File.Exists(rightPath));
        var left = PluginCatalog.LoadModule(leftPath, leftDirectory);
        var right = PluginCatalog.LoadModule(rightPath, rightDirectory);
        Assert.Equal(2, left.Count);
        Assert.Equal(2, right.Count);
        Assert.NotSame(left[0].Type!.Assembly, right[0].Type!.Assembly);
        Assert.NotEqual(left[0].ModuleCode!.ModuleId, right[0].ModuleCode!.ModuleId);

        var error = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions(left.Concat(right)));

        Assert.True(error.Message.Contains(Alpha, StringComparison.Ordinal) || error.Message.Contains(Beta, StringComparison.Ordinal));
    }

    /// <summary>Refreshing one export leaves its sibling on old code, and refreshing the sibling selects its own definition.</summary>
    [Fact]
    public async Task HostReloadsEachDefinitionByIdWithoutReplacingItsSiblingAssemblyOrCache()
    {
        var path = FixturePath();
        var definitions = PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!);
        var originalAssembly = definitions.Single(item => item.Id == Alpha).Type!.Assembly;
        var logger = new FixtureLoggerFactory();
        var host = new PluginHost([Row("alpha", Alpha), Row("beta", Beta)], PluginCatalog.FromDefinitions(definitions), logger);
        try
        {
            var bootstrap = Assert.Single(host.ModuleOwnership.Snapshot());
            Assert.Equal(2, bootstrap.CatalogPins);
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Same(originalAssembly, logger.Last("alpha").Assembly);
            Assert.Same(originalAssembly, logger.Last("beta").Assembly);
            Assert.Equal(Alpha, logger.Last("alpha").Definition);
            Assert.Equal(Beta, logger.Last("beta").Definition);
            Assert.Equal(2, Assert.Single(host.ModuleOwnership.Snapshot()).Activations);
            Assert.Equal(2, Assert.Single(host.ModuleOwnership.Snapshot()).DefinitionCaches);

            await host.ReloadAsync("alpha", default).AsTask().WaitAsync(Limit);

            var refreshedAlpha = logger.Last("alpha").Assembly;
            Assert.NotSame(originalAssembly, refreshedAlpha);
            Assert.Same(originalAssembly, logger.Last("beta").Assembly);
            Assert.Equal(1, logger.Count("beta"));
            var old = host.ModuleOwnership.Snapshot().Single(item => item.RevisionId == bootstrap.RevisionId);
            Assert.Equal(1, old.Activations);
            Assert.Equal(1, old.DefinitionCaches);
            Assert.Equal(2, old.CatalogPins);

            await host.ReloadAsync("beta", default).AsTask().WaitAsync(Limit);

            Assert.Equal(Beta, logger.Last("beta").Definition);
            Assert.NotSame(originalAssembly, logger.Last("beta").Assembly);
            Assert.NotSame(refreshedAlpha, logger.Last("beta").Assembly);
            Assert.Same(refreshedAlpha, logger.Last("alpha").Assembly);
            Assert.Equal(2, logger.Count("alpha"));
            Assert.Equal(2, logger.Count("beta"));
            Assert.Equal(3, host.ModuleOwnership.Snapshot().Count);
            var catalogOnly = host.ModuleOwnership.Snapshot().Single(item => item.RevisionId == bootstrap.RevisionId);
            Assert.Equal(0, catalogOnly.Activations);
            Assert.Equal(0, catalogOnly.DefinitionCaches);
            Assert.All(host.Instances, item => Assert.Equal(PluginState.Active, item.State));
            Assert.Equal(Alpha, host.Instances.Single(item => item.RowId == "alpha").Plugin);
            Assert.Equal(Beta, host.Instances.Single(item => item.RowId == "beta").Plugin);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    private static string FixturePath(bool duplicate = false)
    {
        var path = duplicate
            ? Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-duplicate", "DuplicateDefinitionModuleFixture.dll")
            : Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition", "MultiDefinitionModuleFixture.dll");
        Assert.True(File.Exists(path), $"Build-only multi-definition fixture is missing: {path}");
        return path;
    }

    private static PluginRow Row(string id, string plugin) => new()
    { Id = id, Plugin = plugin, Source = "multi-definition-test", Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 } };

    private sealed record Observation(string Row, string Definition, Assembly Assembly);

    private sealed class FixtureLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<Observation> _observations = new();
        internal Observation Last(string row) => _observations.Last(item => item.Row == row);
        internal int Count(string row) => _observations.Count(item => item.Row == row);
        /// <summary>Creates an observer for actual definition ids and loaded assembly objects.</summary>
        public ILogger CreateLogger(string categoryName) => new FixtureLogger(_observations);
        /// <summary>Uses no external providers for fixture observations.</summary>
        public void AddProvider(ILoggerProvider provider) { }
        /// <summary>Owns no external logging resources.</summary>
        public void Dispose() { }
    }

    private sealed class FixtureLogger(ConcurrentQueue<Observation> observations) : ILogger
    {
        /// <summary>Does not capture scope state.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Accepts the fixture's information-level activation observations.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>Records actual assembly identity rather than inferring it from a version or module counter.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var pairs = values.ToArray();
            if (pairs.FirstOrDefault(item => item.Key == "FixtureRow").Value is string row &&
                pairs.FirstOrDefault(item => item.Key == "FixtureDefinition").Value is string definition &&
                pairs.FirstOrDefault(item => item.Key == "FixtureAssembly").Value is Assembly assembly)
                observations.Enqueue(new(row, definition, assembly));
        }
    }
}
