using BubbleFighter.Fantasy.Abstractions;
namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Enforces one hosted activation's declared access without changing its committed external providers.</summary>
internal sealed class ActivationServiceAccess
{
    private readonly ScopedServiceRegistry _owner;
    private readonly Dictionary<Type, ServiceRequirement> _reads = [];
    private readonly Dictionary<Type, ServiceCardinality> _writes = [];
    private readonly HashSet<Type> _decorates = [];
    private readonly HashSet<Type> _observes = [];
    internal ActivationServiceAccess(ScopedServiceRegistry owner, PluginAttribute declaration)
    {
        _owner = owner;
        AddReads(declaration.Requires, ServiceCardinality.Single, true);
        AddReads(declaration.Optional, ServiceCardinality.Single, false);
        AddReads(declaration.RequiresMany, ServiceCardinality.Multiple, true);
        AddReads(declaration.OptionalMany, ServiceCardinality.Multiple, false);
        foreach (var type in Copy(declaration.Decorates))
        {
            if (!_decorates.Add(type)) throw new InvalidOperationException("Duplicate decoration declaration.");
            if (_reads.TryGetValue(type, out var read) && read.Cardinality != ServiceCardinality.Single)
                throw new InvalidOperationException("Decorated services require singleton access.");
            _reads[type] = new(type, ServiceCardinality.Single, true);
        }
        AddWrites(declaration.Provides, ServiceCardinality.Single);
        AddWrites(declaration.ConditionalProvides, ServiceCardinality.Single);
        AddWrites(declaration.ProvidesMany, ServiceCardinality.Multiple);
        AddWrites(declaration.ConditionalProvidesMany, ServiceCardinality.Multiple);
        foreach (var type in Copy(declaration.Observes))
        {
            if (!_observes.Add(type)) throw new InvalidOperationException("Duplicate observation declaration.");
            if ((_reads.TryGetValue(type, out var read) && read.Cardinality != ServiceCardinality.Single) ||
                (_writes.TryGetValue(type, out var mode) && mode != ServiceCardinality.Single))
                throw new InvalidOperationException("Availability observation requires singleton mode.");
        }
        View = new DeclaredServiceView(owner.Scope, _reads.Values.ToArray()).Commit(owner.Activation,
            owner.Registry.CaptureBindings(owner.Activation, _decorates));
    }
    internal CommittedServiceView View { get; }
    internal bool MatchesCurrent() => View.Matches(_owner.Registry.CaptureBindings(_owner.Activation, _decorates));
    internal bool ReadsProjection(Type contract) => _reads.ContainsKey(contract) && !_decorates.Contains(contract);
    internal void RequirePublish(Type contract, ServiceCardinality cardinality)
    {
        if (!_writes.TryGetValue(contract, out var declared) || declared != cardinality)
            throw new InvalidOperationException($"Activation '{_owner.Activation.EntryId}' did not declare {cardinality} publication of '{contract.FullName}'.");
    }
    internal void RequireDecoration(Type contract)
    {
        if (!_decorates.Contains(contract)) throw new InvalidOperationException($"Decoration of '{contract.FullName}' was not declared in Decorates.");
    }
    internal void RequireObservation(Type contract)
    {
        if (!_observes.Contains(contract)) throw new InvalidOperationException($"Observation of '{contract.FullName}' was not declared in Observes.");
    }
    internal bool TryGet<TService>(out TService? value) where TService : class
    {
        var type = typeof(TService);
        RequireRead(type, ServiceCardinality.Single);
        var own = _writes.ContainsKey(type) ? _owner.Registry.OwnBindings(_owner.Scope, _owner.Activation, type) : [];
        if (own.Count > 0) { value = (TService)own[0].Instance; return true; }
        if (_reads.ContainsKey(type)) return View.TryGet(out value);
        value = null;
        return false;
    }
    internal IReadOnlyList<TService> GetAll<TService>() where TService : class
    {
        var type = typeof(TService);
        RequireRead(type, ServiceCardinality.Multiple);
        IReadOnlyList<ServiceBinding> external = View.Selections.TryGetValue(type, out var selection) ? selection.Bindings : [];
        var own = _writes.ContainsKey(type) ? _owner.Registry.OwnBindings(_owner.Scope, _owner.Activation, type) : [];
        return Array.AsReadOnly(external.Concat(own).OrderByDescending(binding => binding.Priority)
            .ThenBy(binding => binding.Registration.Id).Select(binding => (TService)binding.Instance).ToArray());
    }
    private void RequireRead(Type type, ServiceCardinality cardinality)
    {
        var declared = _reads.TryGetValue(type, out var read) ? read.Cardinality :
            _writes.TryGetValue(type, out var mode) ? mode : (ServiceCardinality?)null;
        if (declared != cardinality)
            throw new InvalidOperationException($"Activation '{_owner.Activation.EntryId}' did not declare {cardinality} reads of '{type.FullName}'.");
    }
    private Type[] Copy(Type[]? types)
    {
        if (types is null) throw new InvalidOperationException("Service declarations must not be null.");
        var copy = _owner.Registry.Runtime.CopyBounded(types);
        foreach (var type in copy) ServiceRuntime.RequireContract(type);
        return copy;
    }
    private void AddReads(Type[]? types, ServiceCardinality cardinality, bool required)
    {
        foreach (var type in Copy(types))
            if (!_reads.TryAdd(type, new(type, cardinality, required)))
                throw new InvalidOperationException($"Duplicate read declaration for '{type.FullName}'.");
    }
    private void AddWrites(Type[]? types, ServiceCardinality cardinality)
    {
        foreach (var type in Copy(types))
        {
            if (!_writes.TryAdd(type, cardinality)) throw new InvalidOperationException($"Duplicate publication declaration for '{type.FullName}'.");
            if (_reads.TryGetValue(type, out var read) && read.Cardinality != cardinality)
                throw new InvalidOperationException($"Read and publication modes differ for '{type.FullName}'.");
        }
    }
}

/// <summary>Classifies declared service roles without knowing application contract families.</summary>
internal static class PluginServiceDeclarations
{
    internal static IEnumerable<Type> Required(PluginAttribute a) => a.Requires.Concat(a.RequiresMany).Concat(a.Decorates).Distinct();
    internal static IEnumerable<Type> Reads(PluginAttribute a) => Required(a).Concat(a.Optional).Concat(a.OptionalMany).Distinct();
    internal static IEnumerable<Type> ProjectedReads(PluginAttribute a) => Reads(a).Except(a.Decorates);
    internal static IEnumerable<Type> Publishes(PluginAttribute a) => a.Provides.Concat(a.ProvidesMany).Concat(a.ConditionalProvides).Concat(a.ConditionalProvidesMany).Distinct();
    internal static IEnumerable<Type> MandatoryPublications(PluginAttribute a) => a.Provides.Concat(a.ProvidesMany).Distinct();
}
