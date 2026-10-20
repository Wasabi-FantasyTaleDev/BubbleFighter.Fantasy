using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.TestFixtures.ExternalModuleSource;

/// <summary>Implements an attributed execution backend using only the published abstraction assembly.</summary>
public sealed class ExternalFactory(string id, PluginModuleCode? code) : IPluginExecutionFactory, IPluginModuleSource
{
    private readonly string _id = id;
    private PluginModuleCode? _code = code;
    private int _sourceReads;
    private int _activations;
    private int _disposals;
    private int _effectsDisposed;

    /// <summary>Gets the number of provenance snapshots requested by the host.</summary>
    public int SourceReads => Volatile.Read(ref _sourceReads);
    /// <summary>Gets the number of executions that entered activation.</summary>
    public int Activations => Volatile.Read(ref _activations);
    /// <summary>Gets the number of execution resources released by their owner.</summary>
    public int Disposals => Volatile.Read(ref _disposals);
    /// <summary>Gets the number of registered effects released before executor disposal.</summary>
    public int EffectsDisposed => Volatile.Read(ref _effectsDisposed);
    /// <summary>Gets whether execution disposal observed prior effect cleanup.</summary>
    public bool ExecutionDisposeSawEffectCleanup { get; private set; }
    /// <summary>Enables a failure if ledger inspection incorrectly reenters the backend snapshot getter.</summary>
    public bool ThrowOnSourceRead { get; set; }
    /// <summary>Replaces provenance during acquisition to exercise invalid factory drift and equivalent snapshot allocation.</summary>
    public PluginModuleCode? CodeOnAcquire { get; set; }
    /// <summary>Allows independent entry acquisition without a shared refresh lock.</summary>
    public string? CoordinationKey => null;
    /// <summary>Returns the configured provenance once per definition capture, including invalid values for boundary tests.</summary>
    public PluginModuleCode ModuleCode
    {
        get
        {
            Interlocked.Increment(ref _sourceReads);
            if (ThrowOnSourceRead) throw new InvalidOperationException("The provenance getter was unexpectedly reentered.");
            return Volatile.Read(ref _code)!;
        }
    }
    /// <summary>Creates an execution with no native implementation type.</summary>
    public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CodeOnAcquire is { } replacement) Volatile.Write(ref _code, replacement);
        return ValueTask.FromResult<IPluginExecution>(new Execution(this));
    }
    /// <summary>Creates an opaque identity whose equality methods must never be used by the host.</summary>
    public static object CreateHostileIdentity() => new HostileIdentity();

    private sealed class Execution(ExternalFactory owner) : IPluginExecution
    {
        public PluginAttribute Declaration => new(owner._id);
        public string Version => "1.0.0";
        public IReadOnlyDictionary<string, string> ContractVersions { get; } =
            new Dictionary<string, string> { [typeof(IPlugin).Assembly.GetName().Name!] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };
        public Type? ImplementationType => null;
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref owner._activations);
            context.Effect(new Effect(owner));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            owner.ExecutionDisposeSawEffectCleanup = Volatile.Read(ref owner._effectsDisposed) > 0;
            Interlocked.Increment(ref owner._disposals);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Effect(ExternalFactory owner) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref owner._effectsDisposed);
    }

    private sealed class HostileIdentity
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException("Revision equality must use reference identity.");
        public override int GetHashCode() => throw new InvalidOperationException("Revision hashing must use reference identity.");
    }
}
