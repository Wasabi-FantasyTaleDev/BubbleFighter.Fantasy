using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises owned native loading failures through the production pipeline with collectible context creation.</summary>
public sealed class PluginNativeLoadFailureTests
{
    private const string AlphaId = "test.multi.alpha";
    private const string MissingId = "test.multi.missing";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>A real assembly with no plugin exports fails after scanning and releases its actual collectible context and assembly.</summary>
    [Fact]
    public void AssemblyWithNoPluginDefinitionsRollsBackAfterRealScan()
    {
        var failed = CaptureEmptyModuleScan();
        Assert.IsType<ConfigException>(failed.Error);
        Assert.Contains("defines no [Plugin] type", failed.Error.Message);
        var item = Assert.Single(ReadReport(failed.Error, "load-module").GetProperty("Modules").EnumerateArray());
        var load = Assert.Single(failed.Loads);
        Assert.Equal("scan", item.GetProperty("Stage").GetString());
        AssertModule(item, typeof(IPlugin).Assembly.Location, "Collectible", "UnloadRequested", restartRequired: false);
        AssertCollected(load);
        GC.KeepAlive(failed.Error);
    }

    /// <summary>A failed second refresh releases its rejected code while preserving the first committed refresh for configuration recovery.</summary>
    [Fact]
    public void FailedNativeRefreshKeepsLastCommittedCacheForConfigurationRecovery()
    {
        using var harness = new NativeRefreshHarness();
        var failed = PrepareFailedNativeRefresh(harness);
        var cause = Assert.IsType<ConfigException>(failed.Error.InnerException);
        Assert.Contains(MissingId, cause.Message);
        Assert.Equal(PluginCatalog.GetLoadFailureDiagnostics(cause), PluginCatalog.GetLoadFailureDiagnostics(failed.Error));
        var item = Assert.Single(ReadReport(failed.Error, "load-definition").GetProperty("Modules").EnumerateArray());
        AssertModule(item, failed.Rejected.Path, "Collectible", "UnloadRequested", restartRequired: false);
        AssertCollected(failed.Rejected);

        var cached = Assert.Single(harness.Host.ModuleOwnership.Snapshot(), entry => entry.RevisionId == failed.CommittedRevision);
        Assert.Equal(0, cached.Activations);
        Assert.Equal(1, cached.DefinitionCaches);
        Assert.Equal(0, cached.CatalogPins);
        Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
        Assert.True(IsAlive(failed.Committed.Context));
        Assert.True(IsAlive(failed.Committed.Assembly!));

        RecoverCommittedNativeConfiguration(harness);

        Assert.Equal(3, harness.Factory.Loads.Count);
        Assert.Equal(PluginState.Active, Assert.Single(harness.Host.Instances).State);
        Assert.True(SameWeakTarget(failed.Committed.Assembly!, harness.Logs.LastAssembly));
        var recovered = Assert.Single(harness.Host.ModuleOwnership.Snapshot(), entry => entry.RevisionId == failed.CommittedRevision);
        Assert.Equal(1, recovered.Activations);
        Assert.Equal(1, recovered.DefinitionCaches);
        Assert.Equal(0, recovered.CatalogPins);
        Assert.Equal(0, Volatile.Read(ref failed.Committed.UnloadCount));
        GC.KeepAlive(harness);
        GC.KeepAlive(failed.Error);
    }

    /// <summary>Selection and module validation failures request unload and do not retain actual contexts or assemblies through diagnostics.</summary>
    [Theory]
    [InlineData("missing-id", "load-definition")]
    [InlineData("exactly-one", "load-descriptor")]
    [InlineData("duplicate-ids", "load-module")]
    public void RejectedNativeModuleReleasesActualCodeAndKeepsPrimitiveDiagnostics(string scenario, string operation)
    {
        var failed = CaptureRejectedModule(scenario);
        Assert.IsType<ConfigException>(failed.Error);
        var report = ReadReport(failed.Error, operation);
        var item = Assert.Single(report.GetProperty("Modules").EnumerateArray());
        var load = Assert.Single(failed.Loads);
        AssertModule(item, load.Path, "Collectible", "UnloadRequested", restartRequired: false);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("CleanupErrorType").ValueKind);
        Assert.Contains(scenario == "duplicate-ids" ? "test.multi.duplicate" :
            scenario == "missing-id" ? MissingId : "exactly one", failed.Error.Message);

