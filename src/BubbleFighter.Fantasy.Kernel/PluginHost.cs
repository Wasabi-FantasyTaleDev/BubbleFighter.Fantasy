using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// The kernel runtime: owns the service registry and event bus, activates plugin rows in
/// dependency order, and unwinds row effects on unload. It registers itself as
/// <see cref="IPluginHost"/> plus the registry and bus so every other capability arrives
/// through ordinary plugin rows. Desired-state requests coalesce and reconcile dependencies.
/// Immediate row operations share dependency reservations with desired-state requests and
/// reject conflicting entry; unload refuses live dependents, while reload cascades through them.
/// </summary>
/// <remarks>Activation stages registrations before publication so consumers cannot observe a
/// partly configured provider. Teardown keeps the activation and its dependencies owned until
/// cleanup settles; a timeout reports incomplete cleanup rather than declaring resources released.</remarks>
public sealed partial class PluginHost : IPluginHost
{
    private sealed class LiveRow(PluginRow row, PluginDescriptor descriptor, PluginContext context, Type[] requiredTypes, Type[] providedTypes, string[] requiredRows, long configurationRevision)
    {
        public long ConfigurationRevision { get; } = configurationRevision;
        public PluginRow Row { get; } = row;
        public PluginDescriptor Descriptor { get; } = descriptor;
        public PluginContext Context { get; } = context;
        public Type[] RequiredTypes { get; } = requiredTypes;
        public Type[] ProvidedTypes { get; } = providedTypes;
        public string[] RequiredRows { get; } = requiredRows;
        private readonly ServiceActivation[] _providers = context.NativeServices?.Access?.View.Providers.ToArray() ?? [];
        public bool DependsOn(LiveRow provider) => RequiredRows.Contains(provider.Row.Id) ||
            _providers.Contains(provider.Context.NativeServices!.Activation);
    }

    private readonly ServiceRegistry _registry;
    private readonly EventBus _events;
    private readonly HostReadiness _readiness = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private PluginCatalog _catalog => Volatile.Read(ref _configuration).Catalog;
    private readonly PluginRuntimeBudget _budget;
    private readonly PluginModules _modules;
    private readonly PluginModuleOwnership _moduleOwnership;
    private sealed record CommittedDefinition(PluginDescriptor Descriptor, IDisposable Lease, DefinitionSource? Source);
    private sealed record RetiringEntry(PluginRow Row, PluginRuntimeEntry Entry);
    private readonly ConcurrentDictionary<string, RetiringEntry> _retiring = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CommittedDefinition> _entryDefinitions = new(StringComparer.Ordinal);
    private ConfigurationSnapshot _configuration;
    private IReadOnlyList<PluginRow> _rows => Volatile.Read(ref _configuration).Rows;
    private readonly LiveRows _live = new();
    private readonly Dictionary<string, PluginInstanceInfo> _info = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _activationGenerations = new(StringComparer.Ordinal);
    private int _transitionBusy;

    private PluginDescriptor? DefinitionFor(PluginRow row)
    {
        lock (_intentGate)
        {
            var snapshot = _configuration;
            snapshot.Sources.TryGetValue(row.Plugin, out var source);
            if (source is { IsPublished: false }) return null;
            if (_entryDefinitions.TryGetValue(row.Id, out var current) && current.Descriptor.Id == row.Plugin &&
                ReferenceEquals(current.Source, source) && (source is null || source.IsPublished)) return current.Descriptor;
            return snapshot.Catalog.Find(row.Plugin);
        }
    }

    /// <summary>Reports coordination reservations and refresh counts, independently of code revision ownership.</summary>
    internal PluginModules Modules => _modules;

    /// <summary>Reports explicit code revision holders separately from coordination reservations.</summary>
    internal PluginModuleOwnership ModuleOwnership => _moduleOwnership;

    private ServiceScope ScopeFor(PluginRow row) =>
        Volatile.Read(ref _configuration).Tree.Entries.TryGetValue(row.Id, out var entry) ? entry.Scope :
        _retiring.TryGetValue(row.Id, out var retired) ? retired.Entry.Scope :
        throw new InvalidOperationException($"Unknown entry '{row.Id}'.");

    private PluginRow KnownRow(string id, bool allowRetiring) =>
        Volatile.Read(ref _configuration).ById.TryGetValue(id, out var row) ? row :
        allowRetiring && _retiring.TryGetValue(id, out var retired) ? retired.Row :
        throw new InvalidOperationException($"Unknown or retiring row '{id}'.");

    private bool EntryIsReady(string id)
    {
        var snapshot = Volatile.Read(ref _configuration);
        for (string? current = id; current is not null; current = snapshot.ById[current].ParentId)
        {
            if (!snapshot.ById.ContainsKey(current) || !snapshot.Tree.Entries[current].IsNamespaceReady) return false;
            var live = _live.FirstOrDefault(item => item.Row.Id == current);
            if (live is null || live.Context.IsClosing || live.Context.WorkFailure is not null ||
                live.ConfigurationRevision != snapshot.EntryRevisions[current]) return false;
        }
        return true;
    }

