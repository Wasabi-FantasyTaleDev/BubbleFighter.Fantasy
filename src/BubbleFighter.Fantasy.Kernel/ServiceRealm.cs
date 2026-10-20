using System.Collections.ObjectModel;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Allocates runtime-local identities without retaining a history of retired objects.</summary>
internal sealed class ServiceRuntime
{
    private readonly object _gate = new();
    private long _sequence;

    internal ServiceRuntime(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        RootRealm = ServiceRealm.Root(this);
        RootScope = new ServiceScope(this, new Dictionary<Type, ServiceRealm>());
    }

    internal Guid Id { get; } = Guid.NewGuid();
    internal int Capacity { get; }
    internal ServiceRealm RootRealm { get; }
    internal ServiceScope RootScope { get; }

    internal ServiceActivation NewActivation(string entryId, string? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return ServiceActivation.Allocate(this, entryId, definition);
    }

    internal long NextIdentity()
    {
        lock (_gate) return _sequence = checked(_sequence + 1);
    }

    internal void RequireOwner(ServiceRuntime runtime)
    {
        if (!ReferenceEquals(this, runtime))
            throw new InvalidOperationException("Service identities cannot cross runtime boundaries.");
    }

    internal static void RequireContract(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!contract.IsInterface || contract.ContainsGenericParameters)
            throw new ArgumentException($"Service contract '{contract}' must be a closed interface.", nameof(contract));
    }

    internal T[] CopyBounded<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var count = source.Count;
        if (count < 0 || count > Capacity)
            throw new InvalidOperationException($"Service metadata capacity of {Capacity} is exhausted.");
        var result = new T[count];
        for (var i = 0; i < count; i++) result[i] = source[i];
        return result;
    }
}

/// <summary>Identifies one entry activation; entry names alone never identify a provider instance.</summary>
internal sealed record ServiceActivation
{
    internal ActivationPublication Publication { get; } = new();
    private ServiceActivation(ServiceRuntime runtime, string entryId, long generation, string? definition)
    { Runtime = runtime; EntryId = entryId; Generation = generation; Definition = definition; }
    internal ServiceRuntime Runtime { get; }
    internal string EntryId { get; }
    internal long Generation { get; }
    // Activations of one definition are symmetric peers whose publications never enter each other's views.
    internal string? Definition { get; }
    internal static ServiceActivation Allocate(ServiceRuntime runtime, string entryId, string? definition = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return new ServiceActivation(runtime, entryId, runtime.NextIdentity(), definition);
    }
}

/// <summary>Revokes publication without changing an activation's value identity or releasing its resources.</summary>
internal sealed class ActivationPublication
{
    private int _sealed;
    private int _phase;
    internal bool IsReady => Volatile.Read(ref _phase) is 0 or 2;
    internal bool IsStaged => Volatile.Read(ref _phase) == 1;
    internal bool IsRetired => Volatile.Read(ref _phase) == 3;
    internal int Dependents;
    internal TaskCompletionSource? DependentsDrained;
    internal void Retire() { Seal(); Volatile.Write(ref _phase, 3); }
    internal void Begin()
    {
        EnsureOpen();
        if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0)
            throw new InvalidOperationException("Activation publication can be staged only once.");
    }
    internal void Commit()
    {
        EnsureOpen();
        if (Interlocked.CompareExchange(ref _phase, 2, 1) != 1)
            throw new InvalidOperationException("Activation publication is not staged.");
    }
    internal void Seal() => Volatile.Write(ref _sealed, 1);
    internal void EnsureOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _sealed) != 0, this);
}

/// <summary>Identifies a service namespace within one runtime.</summary>
internal sealed record ServiceRealm
{
    private ServiceRealm(ServiceRuntime runtime, long id) { Runtime = runtime; Id = id; }
    internal ServiceRuntime Runtime { get; }
    internal long Id { get; }
    internal static ServiceRealm Root(ServiceRuntime runtime) => new(runtime, 0);
    internal static ServiceRealm Allocate(ServiceRuntime runtime) => new(runtime, runtime.NextIdentity());
}

/// <summary>Combines the native contract interface and its selected service namespace.</summary>
internal sealed record ServiceKey
{
    internal ServiceKey(Type contract, ServiceRealm realm)
    {
        ServiceRuntime.RequireContract(contract);
        ArgumentNullException.ThrowIfNull(realm);
        Contract = contract; Realm = realm;
    }
    internal Type Contract { get; }
    internal ServiceRealm Realm { get; }
}

/// <summary>Resolves inherited service namespaces using an immutable, flattened isolation map.</summary>
internal sealed class ServiceScope
{
    private readonly ReadOnlyDictionary<Type, ServiceRealm> _realms;

    internal ServiceScope(ServiceRuntime runtime, Dictionary<Type, ServiceRealm> realms)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(realms);
        if (realms.Count > runtime.Capacity) throw new InvalidOperationException("Service isolation metadata exceeds capacity.");
        foreach (var (contract, realm) in realms)
        {
            ServiceRuntime.RequireContract(contract);
            ArgumentNullException.ThrowIfNull(realm);
            runtime.RequireOwner(realm.Runtime);
        }
        Runtime = runtime;
        _realms = new ReadOnlyDictionary<Type, ServiceRealm>(new Dictionary<Type, ServiceRealm>(realms));
    }

    internal ServiceRuntime Runtime { get; }

    internal ServiceKey Resolve(Type contract)
    {
        ServiceRuntime.RequireContract(contract);
        return new ServiceKey(contract, _realms.GetValueOrDefault(contract, Runtime.RootRealm));
    }

    internal ServiceScope CreateChild(IReadOnlyList<Type> isolate)
    {
        var contracts = Runtime.CopyBounded(isolate);
        var unique = new HashSet<Type>();
        foreach (var contract in contracts)
        {
            ServiceRuntime.RequireContract(contract);
            if (!unique.Add(contract)) throw new ArgumentException("A service may be isolated only once in a scope.", nameof(isolate));
        }
        var map = new Dictionary<Type, ServiceRealm>(_realms);
        if (map.Keys.Union(contracts).Count() > Runtime.Capacity)
            throw new InvalidOperationException("The inherited service isolation map exceeds capacity.");
        if (contracts.Length > 0)
        {
            var realm = ServiceRealm.Allocate(Runtime);
            foreach (var contract in contracts) map[contract] = realm;
        }
        return new ServiceScope(Runtime, map);
    }
}
