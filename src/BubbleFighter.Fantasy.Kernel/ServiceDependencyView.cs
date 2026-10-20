using System.Collections.ObjectModel;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Declares how one activation may read a contract and whether an empty selection is allowed.</summary>
internal sealed record ServiceRequirement(Type Contract, ServiceCardinality Cardinality, bool Required);

/// <summary>Resolves declarations to fixed realm keys before selecting any provider values.</summary>
internal sealed class DeclaredServiceView
{
    private readonly (ServiceRequirement Requirement, ServiceKey Key)[] _declarations;

    internal DeclaredServiceView(ServiceScope scope, IReadOnlyList<ServiceRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Runtime = scope.Runtime;
        var copy = Runtime.CopyBounded(requirements);
        var contracts = new HashSet<Type>();
        _declarations = new (ServiceRequirement, ServiceKey)[copy.Length];
        for (var i = 0; i < copy.Length; i++)
        {
            var requirement = copy[i];
            ArgumentNullException.ThrowIfNull(requirement);
            ServiceRuntime.RequireContract(requirement.Contract);
            if (!Enum.IsDefined(requirement.Cardinality)) throw new ArgumentOutOfRangeException(nameof(requirements));
            if (!contracts.Add(requirement.Contract)) throw new InvalidOperationException("A service read contract may be declared only once.");
            _declarations[i] = (requirement, scope.Resolve(requirement.Contract));
        }
    }

    internal ServiceRuntime Runtime { get; }

    internal CommittedServiceView Commit(ServiceActivation consumer, ServiceBindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(snapshot);
        Runtime.RequireOwner(consumer.Runtime);
        Runtime.RequireOwner(snapshot.Runtime);
        var selections = new Dictionary<Type, ServiceSelection>();
        foreach (var (requirement, key) in _declarations)
        {
            var bindings = snapshot.Resolve(key);
            if (bindings.Count == 0 && requirement.Required) throw new ServiceUnavailableException(key.Contract);
            if (bindings.Any(binding => binding.Cardinality != requirement.Cardinality))
                throw new InvalidOperationException($"Service '{key.Contract.FullName}' does not match its declared read mode.");
            selections.Add(key.Contract, new ServiceSelection(requirement, key, bindings.ToArray()));
        }
        return new CommittedServiceView(consumer, selections);
    }
}

/// <summary>Freezes one declared contract's presence, ordering and provider revisions.</summary>
internal sealed class ServiceSelection(ServiceRequirement requirement, ServiceKey key, ServiceBinding[] bindings)
{
    internal ServiceRequirement Requirement { get; } = requirement;
    internal ServiceKey Key { get; } = key;
    internal ReadOnlyCollection<ServiceBinding> Bindings { get; } = Array.AsReadOnly((ServiceBinding[])bindings.Clone());

    internal bool Matches(IReadOnlyList<ServiceBinding> other)
    {
        if (Bindings.Count != other.Count) return false;
        for (var i = 0; i < Bindings.Count; i++)
            if (Bindings[i].Registration != other[i].Registration || Bindings[i].Revision != other[i].Revision) return false;
        return true;
    }
}

/// <summary>Reads only the provider values captured for one activation, including during its later cleanup.</summary>
internal sealed class CommittedServiceView
{
    private readonly ReadOnlyDictionary<Type, ServiceSelection> _selections;

    internal CommittedServiceView(ServiceActivation consumer, Dictionary<Type, ServiceSelection> selections)
    {
        Consumer = consumer;
        _selections = new ReadOnlyDictionary<Type, ServiceSelection>(new Dictionary<Type, ServiceSelection>(selections));
        Providers = Array.AsReadOnly(_selections.Values.SelectMany(selection => selection.Bindings)
            .SelectMany(binding => binding.Decorations.Select(layer => layer.Registration.Provider)
                .Prepend(binding.Registration.Provider)).Distinct().ToArray());
    }

    internal ServiceActivation Consumer { get; }
    internal ReadOnlyCollection<ServiceActivation> Providers { get; }
    internal IReadOnlyDictionary<Type, ServiceSelection> Selections => _selections;

    internal TService Get<TService>() where TService : class
    {
        if (TryGet<TService>(out var value)) return value!;
        throw new ServiceUnavailableException(typeof(TService));
    }

    internal bool TryGet<TService>(out TService? value) where TService : class
    {
        var selection = RequireSelection(typeof(TService), ServiceCardinality.Single);
        value = selection.Bindings.Count == 0 ? null : (TService)selection.Bindings[0].Instance;
        return value is not null;
    }

    internal IReadOnlyList<TService> GetAll<TService>() where TService : class
    {
        var selection = RequireSelection(typeof(TService), ServiceCardinality.Multiple);
        return Array.AsReadOnly(selection.Bindings.Select(binding => (TService)binding.Instance).ToArray());
    }

    internal bool Matches(ServiceBindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Consumer.Runtime.RequireOwner(snapshot.Runtime);
        return _selections.Values.All(selection => selection.Matches(snapshot.Resolve(selection.Key)));
    }

    internal bool HasSameDependencies(CommittedServiceView other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!ReferenceEquals(Consumer.Runtime, other.Consumer.Runtime) || _selections.Count != other._selections.Count) return false;
        foreach (var (contract, selection) in _selections)
        {
            if (!other._selections.TryGetValue(contract, out var next) || selection.Key != next.Key ||
                selection.Requirement != next.Requirement || !selection.Matches(next.Bindings)) return false;
        }
        return true;
    }

    private ServiceSelection RequireSelection(Type contract, ServiceCardinality cardinality)
    {
        ServiceRuntime.RequireContract(contract);
        if (!_selections.TryGetValue(contract, out var selection))
            throw new InvalidOperationException($"Service '{contract.FullName}' was not declared by activation '{Consumer.EntryId}'.");
        if (selection.Requirement.Cardinality != cardinality)
            throw new InvalidOperationException($"Service '{contract.FullName}' requires the declared {selection.Requirement.Cardinality} read operation.");
        return selection;
    }
}