    private bool ParentIsReady(PluginRow row) => row.ParentId is null || EntryIsReady(row.ParentId);

    /// <summary>Creates the host.</summary>
    /// <param name="rows">The composed row list, including disabled rows. Configuration and lineage
    /// are captured before runtime resources are created; callers must not mutate input during capture.</param>
    /// <param name="catalog">The discovered plugin catalog.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public PluginHost(IReadOnlyList<PluginRow> rows, PluginCatalog catalog, ILoggerFactory loggerFactory)
        : this(new PluginStartupPlan(rows, Array.Empty<PluginRow>()), catalog, loggerFactory)
    {
    }

    /// <summary>Creates a runtime with fixed allocation for the complete startup plan and initially admits only its seed entries.</summary>
    /// <param name="startup">The immutable initial and deferred rows whose grants establish startup capacity.</param>
    /// <param name="catalog">Definitions required by initial entries; deferred definitions may be supplied by ordinary sources.</param>
    /// <param name="loggerFactory">The logging factory used by runtime contexts and diagnostics.</param>
    public PluginHost(PluginStartupPlan startup, PluginCatalog catalog, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var captured = startup.InitialRows.ToArray();
        _budget = PluginRuntimeBudget.Capture(startup.AllRows, catalog.Plugins.Count);
        _bootstrapDefinitionCount = catalog.Plugins.Count;
        _moduleOwnership = new PluginModuleOwnership(catalog.Plugins.ToArray(), _budget.EntryCapacity, _budget.DefinitionCapacity);
        // Startup definitions and source grants fix the key budget before any loader callback runs.
        _modules = new PluginModules(Math.Max(1, _budget.DefinitionCapacity));
        _registry = new ServiceRegistry(_budget.ServiceMetadataCapacity);
        var tree = PluginEntryTree.Build(captured, _registry.Runtime, catalog);
        _configuration = new(0, Array.AsReadOnly(captured), captured.ToDictionary(row => row.Id, _ => 0L, StringComparer.Ordinal), tree, catalog, new Dictionary<string, DefinitionSource>(StringComparer.Ordinal));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginHost>();
        _events = new EventBus(loggerFactory.CreateLogger<EventBus>());
        _registry.Register<IServiceRegistry>(_registry);
        _registry.Register<IEventBus>(_events);
        _registry.Register<IPluginHost>(this);
        _registry.Register<IHostReadiness>(_readiness);
        foreach (var row in _rows)
            _info[row.Id] = InstanceInfo(row, DefinitionFor(row), PluginState.Unloaded, null);
        InitializeBindingReconciliation();
    }

    /// <summary>Marks the host ready once its startup configuration converged with every enabled row active.</summary>
    /// <remarks>Called by the server command after startup; rows that admit new players read <see cref="IHostReadiness"/>.</remarks>
    public void MarkReady()
    {
        if (_readiness.Set()) _logger.LogInformation("Server is ready; launcher sign-in and launches are accepted.");
    }

    private sealed class HostReadiness : IHostReadiness
    {
        private int _ready;
        public bool IsReady => Volatile.Read(ref _ready) != 0;
        public bool Set() => Interlocked.Exchange(ref _ready, 1) == 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginInstanceInfo> Instances
    {
        get
        {
            lock (_intentGate)
            lock (_info)
                return _rows.Concat(_retiring.Values.OrderBy(entry => entry.Row.Id, StringComparer.Ordinal).Select(entry => entry.Row))
                    .Select(row => _info[row.Id] with
                    { IsRetiring = _retiring.ContainsKey(row.Id), CodeRefreshPolicy = row.Lifecycle.CodeRefreshPolicy }).ToArray();
        }
    }

