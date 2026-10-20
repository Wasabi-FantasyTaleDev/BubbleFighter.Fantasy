using System.ComponentModel.DataAnnotations;
using System.Runtime.ExceptionServices;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Owns one activation's synchronous and asynchronous effects in a bounded resource scope.</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly EffectScope _effects;
    private readonly ResourceOwner _resources;
    private Exception? _workFailure;
    private readonly object _closeGate = new();
    private Task? _unwind;
    private IDisposable? _dependencyLease;
    private EffectScope? _executionEffects;
    private IPluginExecution? _executionResource;
    // This reservation spans acquisition and retained live-row metadata; the host releases it after row removal.
    internal PluginModuleOwnership.Acquisition? ModuleOwnership { get; set; }
    private int _superseded;
    private int _closing;

    /// <summary>Creates an activation context from a detached row snapshot with validated lifecycle limits.</summary>
    /// <param name="row">The entry being activated.</param>
    /// <param name="services">The shared service registry.</param>
    /// <param name="events">The shared event bus.</param>
    /// <param name="logger">A logger named after the entry.</param>
    /// <param name="overrideTargets">The declared handler replacement targets.</param>
    public PluginContext(PluginRow row, IServiceRegistry services, IEventBus events, ILogger logger, IReadOnlyList<string>? overrideTargets = null)
        : this(row, services, events, logger, overrideTargets, captureSnapshot: true)
    {
    }

    internal PluginContext(PluginRow row, IServiceRegistry services, IEventBus events, ILogger logger, IReadOnlyList<string>? overrideTargets, bool captureSnapshot, ServiceScope? scope = null)
    {
        if (captureSnapshot) row = PluginRowSnapshot.Capture(row);
        Row = row;
        NativeServices = services switch
        {
            ServiceRegistry registry => registry.For(scope ?? registry.Runtime.RootScope, registry.Runtime.NewActivation(row.Id, row.Plugin)),
            ScopedServiceRegistry scoped => scoped.Registry.For(scoped.Scope, scoped.Registry.Runtime.NewActivation(row.Id, row.Plugin)),
            _ => null
        };
        services = NativeServices ?? services;
        Services = new OwnedServiceRegistry(this, services, events);
        Events = new OwnedEventBus(this, events);
        Logger = logger;
        Config = new PluginConfig(row, captureSnapshot: false);
        _effects = new EffectScope(row.Lifecycle.EffectCapacity);
        _resources = new ResourceOwner(_effects, () => WorkFailure, ReportWorkFailure);
        OverrideTargets = Array.AsReadOnly(overrideTargets?.ToArray() ?? []);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> OverrideTargets { get; }
    /// <inheritdoc />
    public PluginRow Row { get; }
    /// <inheritdoc />
    public IServiceRegistry Services { get; }
    /// <inheritdoc />
    public IEventBus Events { get; }
    /// <inheritdoc />
    public IPluginConfig Config { get; }
    /// <inheritdoc />
    public ILogger Logger { get; }
    /// <inheritdoc />
    public CancellationToken Lifetime => _effects.Lifetime;

    /// <summary>Installs a fixed declared dependency view before publishing any services. The host
    /// always calls this before plugin activation; low-level embedding code must supply its own declaration.</summary>
    public void BindServices(PluginAttribute declaration)
    {
        if (NativeServices is null) throw new NotSupportedException("Declared service access requires the native registry.");
        NativeServices.Bind(declaration);
        if (NativeServices.Activation.Publication.IsStaged &&
            !NativeServices.Registry.TryRetainDependencies(NativeServices.Access!, out _dependencyLease))
        {
            Supersede();
            throw new InvalidOperationException("Dependencies changed before their ownership could be retained.");
        }
    }
    internal ScopedServiceRegistry? NativeServices { get; }
    internal int RetainedEffectCount => _effects.TotalRetainedCount;
    internal int ResourceScopeCount => _effects.ScopeCount;
    internal int RetainedExecutionCount => Math.Max(Volatile.Read(ref _executionEffects)?.TotalRetainedCount ?? 0,
        Volatile.Read(ref _executionResource) is null ? 0 : 1);
    internal bool IsClosing => Volatile.Read(ref _closing) != 0 || _effects.IsClosing;
    internal bool IsSuperseded => Volatile.Read(ref _superseded) != 0;
    internal bool AcceptsCallbacks => !_effects.IsClosing && !IsSuperseded;
    internal void Supersede()
    {
        Volatile.Write(ref _superseded, 1);
        _effects.SealAdmission();
        Volatile.Read(ref _executionEffects)?.SealAdmission();
        if (NativeServices is { } native) native.Registry.SealOwner(native.Activation);
    }
    internal Exception? WorkFailure => Volatile.Read(ref _workFailure);
    internal Action<Exception>? WorkFailureObserver { get; set; }
    internal void EnsureCanUnwind()
    {
        _effects.EnsureCanClose();
        Volatile.Read(ref _executionEffects)?.EnsureCanClose();
    }

    internal async ValueTask<IPluginExecution> AcquireExecutionAsync(IPluginExecutionFactory factory, bool reload, CancellationToken ct)
    {
        ResourceOwner owner;
        lock (_closeGate)
        {
            ObjectDisposedException.ThrowIf(IsClosing || IsSuperseded, this);
            if (_executionEffects is not null) throw new InvalidOperationException("An activation already owns an execution acquisition.");
            var effects = new EffectScope(1);
            Volatile.Write(ref _executionEffects, effects);
            owner = new ResourceOwner(effects, () => WorkFailure, ReportWorkFailure);
        }
        IPluginExecution? execution = null;
        await owner.AcquireEffectAsync(async token =>
        {
            execution = await factory.CreateAsync(reload, token).ConfigureAwait(false);
            Volatile.Write(ref _executionResource, execution);
            return execution;
        }, ct).ConfigureAwait(false);
        return execution ?? throw new InvalidOperationException("The execution factory returned no execution.");
    }
    internal IDisposable EnterCallback() => _effects.EnterCallback();

    internal IDisposable OwnRegistration(Func<IDisposable> acquire) => _resources.OwnRegistration(acquire);

    internal IDisposable OwnCallbackRegistration(Func<OwnedCallbackRegistration, IDisposable> acquire)
    {
        var registration = new OwnedCallbackRegistration(this);
        var handle = _effects.AcquireAsync(_ =>
        {
            registration.Attach(acquire(registration));
            return ValueTask.FromResult<IAsyncDisposable>(registration);
        }).AsTask().GetAwaiter().GetResult();
        return _resources.Wrap(handle, registration);
    }

    /// <inheritdoc />
    public IDisposable Effect(IDisposable disposer) => _resources.Effect(disposer);

    /// <inheritdoc />
    public IDisposable Effect(Action dispose) => _resources.Effect(dispose);

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire) =>
        _resources.AcquireEffectAsync(acquire);

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken) =>
        _resources.AcquireEffectAsync(acquire, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IResourceScope> CreateResourceScopeAsync(string name) => _resources.CreateResourceScopeAsync(name);

    /// <inheritdoc />
    public ValueTask<IOwnedTask> RunTaskAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default) =>
        _resources.RunTaskAsync(name, work, cancellationToken);

    private void ReportWorkFailure(string name, Exception failure)
    {
        var diagnostic = new InvalidOperationException($"Owned task '{name}' failed in row '{Row.Id}'.", failure);
        Interlocked.CompareExchange(ref _workFailure, diagnostic, null);
        WorkFailureObserver?.Invoke(WorkFailure!);
        Logger.LogError(failure, "Owned task '{TaskName}' failed in row '{RowId}'; ownership remains until teardown.", name, Row.Id);
    }

    /// <summary>Closes admission and awaits the same teardown on every call. In-progress early
    /// releases are joined. Failures are aggregated after attempting every remaining effect.</summary>
    public ValueTask UnwindAsync()
    {
        EnsureCanUnwind();
        lock (_closeGate)
        {
            Volatile.Write(ref _closing, 1);
            return new(_unwind ??= UnwindCoreAsync());
        }
    }

    private async Task UnwindCoreAsync()
    {
        if (NativeServices is { } native) await native.Registry.RetireOwner(native.Activation).ConfigureAwait(false);
        Exception? drainedFailure = null;
        try { await _effects.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure) when (OwnedWorkException.IsDrained(failure)) { drainedFailure = failure; }
        try
        {
            if (Volatile.Read(ref _executionEffects) is { } executionEffects)
                await executionEffects.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _executionResource, null);
        }
        catch (Exception executionFailure) when (drainedFailure is not null)
        { throw new AggregateException(drainedFailure, executionFailure); }
        ReleaseDependencies();
        if (drainedFailure is not null) ExceptionDispatchInfo.Capture(drainedFailure).Throw();
    }

    private void ReleaseDependencies()
    {
        NativeServices?.RevokeReadView();
        Interlocked.Exchange(ref _dependencyLease, null)?.Dispose();
    }

    internal void ReportAvailabilityFailure(Exception error) =>
        ReportWorkFailure("availability", new InvalidOperationException($"An availability observer failed in row '{Row.Id}'.", error));

    /// <summary>Synchronously waits for complete teardown and propagates cleanup failures.
    /// Calling from an owned callback is rejected instead of waiting for itself.</summary>
    public void Unwind() => UnwindAsync().AsTask().GetAwaiter().GetResult();

    internal static void ValidateLifecycle(PluginRow row)
    {
        if (row.Lifecycle is null) throw new ConfigException($"Row '{row.Id}' lifecycle must not be null.");
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(row.Lifecycle, new ValidationContext(row.Lifecycle), results, true))
            throw new ConfigException(row.Id, results.Select(result => result.ErrorMessage!).ToArray());
    }

}
