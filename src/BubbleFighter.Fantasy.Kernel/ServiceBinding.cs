using System.Collections.ObjectModel;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Distinguishes singleton dependencies from explicitly ordered extension points.</summary>
internal enum ServiceCardinality { Single, Multiple }

/// <summary>Identifies one registration without comparing its implementation object.</summary>
internal sealed record ServiceRegistration
{
    private ServiceRegistration(ServiceKey key, ServiceActivation provider, long id)
    { Key = key; Provider = provider; Id = id; }
    internal ServiceKey Key { get; }
    internal ServiceActivation Provider { get; }
    internal long Id { get; }
    internal static ServiceRegistration Allocate(ServiceKey key, ServiceActivation provider)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(provider);
        key.Realm.Runtime.RequireOwner(provider.Runtime);
        return new ServiceRegistration(key, provider, provider.Runtime.NextIdentity());
    }
}

/// <summary>Records the owner and ordering of a layer in a captured service projection.</summary>
internal sealed class ServiceDecoration
{
    private ServiceDecoration(ServiceRegistration registration, int priority)
    {
        Registration = registration;
        Priority = priority;
    }

    internal ServiceRegistration Registration { get; }
    internal int Priority { get; }

    internal static ServiceDecoration Create(ServiceScope scope, ServiceActivation provider, Type contract, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(provider);
        scope.Runtime.RequireOwner(provider.Runtime);
        return new ServiceDecoration(ServiceRegistration.Allocate(scope.Resolve(contract), provider), priority);
    }
}

/// <summary>Captures one provider value and its ordered projection provenance. Values remain caller-owned.</summary>
internal sealed class ServiceBinding
{
    private ServiceBinding(ServiceRegistration registration, object instance, ServiceCardinality cardinality, int priority,
        long revision, ServiceDecoration[] decorations)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!registration.Key.Contract.IsInstanceOfType(instance))
            throw new ArgumentException("The value does not implement its declared service interface.", nameof(instance));
        if (!Enum.IsDefined(cardinality)) throw new ArgumentOutOfRangeException(nameof(cardinality));
        if (cardinality == ServiceCardinality.Multiple && decorations.Length != 0)
            throw new InvalidOperationException("Multiple registrations cannot use singleton decoration layers.");
        Registration = registration;
        Instance = instance;
        Cardinality = cardinality;
        Priority = priority;
        Revision = revision;
        Decorations = Array.AsReadOnly(decorations);
    }

    internal ServiceRegistration Registration { get; }
    internal object Instance { get; }
    internal ServiceCardinality Cardinality { get; }
    internal int Priority { get; }
    internal long Revision { get; }
    internal ReadOnlyCollection<ServiceDecoration> Decorations { get; }

    internal static ServiceBinding Create(ServiceScope scope, ServiceActivation provider, Type contract, object instance,
        ServiceCardinality cardinality = ServiceCardinality.Single, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(provider);
        scope.Runtime.RequireOwner(provider.Runtime);
        var registration = ServiceRegistration.Allocate(scope.Resolve(contract), provider);
        return new ServiceBinding(registration, instance, cardinality, priority, registration.Id, []);
    }

    internal ServiceBinding WithDecorations(object projectedValue, IReadOnlyList<ServiceDecoration> decorations)
    {
        var runtime = Registration.Provider.Runtime;
        var copy = runtime.CopyBounded(decorations);
        var ids = new HashSet<long> { Registration.Id };
        foreach (var layer in copy)
        {
            ArgumentNullException.ThrowIfNull(layer);
            if (layer.Registration.Key != Registration.Key)
                throw new InvalidOperationException("A decoration must belong to the same contract and realm as its base.");
            if (!ids.Add(layer.Registration.Id)) throw new InvalidOperationException("Duplicate decoration identity.");
        }
        if (copy.Length >= runtime.Capacity)
            throw new InvalidOperationException("A service and its decoration layers exceed metadata capacity.");
        var ordered = copy.OrderByDescending(layer => layer.Priority).ThenBy(layer => layer.Registration.Id).ToArray();
        return new ServiceBinding(Registration, projectedValue, Cardinality, Priority, runtime.NextIdentity(), ordered);
    }
}

/// <summary>Owns a consistent immutable table supplied by the publication backend, without invoking user code.</summary>
internal sealed class ServiceBindingSnapshot
{
    private readonly Dictionary<ServiceKey, ReadOnlyCollection<ServiceBinding>> _bindings;

    internal ServiceBindingSnapshot(ServiceRuntime runtime, IReadOnlyList<ServiceBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Runtime = runtime;
        var copy = runtime.CopyBounded(bindings);
        var identities = new HashSet<long>();
        foreach (var binding in copy)
        {
            ArgumentNullException.ThrowIfNull(binding);
            runtime.RequireOwner(binding.Registration.Provider.Runtime);
            runtime.RequireOwner(binding.Registration.Key.Realm.Runtime);
            if (!identities.Add(binding.Registration.Id))
                throw new InvalidOperationException("A registration occurs more than once in a service snapshot.");
            foreach (var layer in binding.Decorations)
                if (!identities.Add(layer.Registration.Id)) throw new InvalidOperationException("A decoration occurs more than once in a service snapshot.");
            if (identities.Count > runtime.Capacity)
                throw new InvalidOperationException("Service registrations and decoration layers exceed metadata capacity.");
        }
        _bindings = new Dictionary<ServiceKey, ReadOnlyCollection<ServiceBinding>>();
        foreach (var group in copy.GroupBy(binding => binding.Registration.Key))
        {
            var mode = group.First().Cardinality;
            if (group.Any(binding => binding.Cardinality != mode) || (mode == ServiceCardinality.Single && group.Count() != 1))
                throw new InvalidOperationException("Singleton and multiple registrations cannot conflict within one service realm.");
            _bindings.Add(group.Key, Array.AsReadOnly(group.OrderByDescending(binding => binding.Priority)
                .ThenBy(binding => binding.Registration.Id).ToArray()));
        }
    }

    internal ServiceRuntime Runtime { get; }
    internal IReadOnlyList<ServiceBinding> Resolve(ServiceKey key)
    {
        Runtime.RequireOwner(key.Realm.Runtime);
        return _bindings.TryGetValue(key, out var bindings) ? bindings : Array.Empty<ServiceBinding>();
    }
}