    /// <summary>
    /// Activates every enabled row whose declared requirements are registered, repeating
    /// until no further progress can be made. Any row still pending then fails boot with a
    /// report naming the missing services and the rows that need them.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask StartAsync(CancellationToken ct)
    {
        EnterTransition(ct);
        var previous = _live.Select(live => live.Row.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            await StartCoreAsync(ct).ConfigureAwait(false);
            foreach (var live in _live) NoteLegacyTarget(live.Row.Id, true);
        }
        catch (Exception activationError)
        {
            // A failed boot must not leave listeners or service registrations from this attempt.
            var cleanupErrors = new List<Exception>();
            foreach (var live in _live.Where(live => !previous.Contains(live.Row.Id)).Reverse().ToArray())
            {
                if (live.Context.IsClosing) continue;
                try { await UnloadCoreAsync(live.Row.Id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            lock (_intentGate)
                _desiredRows.IntersectWith(previous.Concat(_pendingIntents.Values.Where(request => request.Enabled).Select(request => request.RowId)));
            if (cleanupErrors.Count > 0)
                throw new AggregateException("Startup and rollback did not complete cleanly.", new[] { activationError }.Concat(cleanupErrors));
            throw;
        }
        finally
        {
            ExitTransition();
        }
    }

    /// <inheritdoc />
    public async ValueTask UnloadAsync(string rowId, CancellationToken ct)
    {
        await RunCompatibilityAsync(rowId, false, false, async token =>
        {
            await UnloadCoreAsync(rowId, token).ConfigureAwait(false);
            NoteLegacyTarget(rowId, false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask LoadAsync(string rowId, CancellationToken ct)
    {
        await RunCompatibilityAsync(rowId, true, false, async token =>
        {
            var row = _rows.FirstOrDefault(r => r.Id == rowId)
                ?? throw new InvalidOperationException($"Unknown row '{rowId}'.");
            if (_live.Any(l => l.Row.Id == rowId))
                throw new InvalidOperationException($"Row '{rowId}' is already active.");
            var descriptor = DefinitionFor(row)
                ?? throw new ConfigException($"Row '{row.Id}' references unknown plugin '{row.Plugin}'.");
            await ReactivateAsync(row, descriptor, "load", token).ConfigureAwait(false);
            NoteLegacyTarget(rowId, true);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ReloadResult> ReloadAsync(string rowId, CancellationToken ct)
    {
        return await RunCompatibilityAsync(rowId, true, true, async token =>
        {
            var row = _rows.FirstOrDefault(r => r.Id == rowId)
                ?? throw new InvalidOperationException($"Unknown row '{rowId}'.");
            var live = _live.FirstOrDefault(l => l.Row.Id == rowId);

            var dependents = DependentsClosure(rowId);
            for (var i = dependents.Count - 1; i >= 0; i--)
                await UnloadCoreAsync(dependents[i].Row.Id, token).ConfigureAwait(false);
            if (live is not null)
                await UnloadCoreAsync(rowId, token).ConfigureAwait(false);

            var descriptor = live?.Descriptor ?? DefinitionFor(row)
                ?? throw new ConfigException($"Row '{row.Id}' references unknown plugin '{row.Plugin}'.");
            await ReactivateAsync(row, descriptor, "reload", token).ConfigureAwait(false);
            var reloaded = new List<string> { rowId };
            foreach (var dependent in dependents)
            {
                await ReactivateAsync(dependent.Row, dependent.Descriptor, "reload", token).ConfigureAwait(false);
                reloaded.Add(dependent.Row.Id);
            }
            NoteLegacyTarget(rowId, true);
            return new ReloadResult(reloaded);
        }, ct, static (result, changed) => new ReloadResult(result.ReloadedRowIds.Union(changed, StringComparer.Ordinal).ToArray())).ConfigureAwait(false);
    }

    private async ValueTask StartCoreAsync(CancellationToken ct)
    {
        RefuseUnsettledActivations();
        var pending = _rows.Where(r => !r.Disabled && !_live.Any(live => live.Row.Id == r.Id)).ToList();
        // A row that reserves definition sources (loader.process, loader.wasm) publishes plugins only once it is active,
        // so at startup a row bound to such a plugin waits as Pending, with its reason, until the source publishes it.
        var sourcesDeclared = pending.Any(r => r.Lifecycle.DefinitionSourceReserve > 0);
        var awaitingDefinitions = new List<PluginRow>();
        foreach (var row in pending.ToArray())
            if (DefinitionFor(row) is null)
            {
                if (!sourcesDeclared)
                    throw new ConfigException($"Row '{row.Id}' references unknown plugin '{row.Plugin}'.");
                lock (_info) _info[row.Id] = InstanceInfo(row, null, PluginState.Pending, $"Definition source for plugin '{row.Plugin}' is unavailable.");
                // Desired, so the binding reconciliation that a publication schedules activates it.
                NoteLegacyTarget(row.Id, true);
                pending.Remove(row);
                awaitingDefinitions.Add(row);
            }

        var progress = true;
        while (progress && (pending.Count > 0 || awaitingDefinitions.Count > 0))
        {
            progress = false;
            // A loader can publish the missing definition during this startup transition. Admit it now so
            // native dependents can resolve its services before boot decides their requirements are missing.
            foreach (var row in awaitingDefinitions.Where(row => DefinitionFor(row) is not null).ToArray())
            {
                awaitingDefinitions.Remove(row);
                pending.Add(row);
            }
            foreach (var row in pending.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (!NamespaceIsReady(row)) continue;
                var descriptor = DefinitionFor(row)!;
                var attribute = Metadata(descriptor);
                if (!PluginServiceDeclarations.Required(attribute).All(t => _registry.IsRegistered(ScopeFor(row), t)))
                    continue;
                if (PendingProviders(row, attribute, pending).Any())
                    continue;
                if (RequiredEntryRows(row, attribute).Any(id => !EntryIsReady(id)))
                    continue;
                await ActivateAsync(row, descriptor, ct).ConfigureAwait(false);
                pending.Remove(row);
                progress = true;
                // Publication can add providers or decorators for a later consumer. Rebuild the
                // pending definition set before selecting another row's immutable service view.
                break;
            }
        }

        if (pending.Count > 0)
        {
            var report = string.Join(Environment.NewLine, pending.Select(row =>
            {
                var attribute = Metadata(DefinitionFor(row)!);
                var missing = PluginServiceDeclarations.Required(attribute).Where(t => !_registry.IsRegistered(ScopeFor(row), t)).Select(t => t.FullName);
                var optional = PendingProviders(row, attribute, pending);
                var targets = RequiredEntryRows(row, attribute).Where(id => !EntryIsReady(id));
                return $"  row '{row.Id}' (plugin '{row.Plugin}') is missing: {string.Join(", ", missing)}; waiting for optional provider row(s): {string.Join(", ", optional)}; override target row(s): {string.Join(", ", targets)}; {NamespaceUnavailableReason(row)}";
            }));
            throw new InvalidOperationException($"Plugin activation stalled; the following rows have unsatisfiable service dependencies:{Environment.NewLine}{report}");
        }
    }

    private async ValueTask UnloadCoreAsync(string rowId, CancellationToken ct)
    {
        var live = _live.FirstOrDefault(l => l.Row.Id == rowId)
            ?? throw new InvalidOperationException($"Row '{rowId}' is not active.");
        // A consumer with incomplete cleanup still owns its provider even during rollback or shutdown.
        {
            var dependents = _live
                .Where(l => l.Row.Id != rowId && l.DependsOn(live))
                .Select(l => l.Row.Id)
                .ToList();
            if (dependents.Count > 0)
                throw new InvalidOperationException(
                    $"Row '{rowId}' cannot unload; active row(s) depend on its services: {string.Join(", ", dependents.Select(id => $"'{id}'"))}.");
        }
        live.Context.EnsureCanUnwind();
        using var backendOperation = await EnterSourceBackendCleanupAsync(live.Context, live.Descriptor.CoordinationKey, ct).ConfigureAwait(false);
        lock (_info)
            _info[rowId] = InstanceInfo(live.Row, live.Descriptor, PluginState.Unloading, null);
        var teardown = live.Context.UnwindAsync().AsTask();
        Exception? drainedWorkFailure = null;
        try
        {
            await teardown.WaitAsync(TimeSpan.FromMilliseconds(live.Row.Lifecycle.TeardownTimeoutMs), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!teardown.IsCompletedSuccessfully)
            {
                if (teardown.IsFaulted && OwnedWorkException.IsDrained(teardown.Exception!))
                {
                    drainedWorkFailure = teardown.Exception;
                }
                else
                {
                    var state = teardown.IsCompleted ? PluginState.CleanupFailed : PluginState.CleanupIncomplete;
                    var failure = teardown.Exception?.InnerException ?? ex;
                    live.Context.ModuleOwnership?.MarkRetainedFailure();
                    lock (_info) _info[rowId] = InstanceInfo(live.Row, live.Descriptor, state, failure.Message);
                    throw new InvalidOperationException($"Row '{rowId}' teardown is {state}; ownership and dependencies are retained.", failure);
                }
            }
        }
        _live.Remove(live);
        ReleaseDefinitionSource(live.Context);
        live.Context.ModuleOwnership?.Dispose();
        lock (_info)
            _info[rowId] = InstanceInfo(live.Row, live.Descriptor,
                drainedWorkFailure is null ? PluginState.Unloaded : PluginState.Failed, drainedWorkFailure?.Message);
        _logger.LogInformation("Row '{RowId}' unloaded.", rowId);
        if (drainedWorkFailure is not null)
            throw new InvalidOperationException($"Row '{rowId}' owned work failed; resource teardown completed.", drainedWorkFailure);
    }

    /// <summary>
    /// Returns every live row that transitively depends on services published by
    /// <paramref name="rowId"/>, in activation order.
    /// </summary>
    private List<LiveRow> DependentsClosure(string rowId)
    {
        var result = new List<LiveRow>();
        var target = _live.FirstOrDefault(l => l.Row.Id == rowId);
        if (target is null)
            return result;
        var included = new HashSet<string>(StringComparer.Ordinal) { rowId };

        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var live in _live)
            {
                if (included.Contains(live.Row.Id))
                    continue;
                if (_live.Any(provider => included.Contains(provider.Row.Id) && live.DependsOn(provider)))
                {
                    included.Add(live.Row.Id);
                    result.Add(live);
                    progress = true;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Re-activates a row that was unloaded: reloads the descriptor from disk when the
    /// plugin lives in an isolated load context, verifies required services, activates.
    /// </summary>
    private async ValueTask ReactivateAsync(PluginRow row, PluginDescriptor descriptor, string verb, CancellationToken ct, bool refreshBackend = true)
    {
        RequireNamespaceReady(row);
        if (refreshBackend) RequireCodeRefreshAllowed(row);
        RefuseUnsettledActivations();
        ValidateReservedDescriptor(row, descriptor);
        var metadata = Metadata(descriptor);
        var missingRows = RequiredEntryRows(row, metadata).Where(id => !EntryIsReady(id)).ToArray();
        if (missingRows.Length > 0)
            throw new InvalidOperationException($"Row '{row.Id}' cannot {verb}; missing override target row(s): {string.Join(", ", missingRows)}.");
        var missing = PluginServiceDeclarations.Required(metadata).Where(t => !_registry.IsRegistered(ScopeFor(row), t)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Row '{row.Id}' cannot {verb}; missing service(s): {string.Join(", ", missing.Select(t => t.FullName))}.");
        await ActivateAsync(row, descriptor, ct, reload: refreshBackend).ConfigureAwait(false);
    }

    private IEnumerable<string> PendingProviders(PluginRow consumer, PluginAttribute attribute, IEnumerable<PluginRow> pending)
    {
        var dependencies = PluginServiceDeclarations.Reads(attribute).Select(ScopeFor(consumer).Resolve).ToHashSet();
        var projected = PluginServiceDeclarations.ProjectedReads(attribute).Select(ScopeFor(consumer).Resolve).ToHashSet();
        // Instances of one definition are symmetric peers (one per client build in a multi-build
        // server); waiting for a sibling would always deadlock, so optional waits skip them.
        // A row whose definition is not yet published advertises nothing until its source publishes it.
        return pending.Where(row => row.Id != consumer.Id && row.Plugin != consumer.Plugin && DefinitionFor(row) is { } provider &&
            (AdvertisedServices(provider).Select(ScopeFor(row).Resolve).Any(dependencies.Contains) ||
             Metadata(provider).Decorates.Select(ScopeFor(row).Resolve).Any(projected.Contains))).Select(row => row.Id);
    }
    private static PluginAttribute Metadata(PluginDescriptor descriptor) =>
        descriptor.Declaration;

    private static IEnumerable<Type> AdvertisedServices(PluginDescriptor descriptor)
    {
        var attribute = Metadata(descriptor);
        return PluginServiceDeclarations.Publishes(attribute);
    }

    private void RefuseLateProvider(PluginRow row, PluginDescriptor descriptor, PluginContext context)
    {
        if (_runningIntent is { Compatibility: false }) return;
        var attribute = Metadata(descriptor);
        var scope = context.NativeServices!.Scope;
        var published = AdvertisedServices(descriptor).Select(scope.Resolve).ToHashSet();
        var decorated = attribute.Decorates.Select(scope.Resolve).ToHashSet();
        var consumers = _live.Where(live => live.Row.Id != row.Id && live.Row.Plugin != row.Plugin && live.Context.NativeServices!.Access is { } access &&
            access.View.Selections.Values.Any(selection => published.Contains(selection.Key) ||
                (decorated.Contains(selection.Key) && access.ReadsProjection(selection.Key.Contract))))
            .Select(live => live.Row.Id).ToArray();
        if (consumers.Length > 0)
            throw new InvalidOperationException($"Row '{row.Id}' would change services used by active row(s): {string.Join(", ", consumers)}. Unload those consumers before loading the provider, then load them again.");
    }
    private async ValueTask ActivateAsync(PluginRow row, PluginDescriptor descriptor, CancellationToken ct, bool reload = false)
    {
        RefuseUnsettledActivations();
        long configurationRevision;
        DefinitionSource? definitionSource;
        lock (_intentGate)
        {
            var snapshot = _configuration;
            if (!snapshot.ById.TryGetValue(row.Id, out var desired) || !ReferenceEquals(row, desired))
                throw new OperationCanceledException($"Row '{row.Id}' configuration was superseded or retired.", ct);
            if (!snapshot.Tree.Entries[row.Id].IsNamespaceReady) RequireNamespaceReady(row);
            configurationRevision = snapshot.EntryRevisions[row.Id];
            snapshot.Sources.TryGetValue(row.Plugin, out definitionSource);
            if (definitionSource is { IsPublished: false })
                throw new ConfigException($"Definition source for plugin '{row.Plugin}' has been revoked.");
            if (!ReferenceEquals(snapshot.Catalog.Find(row.Plugin), descriptor) &&
                !(_entryDefinitions.TryGetValue(row.Id, out var cached) && ReferenceEquals(cached.Descriptor, descriptor) && ReferenceEquals(cached.Source, definitionSource)))
                throw new InvalidOperationException("The selected definition belongs to an obsolete source revision.");
        }
        // A refresh counter measures successful resolution, not the revision held by every sibling.
        // Immutable code ownership is bound separately to the actual resolved descriptor.
        string? reloadKey = reload ? descriptor.CoordinationKey : null;
        bool reloadClaimed = false;
        var logger = _loggerFactory.CreateLogger($"plugin:{row.Id}");
        var attribute = Metadata(descriptor);
        var context = new PluginContext(row, _registry, _events, logger, attribute.Overrides, captureSnapshot: false, ScopeFor(row));
        _registry.StageOwner(context.NativeServices!.Activation);
        lock (_info) _activationGenerations[row.Id] = context.NativeServices!.Activation.Generation;
        context.WorkFailureObserver = error =>
        {
            lock (_info)
                if (_activationGenerations.TryGetValue(row.Id, out var generation) && generation == context.NativeServices!.Activation.Generation &&
                    _info.TryGetValue(row.Id, out var info) && info.State == PluginState.Active)
                    _info[row.Id] = InstanceInfo(row, descriptor, PluginState.WorkFailed, error.Message);
        };


        string[] requiredRows = row.ParentId is { } parentId ? [parentId] : [];
        IDisposable? candidateDefinitionLease = null;
        IDisposable? backendOperation = null;
        Func<IReadOnlyList<Exception>> announce;
        try
        {
            lock (_intentGate)
            {
                if (definitionSource is { IsPublished: false })
                    throw new ConfigException($"Definition source for plugin '{row.Plugin}' was revoked before acquisition.");
                if (!_configuration.ById.TryGetValue(row.Id, out var accepted) || !ReferenceEquals(accepted, row) ||
                    !_configuration.Tree.Entries[row.Id].IsNamespaceReady ||
                    !ReferenceEquals(context.NativeServices!.Scope, _configuration.Tree.Entries[row.Id].Scope))
                { context.Supersede(); throw new OperationCanceledException("Activation scope was superseded before acquisition.", ct); }
                _hostedContexts.Add(context);
                if (definitionSource is not null) _sourceReaders.Add(context, definitionSource);
            }
            lock (_intentGate)
                if (_runningIntent is { } intent)
                {
                    _activatingContext = context;
                    if (intent.IsSuperseded) context.Supersede();
                }
            ct.ThrowIfCancellationRequested();
            EnsureDefinitionSource(definitionSource, context);
            await AwaitRetiringDefinitionSourcesAsync(descriptor.CoordinationKey, definitionSource, ct).ConfigureAwait(false);
            EnsureDefinitionSource(definitionSource, context);
            if (context.IsSuperseded) throw new OperationCanceledException(ct);
            backendOperation = EnterSourceBackendOperation(context, descriptor.CoordinationKey);
            requiredRows = RequiredEntryRows(row, attribute);
            context.ModuleOwnership = _moduleOwnership.Reserve(row.Id);
            // The refresh slot is claimed inside the try so every statement after it is covered by
            // the catch's CompleteReload(success: false); nothing between claim and acquire can throw.
            if (reloadKey is { } reloadSlot)
            {
                if (!_modules.TryBeginReload(reloadSlot))
                    throw new InvalidOperationException($"Module '{reloadSlot}' is already reloading; row '{row.Id}' cannot start a second refresh.");
                reloadClaimed = true;
            }
            var execution = await context.AcquireExecutionAsync(descriptor.ExecutionFactory, reload, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            EnsureDefinitionSource(definitionSource, context);
            descriptor = descriptor.ResolveExecution(execution);
            context.ModuleOwnership.Bind(descriptor);
            if (reloadKey is { } reloadSlot2)
            {
                reloadClaimed = false;
                _modules.CompleteReload(reloadSlot2, success: true);
            }
            attribute = Metadata(descriptor);
            ValidateReservedDescriptor(row, descriptor);
            ValidateCompatibility(row, descriptor, attribute);
            requiredRows = RequiredEntryRows(row, attribute);
            RefuseLateProvider(row, descriptor, context);
            context.BindServices(attribute);
            foreach (var optional in attribute.Optional.Concat(attribute.OptionalMany))
                logger.LogInformation("Row '{RowId}' optional service '{Service}': {Availability}.", row.Id,
                    optional.FullName, _registry.IsRegistered(ScopeFor(row), optional) ? "available" : "unavailable");
            var requiredTypes = PluginServiceDeclarations.Required(attribute).Concat(attribute.Optional.Concat(attribute.OptionalMany).Where(type => _registry.IsRegistered(ScopeFor(row), type))).Distinct().ToArray();
            await execution.ActivateAsync(context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (context.IsSuperseded) throw new OperationCanceledException(ct);
            var providedTypes = _registry.OwnedServices(context.NativeServices!.Activation);
            var missing = PluginServiceDeclarations.MandatoryPublications(attribute).Except(providedTypes).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Declared service(s) were not published: {string.Join(", ", missing.Select(type => type.FullName))}.");
            var decorated = _registry.OwnedDecorations(context.NativeServices!.Activation);
            if (attribute.Decorates.Except(decorated).Any() || decorated.Except(attribute.Decorates).Any())
                throw new InvalidOperationException($"Installed decorator layers do not match the row's Decorates declaration: {string.Join(", ", decorated.Select(type => type.FullName))}.");
            foreach (var type in decorated)
                logger.LogInformation("Row '{RowId}' decorates '{Service}'.", row.Id, type.FullName);
            var overrides = _registry.TryGet<IMessageRouter>(out var messages)
                ? messages!.Handlers.Where(handler => handler.OwnerRowId == row.Id && handler.Overrides is not null)
                    .Select(handler => handler.Overrides!).Distinct(StringComparer.Ordinal).ToArray() : [];
            if (attribute.Overrides.Except(overrides, StringComparer.Ordinal).Any() || overrides.Except(attribute.Overrides, StringComparer.Ordinal).Any())
                throw new InvalidOperationException($"Installed handler replacements do not match row '{row.Id}' Overrides declarations.");
            foreach (var target in overrides) logger.LogInformation("Row '{RowId}' overrides '{Target}'.", row.Id, target);
            lock (_intentGate)
            {
                // A row removed from the accepted configuration must never commit: RetireOwner has
                // already withdrawn its selection, so a late activation supersedes instead.
                var settled = _configuration;
                if (definitionSource is { IsPublished: false } || context.IsSuperseded || _runningIntent is { IsSuperseded: true } ||
                    !settled.ById.TryGetValue(row.Id, out var desired) || !ReferenceEquals(desired, row) ||
                    settled.EntryRevisions[row.Id] != configurationRevision ||
                    !settled.Tree.Entries[row.Id].IsNamespaceReady ||
                    !ReferenceEquals(context.NativeServices!.Scope, settled.Tree.Entries[row.Id].Scope) ||
                    !ParentIsReady(row) || !context.NativeServices!.Access!.MatchesCurrent())
                { context.Supersede(); throw new OperationCanceledException(ct); }
                lock (_info)
                {
                    if (context.WorkFailure is { } failure)
                        throw new InvalidOperationException($"Row '{row.Id}' owned work failed during activation.", failure);
                    candidateDefinitionLease = context.ModuleOwnership.RetainDefinition();
                    announce = _registry.CommitOwner(context.NativeServices!.Activation);
                    _live.Add(new LiveRow(row, descriptor, context, requiredTypes, providedTypes, requiredRows, configurationRevision));
                    _entryDefinitions.TryGetValue(row.Id, out var previousDefinition);
                    _entryDefinitions[row.Id] = new CommittedDefinition(descriptor, candidateDefinitionLease, definitionSource);
                    candidateDefinitionLease = null;
                    previousDefinition?.Lease.Dispose();
                    _info[row.Id] = InstanceInfo(row, descriptor, PluginState.Active, null);
                }
                if (ReferenceEquals(_activatingContext, context)) _activatingContext = null;
            }
            foreach (var observerFailure in announce())
                logger.LogError(observerFailure, "An availability observer failed after row '{RowId}' committed.", row.Id);
            _logger.LogInformation("Row '{RowId}' activated (plugin '{Plugin}' version {Version}; contracts {Contracts}; builds {Builds}).",
                row.Id, row.Plugin, descriptor.Version, string.Join(", ", descriptor.ContractVersions.Select(pair => $"{pair.Key}={pair.Value}")),
                attribute.SupportedBuilds.Length == 0 ? "independent" : string.Join(", ", attribute.SupportedBuilds));
        }
        catch (Exception ex)
        {
            if (reloadClaimed && reloadKey is { } key)
            {
                // Accounting failures must not bypass the activation's owned rollback.
                try { _modules.CompleteReload(key, success: false); }
                catch (Exception accountingError) { ex = new AggregateException(ex, accountingError); }
            }
            var required = PluginServiceDeclarations.Required(attribute)
                .Concat(attribute.Optional.Concat(attribute.OptionalMany).Where(type => _registry.IsRegistered(ScopeFor(row), type))).Distinct().ToArray();
            // Rejected declarations never became bindings that existing consumers could retain.
            var provided = _registry.OwnedServices(context.NativeServices!.Activation);
            if (!_live.Any(live => ReferenceEquals(live.Context, context)))
                _live.Add(new LiveRow(row, descriptor, context, required, provided, requiredRows, configurationRevision));
            try { await UnloadCoreAsync(row.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                if (!_live.Any(live => live.Row.Id == row.Id))
                    throw new InvalidOperationException($"Activation of row '{row.Id}' failed; rollback completed with owned work errors.",
                        new AggregateException(ex, cleanupError));
                throw new InvalidOperationException($"Activation of row '{row.Id}' failed and rollback retained ownership.",
                    new AggregateException(ex, cleanupError));
            }
            lock (_info)
                _info[row.Id] = InstanceInfo(row, descriptor, context.IsSuperseded ? PluginState.Pending : PluginState.Failed, ex.Message);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw new InvalidOperationException($"Activation of row '{row.Id}' (plugin '{row.Plugin}') failed: {ex.Message}", ex);
        }
        finally
        {
            candidateDefinitionLease?.Dispose();
            backendOperation?.Dispose();
            lock (_intentGate)
                if (ReferenceEquals(_activatingContext, context)) _activatingContext = null;
        }
    }

    private static PluginInstanceInfo InstanceInfo(PluginRow row, PluginDescriptor? descriptor, PluginState state, string? error) =>
        new(row.Id, row.Plugin, row.Source, state, error)
        {
            CodeRefreshPolicy = row.Lifecycle.CodeRefreshPolicy,
            CodeLifetime = descriptor?.ModuleCode?.Lifetime ?? PluginCodeLifetime.Unknown,
            Version = descriptor?.Version,
            ContractVersions = descriptor?.ContractVersions ?? new Dictionary<string, string>(),
            SupportedBuilds = descriptor?.SupportedBuilds ?? [],
            Overrides = descriptor is null ? [] : Metadata(descriptor).Overrides,
        };

    private void EnterTransition(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_intentGate)
        {
            if (_transitionBusy != 0 || _runningExecutions.Count > 0)
                throw new InvalidOperationException("A plugin lifecycle transition is already in progress; use RequestState for deferred targets.");
            _transitionBusy = 1;
            _transitionFrame.Value = new();
        }
    }

    private void RefuseUnsettledActivations()
    {
        var unsettled = _live.Where(live => (Touches(live.Row.Id) ||
            (CurrentExecution is { } execution && live.Descriptor.CoordinationKey is { } key && execution.Modules.Contains(key))) &&
            (live.Context.IsClosing || live.Context.WorkFailure is not null)).Select(live => live.Row.Id).ToArray();
        if (unsettled.Length > 0)
            throw new InvalidOperationException($"Activation is refused while failed work or cleanup ownership remains: {string.Join(", ", unsettled)}.");
    }

    private string[] RequiredEntryRows(PluginRow row, PluginAttribute attribute)
    {
        if (attribute.Overrides.Length > 256 || attribute.Overrides.Distinct(StringComparer.Ordinal).Count() != attribute.Overrides.Length)
            throw new ConfigException($"Row '{row.Id}' Overrides must contain at most 256 distinct targets.");
        return attribute.Overrides.Select(target =>
        {
            var separator = target?.IndexOf(':') ?? -1;
            if (separator < 1 || separator == target!.Length - 1 || target.IndexOf(':', separator + 1) >= 0 || target.Length > 512)
                throw new ConfigException($"Row '{row.Id}' has invalid Overrides target '{target}'; expected rowId:fully.qualified.MessageType.");
            var id = target[..separator];
            if (id == row.Id) throw new ConfigException($"Row '{row.Id}' cannot declare an override of itself.");
            return id;
        }).Concat(row.ParentId is { } parent ? [parent] : [])
            .Concat(Volatile.Read(ref _configuration).Sources.TryGetValue(row.Plugin, out var source) ? [source.Owner.Row.Id] : [])
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateCompatibility(PluginRow row, PluginDescriptor descriptor, PluginAttribute attribute)
    {
        var version = PluginVersion.Parse(descriptor.Version);
        if (row.Version is { } constraint && !version.Satisfies(constraint))
            throw new ConfigException($"Plugin '{descriptor.Id}' version {descriptor.Version} does not satisfy row '{row.Id}' constraint '{constraint}'.");
        if (attribute.SupportedFamilies.Length > 0)
        {
            if (row.Family is not { } family)
                throw new ConfigException($"Row '{row.Id}' requires a profile with a client build: plugin '{descriptor.Id}' serves the {string.Join(", ", attribute.SupportedFamilies)} family.");
            if (!attribute.SupportedFamilies.Contains(family, StringComparer.Ordinal))
                throw new ConfigException($"Row '{row.Id}' is in the {family} family; plugin '{descriptor.Id}' serves only {string.Join(", ", attribute.SupportedFamilies)}.");
        }
        if (attribute.SupportedBuilds.Length == 0)
            return;
        var ranges = attribute.SupportedBuilds.Select(Abstractions.Protocol.BuildRange.Parse).ToArray();
        if (row.ClientBuild is not { } build)
            throw new ConfigException($"Row '{row.Id}' requires an explicit client_build in its profile.");
        if (!ranges.Any(range => range.Contains(build)))
            throw new ConfigException($"Row '{row.Id}' does not support client build {build}; supported: {string.Join(", ", attribute.SupportedBuilds)}.");
    }
}