        AssertCollected(load);

        GC.KeepAlive(failed.Error);
        GC.KeepAlive(report);
    }

    /// <summary>A failed discovery batch unwinds newly owned modules in reverse creation order without treating borrowed default assemblies as owned.</summary>
    [Fact]
    public void DiscoveryDuplicateRollsBackAllOwnedModulesAndPreservesBorrowedDefaultContext()
    {
        var failed = CaptureDiscoveryFailure(throwDuringUnload: false);
        Assert.IsType<ConfigException>(failed.Error);
        Assert.Contains("Duplicate", failed.Error.Message);
        Assert.Equal(2, failed.Loads.Length);
        var report = ReadReport(failed.Error, "discover");
        var items = report.GetProperty("Modules").EnumerateArray().ToArray();
        Assert.Equal(failed.Loads.Reverse().Select(load => load.Path), items.Select(item => item.GetProperty("Path").GetString()));
        Assert.Equal(failed.Loads.Reverse().Select(load => load.Path), failed.UnloadOrder);
        foreach (var item in items)
        {
            Assert.Equal("Collectible", item.GetProperty("CodeLifetime").GetString());
            Assert.Equal("UnloadRequested", item.GetProperty("Cleanup").GetString());
        }
        Assert.All(failed.Loads, AssertCollected);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IPlugin).Assembly));
        GC.KeepAlive(failed.Error);
        GC.KeepAlive(report);
    }

    /// <summary>A throwing unload callback is reported without replacing the discovery error or abandoning the remaining modules.</summary>
    [Fact]
    public void UnloadCallbackFailureDoesNotReplacePrimaryFailureOrSkipEarlierModules()
    {
        var failed = CaptureDiscoveryFailure(throwDuringUnload: true);
        Assert.IsType<ConfigException>(failed.Error);
        Assert.Contains("Duplicate", failed.Error.Message);
        Assert.DoesNotContain("fixture unload callback failure", failed.Error.Message);
        Assert.Equal(2, failed.Loads.Length);
        Assert.Equal(failed.Loads.Reverse().Select(load => load.Path), failed.UnloadOrder);
        var report = ReadReport(failed.Error, "discover");
        var items = report.GetProperty("Modules").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        AssertModule(items[0], failed.Loads[1].Path, "Collectible", "CleanupFailed", restartRequired: true);
        Assert.Contains(nameof(InvalidOperationException), items[0].GetProperty("CleanupErrorType").GetString()!);
        AssertModule(items[1], failed.Loads[0].Path, "Collectible", "UnloadRequested", restartRequired: false);
        AssertCollected(failed.Loads[0]);
        GC.KeepAlive(failed.Error);
    }

    /// <summary>Returning no context leaves an explicit creation-stage diagnostic rather than claiming an unload occurred.</summary>
    [Fact]
    public void NullContextFactoryReportsContextNotReturned()
    {
        var path = FixturePath();
        var error = Record.Exception(() => PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!, static (_, _) => null!));
        Assert.NotNull(error);
        var report = ReadReport(error, "load-module");
        var item = Assert.Single(report.GetProperty("Modules").EnumerateArray());
        AssertModule(item, path, "Unknown", "ContextNotReturned", restartRequired: null);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("CleanupErrorType").ValueKind);
    }

    /// <summary>A factory exception is rethrown by identity with its original throw frame and an unattached-context diagnostic.</summary>
    [Fact]
    public void ThrowingContextFactoryPreservesOriginalExceptionIdentityAndStack()
    {
        var path = FixturePath();
        var original = new ConfigException("fixture context factory failure");
        var error = Assert.Throws<ConfigException>(() => PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!,
            (_, _) => ThrowFactory(original)));
        Assert.Same(original, error);
        Assert.Contains(nameof(ThrowFactory), error.StackTrace!);
        var item = Assert.Single(ReadReport(error, "load-module").GetProperty("Modules").EnumerateArray());
        AssertModule(item, path, "Unknown", "ContextNotReturned", restartRequired: null);
    }

    /// <summary>Failure diagnostics do not call virtual exception Data or require its dictionary to be writable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactoryExceptionDataCannotMaskTheOriginalFailure(bool throwingData)
    {
        var path = FixturePath();
        Exception original = throwingData ? new ThrowingDataException() : new ReadOnlyDataException();
        var error = Record.Exception(() => PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!,
            (_, _) => ThrowFactory(original)));
        Assert.Same(original, error);
        Assert.NotNull(error);
        Assert.Contains(nameof(ThrowFactory), error.StackTrace!);
        var item = Assert.Single(ReadReport(error, "load-module").GetProperty("Modules").EnumerateArray());
        AssertModule(item, path, "Unknown", "ContextNotReturned", restartRequired: null);
    }

    /// <summary>A later roots-enumerator failure retains its exact IOException and rolls back contexts already acquired from an earlier root.</summary>
    [Fact]
    public void RootEnumerationFailureRestoresDiscoveryPhaseAndUnloadsAcquiredModules()
    {
        var original = new IOException("fixture roots enumeration failure");
        var failed = CaptureEnumerationFailure(original);
        Assert.Same(original, failed.Error);
        Assert.Contains(nameof(RootsThenThrow), failed.Error.StackTrace!);
        Assert.Equal(2, failed.Loads.Length);
        var report = ReadReport(failed.Error, "discover");
        Assert.Equal("module-discovery", report.GetProperty("FailurePhase").GetString());
        var items = report.GetProperty("Modules").EnumerateArray().ToArray();
        Assert.Equal(failed.Loads.Reverse().Select(load => load.Path), items.Select(item => item.GetProperty("Path").GetString()));
        Assert.All(items, item => Assert.Equal("UnloadRequested", item.GetProperty("Cleanup").GetString()));
        Assert.All(failed.Loads, AssertCollected);
        var wrapper = new InvalidOperationException("lifecycle wrapper", original);
        Assert.Equal(PluginCatalog.GetLoadFailureDiagnostics(original), PluginCatalog.GetLoadFailureDiagnostics(wrapper));
        GC.KeepAlive(original);
        GC.KeepAlive(wrapper);
    }

    /// <summary>The public native loader keeps its process-lifetime default and reports retained code without claiming collection.</summary>
    [Fact]
    public void DefaultFailedSelectionReportsProcessLifetimeRetention()
    {
        var path = FixturePath();
        var error = Assert.Throws<ConfigException>(() => PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, MissingId));
        var item = Assert.Single(ReadReport(error, "load-definition").GetProperty("Modules").EnumerateArray());
        AssertModule(item, path, "ProcessLifetime", "ProcessLifetimeRetained", restartRequired: true);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("CleanupErrorType").ValueKind);
    }

    /// <summary>Path validation fails before a constructor can register either a collectible or process-lifetime context.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidConstructorPathDoesNotRegisterAnOrphanContext(bool collectible)
    {
        var name = "invalid-native-path-" + Guid.NewGuid().ToString("N");
        const string invalidPath = "invalid\0native.dll";
        Assert.Throws<ArgumentException>(() =>
        {
            if (collectible) _ = new PluginLoadContext(invalidPath, name, isCollectible: true);
            else _ = new PluginLoadContext(invalidPath, name);
        });
        Assert.DoesNotContain(AssemblyLoadContext.All, context => StringComparer.Ordinal.Equals(context.Name, name));
    }

    /// <summary>Successful module or id selection commits a real context anchor, which stays alive until the returned definitions are released.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulLoadDoesNotUnloadCommittedCode(bool selectOne)
    {
        var load = CommitThenRelease(selectOne);
        AssertCollected(load);
    }

    /// <summary>Unrelated exceptions have no native-load diagnostic report.</summary>
    [Fact]
    public void UnrelatedExceptionHasNoLoadFailureDiagnostics() =>
        Assert.Null(PluginCatalog.GetLoadFailureDiagnostics(new InvalidOperationException("unrelated")));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailureCapture CaptureEmptyModuleScan()
    {
        using var factory = new TrackingFactory();
        var path = typeof(IPlugin).Assembly.Location;
        Assert.True(File.Exists(path));
        var error = Assert.Throws<ConfigException>(() => PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!, factory.Create));
        return new(error, factory.Loads.ToArray(), factory.UnloadOrder.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailedRefreshCapture PrepareFailedNativeRefresh(NativeRefreshHarness harness)
    {
        harness.Host.StartAsync(default).AsTask().WaitAsync(Limit).GetAwaiter().GetResult();
        harness.Host.ReloadAsync(harness.Row.Id, default).AsTask().WaitAsync(Limit).GetAwaiter().GetResult();
        Assert.Equal(2, harness.Factory.Loads.Count);
        var committed = harness.Factory.Loads.Last();
        Assert.False(SameWeakTarget(harness.Factory.Loads.First().Assembly!, committed.Assembly!));
        Assert.True(SameWeakTarget(committed.Assembly!, harness.Logs.LastAssembly));
        var revision = Assert.Single(harness.Host.ModuleOwnership.Snapshot(), entry => entry.CatalogPins == 0).RevisionId;
        var error = Assert.Throws<InvalidOperationException>(() =>
            harness.Host.ReloadAsync(harness.Row.Id, default).AsTask().WaitAsync(Limit).GetAwaiter().GetResult());
        Assert.Equal(3, harness.Factory.Loads.Count);
        return new(committed, harness.Factory.Loads.Last(), error, revision);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecoverCommittedNativeConfiguration(NativeRefreshHarness harness)
    {
        var candidate = harness.Row with { Config = new Dictionary<string, object?> { ["round"] = 1 } };
        var result = harness.Host.ReconcileConfigurationAsync([candidate]).AsTask().WaitAsync(Limit).GetAwaiter().GetResult();
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SameWeakTarget(WeakReference first, WeakReference second)
    {
        var target = first.Target;
        return target is not null && ReferenceEquals(target, second.Target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailureCapture CaptureRejectedModule(string scenario)
    {
        using var factory = new TrackingFactory();
        var path = FixturePath(duplicate: scenario == "duplicate-ids");
        var error = Assert.Throws<ConfigException>(() =>
        {
            if (scenario == "missing-id")
                _ = PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, MissingId, factory.Create);
            else if (scenario == "exactly-one")
                _ = PluginCatalog.LoadDescriptor(path, Path.GetDirectoryName(path)!, factory.Create);
            else _ = PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!, factory.Create);
        });
        return new(error, factory.Loads.ToArray(), factory.UnloadOrder.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailureCapture CaptureDiscoveryFailure(bool throwDuringUnload)
    {
        using var factory = new TrackingFactory(throwDuringUnload ? 2 : 0);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-copies");
        Assert.True(File.Exists(Path.Combine(root, "left", "left.dll")));
        Assert.True(File.Exists(Path.Combine(root, "right", "right.dll")));
        var defaultUnloads = 0;
        void BorrowedUnloading(AssemblyLoadContext _) => Interlocked.Increment(ref defaultUnloads);
        AssemblyLoadContext.Default.Unloading += BorrowedUnloading;
        try
        {
            var error = Assert.Throws<ConfigException>(() => PluginCatalog.Discover([root], factory.Create, [typeof(IPlugin).Assembly]));
            Assert.Equal(0, Volatile.Read(ref defaultUnloads));
            return new(error, factory.Loads.ToArray(), factory.UnloadOrder.ToArray());
        }
        finally { AssemblyLoadContext.Default.Unloading -= BorrowedUnloading; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedLoad CommitThenRelease(bool selectOne)
    {
        using var factory = new TrackingFactory();
        var path = FixturePath();
        IReadOnlyList<PluginDescriptor> definitions = selectOne
            ? [PluginCatalog.LoadDefinition(path, Path.GetDirectoryName(path)!, AlphaId, factory.Create)]
            : PluginCatalog.LoadModule(path, Path.GetDirectoryName(path)!, factory.Create);
        Assert.Equal(selectOne ? 1 : 2, definitions.Count);
        var load = Assert.Single(factory.Loads);
        foreach (var definition in definitions)
        {
            Assert.Equal(PluginCodeLifetime.Collectible, definition.ModuleCode!.Lifetime);
            Assert.True(SameTarget(load.Context, definition.ModuleCode.LifetimeAnchor));
            Assert.True(SameTarget(load.Assembly!, definition.Type!.Assembly));
        }
        CollectFinite(4);
        Assert.True(IsAlive(load.Context));
        Assert.True(IsAlive(load.Assembly!));
        Assert.Equal(0, Volatile.Read(ref load.UnloadCount));
        Assert.Empty(factory.UnloadOrder);
        GC.KeepAlive(definitions);
        return load;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FailureCapture CaptureEnumerationFailure(IOException original)
    {
        using var factory = new TrackingFactory();
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-copies");
        Assert.True(File.Exists(Path.Combine(root, "left", "left.dll")));
        Assert.True(File.Exists(Path.Combine(root, "right", "right.dll")));
        var error = Assert.Throws<IOException>(() => PluginCatalog.Discover(RootsThenThrow(root, original), factory.Create, [typeof(IPlugin).Assembly]));
        return new(error, factory.Loads.ToArray(), factory.UnloadOrder.ToArray());
    }

    private static IEnumerable<string> RootsThenThrow(string root, IOException original)
    {
        yield return root;
        throw original;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginLoadContext ThrowFactory(Exception failure) => throw failure;

    private static JsonElement ReadReport(Exception error, string operation)
    {
        var json = PluginCatalog.GetLoadFailureDiagnostics(error);
        Assert.NotNull(json);
        using var parsed = JsonDocument.Parse(json);
        var report = parsed.RootElement.Clone();
        Assert.Equal(operation, report.GetProperty("Operation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("FailurePhase").GetString()));
        Assert.Equal(JsonValueKind.Array, report.GetProperty("Modules").ValueKind);
        foreach (var module in report.GetProperty("Modules").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(module.GetProperty("Stage").GetString()));
            Assert.All(module.EnumerateObject(), property =>
                Assert.True(property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False));
        }
        return report;
    }

    private static void AssertModule(JsonElement module, string path, string lifetime, string cleanup, bool? restartRequired)
    {
        Assert.Equal(Path.GetFullPath(path), module.GetProperty("Path").GetString());
        Assert.Equal(lifetime, module.GetProperty("CodeLifetime").GetString());
        Assert.Equal(cleanup, module.GetProperty("Cleanup").GetString());
        var capability = module.GetProperty("RestartRequiredForReclamation");
        if (restartRequired is { } expected) Assert.Equal(expected, capability.GetBoolean());
        else Assert.Equal(JsonValueKind.Null, capability.ValueKind);
    }

    private static string FixturePath(bool duplicate = false)
    {
        var path = duplicate
            ? Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition-duplicate", "DuplicateDefinitionModuleFixture.dll")
            : Path.Combine(AppContext.BaseDirectory, "fixtures", "multi-definition", "MultiDefinitionModuleFixture.dll");
        Assert.True(File.Exists(path), $"Build-only native fixture is missing: {path}");
        return path;
    }

    private static void AssertCollected(TrackedLoad load)
    {
        Assert.NotNull(load.Assembly);
        for (var attempt = 0; attempt < 32 && (IsAlive(load.Context) || IsAlive(load.Assembly)); attempt++)
            CollectFinite(1);
        Assert.False(IsAlive(load.Context));
        Assert.False(IsAlive(load.Assembly));
        Assert.True(Volatile.Read(ref load.UnloadCount) > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference reference) => reference.IsAlive;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SameTarget(WeakReference reference, object? candidate) =>
        candidate is not null && ReferenceEquals(reference.Target, candidate);

    private static void CollectFinite(int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Sleep(10);
        }
    }

    private sealed record FailureCapture(Exception Error, TrackedLoad[] Loads, string[] UnloadOrder);
    private sealed record FailedRefreshCapture(TrackedLoad Committed, TrackedLoad Rejected, InvalidOperationException Error, long CommittedRevision);

    private sealed class NativeRefreshHarness : IDisposable
    {
        private int _refreshes;
        internal TrackingFactory Factory { get; } = new();
        internal WeakAssemblyLogger Logs { get; } = new();
        internal PluginRow Row { get; } = new()
        {
            Id = "native-failure", Plugin = AlphaId, Source = "native-load-failure-test",
            Config = new Dictionary<string, object?> { ["round"] = 0 },
            Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 }
        };
        internal PluginHost Host { get; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal NativeRefreshHarness()
        {
            var path = FixturePath();
            var directory = Path.GetDirectoryName(path)!;
            var initial = PluginCatalog.LoadDefinition(path, directory, AlphaId, Factory.Create);
            var factory = new NativePluginExecutionFactory(initial.Type!, directory, path, loadDescriptor: RefreshDefinition);
            var descriptor = new PluginDescriptor(initial.Declaration, factory, directory, initial.Version, initial.ContractVersions)
            { Type = initial.Type, MainAssemblyPath = path };
            Host = new PluginHost([Row], PluginCatalog.FromDefinitions([descriptor]), Logs);
        }

        private PluginDescriptor RefreshDefinition(string path, string directory) =>
            PluginCatalog.LoadDefinition(path, directory, Interlocked.Increment(ref _refreshes) == 1 ? AlphaId : MissingId, Factory.Create);

        /// <summary>Joins real host shutdown and removes the temporary assembly observer even if the test failed.</summary>
        public void Dispose()
        {
            try { Host.ShutdownAsync(default).AsTask().WaitAsync(Limit).GetAwaiter().GetResult(); }
            finally { Factory.Dispose(); }
        }
    }

    private sealed class WeakAssemblyLogger : ILoggerFactory, ILogger
    {
        private WeakReference? _lastAssembly;
        internal WeakReference LastAssembly => Volatile.Read(ref _lastAssembly)
            ?? throw new InvalidOperationException("The native fixture has not reported its assembly.");
        /// <summary>Returns the bounded observer without retaining category-specific state.</summary>
        public ILogger CreateLogger(string categoryName) => this;
        /// <summary>Uses no external logging providers.</summary>
        public void AddProvider(ILoggerProvider provider) { }
        /// <summary>Owns no external logging resources.</summary>
        public void Dispose() { }
        /// <summary>Does not retain logging scope state.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Accepts fixture assembly observations.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>Retains only the latest weak assembly observation, never formatter, exception or log state roots.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            foreach (var pair in values)
                if (pair.Key == "FixtureAssembly" && pair.Value is Assembly assembly)
                    Volatile.Write(ref _lastAssembly, new WeakReference(assembly));
        }
    }

    private sealed class ThrowingDataException : Exception
    {
        /// <summary>Rejects Data access to expose diagnostic code that could otherwise mask the primary exception.</summary>
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("fixture Data getter failure");
    }

    private sealed class ReadOnlyDataException : Exception
    {
        private readonly System.Collections.IDictionary _data =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
        /// <summary>Exposes a read-only dictionary that diagnostic recording must not mutate.</summary>
        public override System.Collections.IDictionary Data => _data;
    }

    private sealed class TrackedLoad(string path, AssemblyLoadContext context)
    {
        internal string Path { get; } = path;
        internal WeakReference Context { get; } = new(context);
        internal WeakReference? Assembly;
        internal int UnloadCount;
        internal int ThrowOnce;
    }

    private sealed class TrackingFactory : IDisposable
    {
        private readonly string _prefix = "owned-load-test-" + Guid.NewGuid().ToString("N") + "-";
        private readonly ConcurrentDictionary<string, TrackedLoad> _contexts = new(StringComparer.Ordinal);
        private readonly int _throwOnUnload;
        private int _created;
        internal ConcurrentQueue<TrackedLoad> Loads { get; } = new();
        internal ConcurrentQueue<string> UnloadOrder { get; } = new();

        internal TrackingFactory(int throwOnUnload = 0)
        {
            _throwOnUnload = throwOnUnload;
            AppDomain.CurrentDomain.AssemblyLoad += ObserveAssembly;
        }

        internal PluginLoadContext Create(string path, string name)
        {
            var number = Interlocked.Increment(ref _created);
            var contextName = _prefix + number;
            var context = new PluginLoadContext(path, contextName, isCollectible: true);
            var load = new TrackedLoad(Path.GetFullPath(path), context)
            { ThrowOnce = number == _throwOnUnload ? 1 : 0 };
            Assert.True(_contexts.TryAdd(contextName, load));
            Loads.Enqueue(load);
            context.Unloading += _ =>
            {
                Interlocked.Increment(ref load.UnloadCount);
                UnloadOrder.Enqueue(load.Path);
                if (Interlocked.Exchange(ref load.ThrowOnce, 0) == 1)
                    throw new InvalidOperationException("fixture unload callback failure");
            };
            return context;
        }

        private void ObserveAssembly(object? sender, AssemblyLoadEventArgs args)
        {
            var context = AssemblyLoadContext.GetLoadContext(args.LoadedAssembly);
            if (context?.Name is not { } name || !name.StartsWith(_prefix, StringComparison.Ordinal) ||
                !_contexts.TryGetValue(name, out var load)) return;
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(args.LoadedAssembly.Location), load.Path))
                load.Assembly = new WeakReference(args.LoadedAssembly);
        }

        /// <summary>Removes the process-wide observer so it cannot retain the test's tracking state beyond its operation.</summary>
        public void Dispose() => AppDomain.CurrentDomain.AssemblyLoad -= ObserveAssembly;
    }
}
