using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises the host module ledger: generations advance only on explicit reloads and leases drain with their executions.</summary>
public sealed class PluginHostModuleLedgerTests
{
    private const string Shared = "module-shared";
    private const string Other = "module-other";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Provides the service contract both shared-key definitions publish.</summary>
    public interface IShared
    {
        /// <summary>Gets the value supplied by the running revision.</summary>
        int Number { get; }
    }

    /// <summary>Restarting one of two entries sharing a module advances its single generation once without touching the twin.</summary>
    [Fact]
    public async Task RestartingOneEntryAdvancesTheSharedGenerationOnce()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        // Startup activations are not intent executions, so the ledger opens records lazily.
        Assert.Empty(harness.Host.Modules.Snapshot());

        await harness.Restart("first");
        var after = Assert.Single(harness.Host.Modules.Snapshot());
        Assert.Equal(Shared, after.Key);
        Assert.Equal(2, after.Generation);
        Assert.Equal(0, after.Scheduled);
        Assert.Equal([false, true], harness.Definition(Shared + "-a").Acquisitions);
        Assert.All(harness.Definition(Shared + "-b").Acquisitions, reload => Assert.False(reload));
        Assert.Equal(4, harness.Host.Instances.Count(instance => instance.State == PluginState.Active));
    }

    /// <summary>Configuration-driven convergence acquires without refresh, so no generation can move.</summary>
    [Fact]
    public async Task ConfigurationConvergenceNeverAdvancesGenerations()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        for (var round = 1; round <= 3; round++)
        {
            var result = await harness.Host.ReconcileConfigurationAsync(harness.Candidate(round)).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
            Assert.All(harness.Definition(Shared + "-a").Acquisitions, reload => Assert.False(reload));
            Assert.All(harness.Definition(Shared + "-b").Acquisitions, reload => Assert.False(reload));
        }

        var entries = harness.Host.Modules.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(1, entry.Generation));
        Assert.All(entries, entry => Assert.Equal(0, entry.Scheduled));
    }

    /// <summary>A failed explicit reload leaves the generation in place and the whole restart fails even though recovery reloaded.</summary>
    [Fact]
    public async Task FailedReloadKeepsTheGenerationAndTheRetryAdvancesItOnce()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var attempts = 0;
        harness.Definition(Shared + "-a").Create = _ =>
        {
            // Only the restart's own reload fails; recovery skips failed rows (reader is recovered
            // instead), so the row stays failed until the explicit retry reloads successfully.
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("shared reload failed");
        };

        // The recovery failure surfaces as an aggregate, so assert any failure rather than a specific type.
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Restart("first").WaitAsync(Limit));
        var failed = Assert.Single(harness.Host.Modules.Snapshot());
        Assert.Equal(1, failed.Generation);
        Assert.Equal(PluginState.Failed, harness.Host.Instances.Single(instance => instance.RowId == "first").State);

        await harness.Restart("first");
        var retried = Assert.Single(harness.Host.Modules.Snapshot());
        Assert.Equal(2, retried.Generation);
        Assert.Equal(PluginState.Active, harness.Host.Instances.Single(instance => instance.RowId == "first").State);
    }

    /// <summary>Repeated restarts of one module keep a single record whose generation grows monotonically.</summary>
    [Fact]
    public async Task RepeatedRestartsGrowOneRecordMonotonically()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await harness.Restart("first");
            var entry = Assert.Single(harness.Host.Modules.Snapshot());
            Assert.Equal(1 + cycle, entry.Generation);
            Assert.Equal(0, entry.Scheduled);
        }
        Assert.Equal(4, harness.Definition(Shared + "-a").Acquisitions.Count);
    }

    /// <summary>The ledger records exactly the distinct keys of restarted definitions and nothing else.</summary>
    [Fact]
    public async Task RecordsStayBoundedByDistinctKeys()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.Empty(harness.Host.Modules.Snapshot());

        await harness.Restart("first");
        Assert.Equal([Shared], harness.Host.Modules.Snapshot().Select(entry => entry.Key).ToArray());
        await harness.Restart("other");
        var keys = harness.Host.Modules.Snapshot().Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Other, Shared], keys);
        Assert.Equal(2, harness.Host.Modules.Snapshot().Count);
    }

    /// <summary>Cancellation observed before the refresh claim preserves the original error and releases the staged activation.</summary>
    [Fact]
    public async Task CancellationBeforeReloadClaimDoesNotCompleteAnUnownedSlot()
    {
        using var loggers = new CallbackLoggerFactory();
        using var harness = new Harness(loggers);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var definition = harness.Definition(Shared + "-a");
        var registry = definition.Contexts.Single().NativeServices!.Registry;
        var initialMetadata = registry.RetainedMetadataCount;
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        loggers.Creating = category =>
        {
            if (category != "plugin:first" || cancelled) return;
            cancelled = true;
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Host.ReloadAsync("first", cancellation.Token).AsTask().WaitAsync(Limit));
        Assert.True(cancelled);
        Assert.Equal([false], definition.Acquisitions);
        Assert.Equal(PluginState.Failed, harness.Host.Instances.Single(instance => instance.RowId == "first").State);
        var cancelledModule = Assert.Single(harness.Host.Modules.Snapshot());
        Assert.Equal(1, cancelledModule.Generation);
        Assert.Equal(0, cancelledModule.Scheduled);

        var retry = await harness.Restart("first");
        Assert.Equal(PluginTransitionOutcome.Converged, retry.Outcome);
        Assert.Equal([false, true], definition.Acquisitions);
        Assert.Equal(4, harness.Host.Instances.Count(instance => instance.State == PluginState.Active));
        Assert.Equal(initialMetadata, registry.RetainedMetadataCount);
        Assert.Equal(2, Assert.Single(harness.Host.Modules.Snapshot()).Generation);
    }

    /// <summary>A rejected refresh cannot finish the reload slot already held by another owner.</summary>
    [Fact]
    public async Task RejectedReloadCannotCompleteAnotherOwnersSlot()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.True(harness.Host.Modules.TryBeginReload(Other));
        var released = false;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Host.ReloadAsync("other", default).AsTask().WaitAsync(Limit));
            Assert.Contains($"Module '{Other}' is already reloading", failure.Message);
            Assert.Equal([false], harness.Definition(Other + "-only").Acquisitions);
            Assert.False(harness.Host.Modules.TryBeginReload(Other));
            var rejected = Assert.Single(harness.Host.Modules.Snapshot());
            Assert.Equal(1, rejected.Generation);
            Assert.Equal(0, rejected.Scheduled);
            Assert.False(harness.Host.Modules.CompleteReload(Other, success: false));
            released = true;

            await harness.Host.ReloadAsync("other", default).AsTask().WaitAsync(Limit);
            Assert.Equal([false, true], harness.Definition(Other + "-only").Acquisitions);
            Assert.Equal(2, Assert.Single(harness.Host.Modules.Snapshot()).Generation);
            Assert.Equal(PluginState.Active, harness.Host.Instances.Single(instance => instance.RowId == "other").State);
        }
        finally
        {
            if (!released)
            {
                // Ensure this fixture releases either its original slot or the failed assertion's probe.
                _ = harness.Host.Modules.TryBeginReload(Other);
                harness.Host.Modules.CompleteReload(Other, success: false);
            }
        }
    }

    /// <summary>Every coordination key accepted by a descriptor remains usable by both lifecycle admission APIs.</summary>
    [Theory]
    [InlineData(513)]
    [InlineData(1024)]
    public async Task LongCoordinationKeysRemainUsableAfterStartup(int length)
    {
        var key = new string('k', length);
        using var harness = new Harness(otherKey: key);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        await harness.Host.ReloadAsync("other", default).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, (await harness.Restart("other")).Outcome);
        Assert.Equal(PluginTransitionOutcome.Converged, (await harness.Restart("first")).Outcome);

        Assert.Equal([false, true, true], harness.Definition(Other + "-only").Acquisitions);
        var modules = harness.Host.Modules.Snapshot();
        Assert.Equal(2, modules.Count);
        Assert.Equal(3, modules.Single(module => module.Key == key).Generation);
        Assert.Equal(2, modules.Single(module => module.Key == Shared).Generation);
        Assert.All(modules, module => Assert.Equal(0, module.Scheduled));
        Assert.Equal(4, harness.Host.Instances.Count(instance => instance.State == PluginState.Active));
    }

    /// <summary>A lease invariant failure is reported without leaving shutdown waiting forever for an already finished execution.</summary>
    [Fact]
    public async Task ScheduledLeaseInvariantFailureStillSettlesShutdown()
    {
        using var harness = new Harness();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Definition(Other + "-only").BeforeCreate = async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        };
        harness.Host.Modules.AddLease(Shared, LeaseKind.Scheduled);
        try
        {
            var reload = harness.Host.ReloadAsync("other", default).AsTask();
            await entered.Task.WaitAsync(Limit);
            var shutdown = harness.Host.ShutdownAsync(default).AsTask();
            Assert.False(shutdown.IsCompleted);
            await canceled.Task.WaitAsync(Limit);
            release.TrySetResult();

            var failure = await Assert.ThrowsAsync<AggregateException>(() => reload.WaitAsync(Limit));
            Assert.Contains(failure.Flatten().InnerExceptions, error => error is OperationCanceledException);
            Assert.Contains(failure.Flatten().InnerExceptions, error => error is InvalidOperationException &&
                error.Message == "Module ledger retains scheduled leases after every execution settled; an admission site skipped its release.");
            await shutdown.WaitAsync(Limit);
            Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Unloaded, instance.State));
            Assert.Equal(0, harness.Host.Modules.Snapshot().Single(module => module.Key == Other).Scheduled);
        }
        finally
        {
            release.TrySetResult();
            harness.Host.Modules.RemoveLease(Shared, LeaseKind.Scheduled);
        }
        Assert.All(harness.Host.Modules.Snapshot(), module => Assert.Equal(0, module.Scheduled));
    }

    private static PluginRow Row(string id, string plugin, int value = 0, string? parent = null, bool disabled = false, string[]? isolated = null) => new()
    {
        Id = id, Plugin = plugin, ParentId = parent, Disabled = disabled, Source = "module-ledger-test",
        ClientBuild = new ClientBuild("kr", 1256, 0), Lineage = ["initial-profile"],
        Config = new Dictionary<string, object?> { ["value"] = value },
        IsolatedServices = isolated ?? [],
        Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 }
    };

    private sealed record SharedValue(int Number) : IShared;

    private sealed class DefinitionProbe(string id, Type[]? provides = null, Type[]? requires = null, string? key = null)
    {
        internal string Id { get; } = id;
        internal string? CoordinationKey { get; } = key;
        internal PluginAttribute Declaration { get; } = new(id) { Provides = provides ?? [], Requires = requires ?? [] };
        internal ConcurrentQueue<bool> Acquisitions { get; } = new();
        internal ConcurrentQueue<PluginContext> Contexts { get; } = new();
        internal Action<CancellationToken>? Create;
        internal Func<CancellationToken, ValueTask>? BeforeCreate;
    }

    private sealed class Harness : IDisposable
    {
        internal PluginHost Host { get; }
        internal PluginRow[] Rows { get; }
        private readonly Dictionary<string, DefinitionProbe> _definitions = new(StringComparer.Ordinal);

        internal Harness(ILoggerFactory? loggerFactory = null, string? otherKey = null)
        {
            Rows =
            [
                Row("first", Shared + "-a", 11, isolated: [typeof(IShared).FullName!]),
                Row("second", Shared + "-b", 22, isolated: [typeof(IShared).FullName!]),
                Row("other", Other + "-only", 33, isolated: [typeof(IShared).FullName!]),
                Row("reader", "reader", parent: "first"),
            ];
            foreach (var definition in new[]
            {
                new DefinitionProbe(Shared + "-a", provides: [typeof(IShared)], key: Shared),
                new DefinitionProbe(Shared + "-b", provides: [typeof(IShared)], key: Shared),
                new DefinitionProbe(Other + "-only", provides: [typeof(IShared)], key: otherKey ?? Other),
                new DefinitionProbe("reader", requires: [typeof(IShared)]),
            })
                _definitions[definition.Id] = definition;
            var descriptors = _definitions.Values.Select(definition => new PluginDescriptor(definition.Declaration,
                new Factory(definition), "module-ledger-test", "1.0.0", Versions));
            Host = new PluginHost(Rows, PluginCatalog.FromDefinitions(descriptors), loggerFactory ?? NullLoggerFactory.Instance);
        }

        internal DefinitionProbe Definition(string id) => _definitions[id];

        internal Task<PluginTransitionResult> Restart(string rowId) =>
            Host.RequestState(rowId, enabled: true, restart: true).WaitAsync().AsTask().WaitAsync(Limit);

        internal PluginRow[] Candidate(int round) => Rows.Select(row => row with
        { Config = new Dictionary<string, object?> { ["value"] = round } }).ToArray();

        public void Dispose() => Host.ShutdownAsync(default).AsTask().WaitAsync(Limit).GetAwaiter().GetResult();
    }

    private sealed class Factory(DefinitionProbe definition) : IPluginExecutionFactory
    {
        /// <summary>Reports the shared backend coordination identity carried into the descriptor.</summary>
        public string? CoordinationKey => definition.CoordinationKey;
        /// <summary>Records the refresh request and returns a fresh backend execution.</summary>
        public async ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            definition.Acquisitions.Enqueue(reload);
            definition.Create?.Invoke(cancellationToken);
            if (definition.BeforeCreate is { } beforeCreate)
                await beforeCreate(cancellationToken).ConfigureAwait(false);
            return new Execution(definition);
        }
    }

    private sealed class Execution(DefinitionProbe definition) : IPluginExecution
    {
        /// <summary>Returns the backend declaration carried by this definition.</summary>
        public PluginAttribute Declaration => definition.Declaration;
        /// <summary>Reports the fixture release served by this execution.</summary>
        public string Version => "1.0.0";
        /// <summary>References the shared abstraction version loaded by this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => PluginHostModuleLedgerTests.Versions;
        /// <summary>Identifies the fixture as a backend execution without a native implementing class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Publishes the declared service so readers can bind to it.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            definition.Contexts.Enqueue((PluginContext)context);
            var value = context.Config.Bind<LedgerOptions>().Value;
            if (Declaration.Provides.Contains(typeof(IShared)))
                context.Effect(context.Services.Register<IShared>(new SharedValue(value)));
            return ValueTask.CompletedTask;
        }
        /// <summary>Closes the backend execution.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackLoggerFactory : ILoggerFactory
    {
        internal Action<string>? Creating;

        /// <summary>Invokes the fixture callback at the host's logger acquisition boundary.</summary>
        public ILogger CreateLogger(string categoryName)
        {
            Creating?.Invoke(categoryName);
            return NullLogger.Instance;
        }

        /// <summary>Accepts no providers because fixture loggers never retain log records.</summary>
        public void AddProvider(ILoggerProvider provider) { }

        /// <summary>Releases the optional callback owned by this fixture.</summary>
        public void Dispose() => Creating = null;
    }

    private sealed class LedgerOptions
    {
        /// <summary>Gets or sets the per-row value published by the fixture.</summary>
        public int Value { get; set; }
    }
}
