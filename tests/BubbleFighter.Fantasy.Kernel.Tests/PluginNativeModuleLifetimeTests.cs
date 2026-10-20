using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises real native code lifetime across cache reuse and release with opt-in collectible test loaders.</summary>
public sealed class PluginNativeModuleLifetimeTests
{
    private const string PluginId = "test.module-ownership-native";
    private const string FixtureType = "BubbleFighter.Fantasy.TestFixtures.ModuleOwnership.NativeOwnershipPlugin";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>An unloaded execution cannot request assembly unload while its committed cache is needed by a later activation.</summary>
    [Fact]
    public void CollectibleCacheSurvivesConfigurationRestartAndManualUnloadUntilEntryRemoval()
    {
        using var harness = new Harness();
        var released = ExerciseCacheReuseAndRemove(harness);

        AssertCollected(released);
        GC.KeepAlive(harness);
    }

    /// <summary>Resolved factories retain the injected loader across repeated refreshes and release each obsolete native revision.</summary>
    [Fact]
    public void InjectedCollectibleLoaderSurvivesMultipleRefreshesWithoutRetainingObsoleteAssemblies()
    {
        using var harness = new Harness();
        Start(harness);
        LoadedModule? previous = null;
        for (var refresh = 0; refresh < 3; refresh++)
        {
            var current = Refresh(harness);
            Assert.Equal(refresh + 1, harness.Loader.Loads.Count);
            AssertRetained(current);
            Assert.True(SameLiveTarget(current.Assembly, harness.Logs.LastAssembly));
            if (previous is not null) AssertCollected(previous);
            previous = current;
        }

        RemoveEntry(harness);

        AssertCollected(previous!);
        Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(entry => entry.Activations));
        Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(entry => entry.DefinitionCaches));
        GC.KeepAlive(harness);
    }

    /// <summary>A collectible catalog reference stays alive after every entry owner is removed while that catalog remains owned.</summary>
    [Fact]
    public void CatalogPinKeepsCollectibleAssemblyAliveAfterEntryAndCacheRemoval()
    {
        using var harness = new Harness(collectibleBootstrap: true);
        var catalogModule = Assert.Single(harness.Loader.Loads);
        StartAndRemoveCatalogEntry(harness);

        CollectFinite(4);

        AssertRetained(catalogModule);
        var record = Assert.Single(harness.Host.ModuleOwnership.Snapshot());
        Assert.Equal(1, record.CatalogPins);
        Assert.Equal(0, record.Activations);
        Assert.Equal(0, record.DefinitionCaches);
        GC.KeepAlive(harness);
    }

    /// <summary>Failed native effect cleanup preserves the actual collectible context, executor and cache after entry retirement.</summary>
    [Fact]
    public void RetainedNativeCleanupKeepsCollectibleAssemblyAliveAfterEntryRemovalFails()
    {
        using var harness = new Harness();
        var retained = PrepareRetainedCleanup(harness);

        CollectFinite(4);

        AssertRetained(retained);
        var row = Assert.Single(harness.Host.Instances);
        Assert.True(row.IsRetiring);
        Assert.Equal(PluginState.CleanupFailed, row.State);
        var revision = harness.Host.ModuleOwnership.Snapshot().Single(entry => entry.CatalogPins == 0);
        Assert.Equal(1, revision.Activations);
        Assert.Equal(1, revision.DefinitionCaches);
        Assert.Equal(1, revision.RetainedFailures);
        Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
        GC.KeepAlive(harness);
    }

    /// <summary>The normal production loader remains noncollectible until a separately approved policy changes it.</summary>
    [Fact]
    public void DefaultNativeReloadPolicyRemainsNonCollectible()
    {
        using var harness = new Harness(injectLoader: false);
        Start(harness);
        ReloadDefault(harness);

        AssertObservationIsNonCollectible(harness.Logs.LastAssembly);
        Assert.Empty(harness.Loader.Loads);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedModule ExerciseCacheReuseAndRemove(Harness harness)
    {
        Start(harness);
        var module = Refresh(harness);
        AssertRetained(module);
        var configured = harness.Row with { Config = new Dictionary<string, object?> { ["round"] = 1 } };
        var result = Wait(harness.Host.ReconcileConfigurationAsync([configured]).AsTask());
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Single(harness.Loader.Loads);
        Assert.True(SameLiveTarget(module.Assembly, harness.Logs.LastAssembly));
        AssertRetained(module);

        Wait(harness.Host.UnloadAsync(harness.Row.Id, default).AsTask());
        Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(entry => entry.Activations));
        Assert.Equal(1, harness.Host.ModuleOwnership.Snapshot().Sum(entry => entry.DefinitionCaches));
        CollectFinite(4);
        AssertRetained(module);
        var disabled = Wait(harness.Host.ReconcileConfigurationAsync([configured with { Disabled = true }]).AsTask());
        Assert.Equal(PluginTransitionOutcome.Converged, disabled.Outcome);
        var enabled = Wait(harness.Host.ReconcileConfigurationAsync([configured with { Disabled = false }]).AsTask());
        Assert.Equal(PluginTransitionOutcome.Converged, enabled.Outcome);
        Assert.Single(harness.Loader.Loads);
        Assert.True(SameLiveTarget(module.Assembly, harness.Logs.LastAssembly));
        AssertRetained(module);
        RemoveEntry(harness);
        return module;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedModule PrepareRetainedCleanup(Harness harness)
    {
        Start(harness);
        var module = Refresh(harness);
        var configured = harness.Row with
        { Config = new Dictionary<string, object?> { ["round"] = 1, ["fail_cleanup"] = true } };
        var result = Wait(harness.Host.ReconcileConfigurationAsync([configured]).AsTask());
        harness.ExpectShutdownFailure = true;
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Single(harness.Loader.Loads);
        Assert.True(SameLiveTarget(module.Assembly, harness.Logs.LastAssembly));
        AssertRetained(module);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            Wait(harness.Host.ReconcileConfigurationAsync([]).AsTask()));

        Assert.Contains("native ownership fixture cleanup failure", failure.ToString());
        return module;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Start(Harness harness) => Wait(harness.Host.StartAsync(default).AsTask());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LoadedModule Refresh(Harness harness)
    {
        Wait(harness.Host.ReloadAsync(harness.Row.Id, default).AsTask());
        return harness.Loader.Loads.Last();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadDefault(Harness harness) => Wait(harness.Host.ReloadAsync(harness.Row.Id, default).AsTask());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveEntry(Harness harness)
    {
        var result = Wait(harness.Host.ReconcileConfigurationAsync([]).AsTask());
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Empty(harness.Host.Instances);
        Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartAndRemoveCatalogEntry(Harness harness)
    {
        Start(harness);
        Wait(harness.Host.ShutdownAsync(default).AsTask());
        RemoveEntry(harness);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Wait(Task task) => task.WaitAsync(Limit).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T Wait<T>(Task<T> task) => task.WaitAsync(Limit).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SameLiveTarget(WeakReference expected, WeakReference observed)
    {
        var target = expected.Target;
        return target is not null && ReferenceEquals(target, observed.Target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertObservationIsNonCollectible(WeakReference observation)
    {
        var assembly = Assert.IsAssignableFrom<Assembly>(observation.Target);
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        Assert.NotNull(context);
        Assert.False(context.IsCollectible);
    }

    private static void AssertRetained(LoadedModule module)
    {
        Assert.True(IsAlive(module.Context));
        Assert.True(IsAlive(module.Assembly));
        Assert.Equal(0, Volatile.Read(ref module.Unloading.Count));
    }

    private static void AssertCollected(LoadedModule module)
    {
        for (var attempt = 0; attempt < 32 &&
            (IsAlive(module.Context) || IsAlive(module.Assembly) || Volatile.Read(ref module.Unloading.Count) == 0); attempt++)
            CollectFinite(1);
        Assert.False(IsAlive(module.Context));
        Assert.False(IsAlive(module.Assembly));
        Assert.Equal(1, Volatile.Read(ref module.Unloading.Count));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference reference) => reference.IsAlive;

    private static void CollectFinite(int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            // Yield bounded time for runtime finalization; never synchronously wait for arbitrary finalizers.
            Thread.Sleep(10);
        }
    }

    private sealed class UnloadSignal
    {
        internal int Count;
    }

    private sealed class LoadedModule(AssemblyLoadContext context, Assembly assembly, UnloadSignal unloading)
    {
        internal WeakReference Context { get; } = new(context);
        internal WeakReference Assembly { get; } = new(assembly);
        internal UnloadSignal Unloading { get; } = unloading;
    }

    private sealed class CollectibleLoader
    {
        internal ConcurrentQueue<LoadedModule> Loads { get; } = new();
        internal PluginDescriptor Load(string path, string origin)
        {
            var context = new PluginLoadContext(path, "module-lifetime-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            var signal = new UnloadSignal();
            context.Unloading += _ => Interlocked.Increment(ref signal.Count);
            var assembly = context.LoadFromAssemblyPath(path);
            Loads.Enqueue(new LoadedModule(context, assembly, signal));
            var type = assembly.GetType(FixtureType, throwOnError: true)!;
            var declaration = type.GetCustomAttribute<PluginAttribute>()!;
            return new PluginDescriptor(declaration.Id, declaration.Description, type, origin, path);
        }
    }

    private sealed class Harness : IDisposable
    {
        internal PluginRow Row { get; } = new()
        {
            Id = "native", Plugin = PluginId, Source = "native-lifetime-test",
            Config = new Dictionary<string, object?>(), Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 }
        };
        internal CollectibleLoader Loader { get; } = new();
        internal WeakAssemblyLoggerFactory Logs { get; } = new();
        internal PluginHost Host { get; }
        internal bool ExpectShutdownFailure;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal Harness(bool collectibleBootstrap = false, bool injectLoader = true)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "module-ownership", "ModuleOwnershipFixture.dll");
            Assert.True(File.Exists(path), $"Build-only native fixture is missing: {path}");
            var origin = Path.GetDirectoryName(path)!;
            var initial = collectibleBootstrap ? Loader.Load(path, origin) : PluginCatalog.LoadDescriptor(path, origin);
            var descriptor = initial;
            if (injectLoader)
            {
                var factory = new NativePluginExecutionFactory(initial.Type!, origin, path, loadDescriptor: Loader.Load);
                descriptor = new PluginDescriptor(initial.Declaration, factory, origin, initial.Version, initial.ContractVersions)
                { Type = initial.Type, MainAssemblyPath = path };
            }
            Host = new PluginHost([Row], PluginCatalog.FromDefinitions([descriptor]), Logs);
        }
        /// <summary>Requires ordinary shutdown success except for the explicitly retained fixture cleanup failure.</summary>
        public void Dispose()
        {
            if (ExpectShutdownFailure)
            {
                var failure = Assert.Throws<AggregateException>(() => Wait(Host.ShutdownAsync(default).AsTask()));
                Assert.Contains("native ownership fixture cleanup failure", failure.ToString());
            }
            else Wait(Host.ShutdownAsync(default).AsTask());
        }
    }

    private sealed class WeakAssemblyLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<WeakReference> _assemblies = new();
        internal WeakReference LastAssembly => _assemblies.Last();
        /// <summary>Creates an observer that never retains fixture assemblies or their load contexts strongly.</summary>
        public ILogger CreateLogger(string categoryName) => new WeakAssemblyLogger(_assemblies);
        /// <summary>Uses no external logger providers.</summary>
        public void AddProvider(ILoggerProvider provider) { }
        /// <summary>Has no external resources to release.</summary>
        public void Dispose() { }
    }

    private sealed class WeakAssemblyLogger(ConcurrentQueue<WeakReference> assemblies) : ILogger
    {
        /// <summary>Does not retain state through logging scopes.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Accepts fixture information logs for weak assembly observations.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>Copies only a weak reference from a structured assembly log, never its state or formatter.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            foreach (var pair in values)
                if (pair.Key == "FixtureAssembly" && pair.Value is Assembly assembly)
                    assemblies.Enqueue(new WeakReference(assembly));
        }
    }
}
