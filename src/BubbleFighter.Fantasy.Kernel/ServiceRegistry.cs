using BubbleFighter.Fantasy.Abstractions;
namespace BubbleFighter.Fantasy.Kernel;
/// <summary>Publishes interface services within realms using bounded metadata and atomic snapshots.</summary>
public sealed partial class ServiceRegistry : IServiceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<ServiceKey, Registration> _services = [];
    private readonly Dictionary<ServiceKey, List<Registration>> _multiple = [];
    private readonly Dictionary<ServiceKey, List<Waiter>> _waiters = [];
    private readonly ServiceActivation _rootOwner;
    private int _retained;
    /// <summary>Uses the standard activation capacity for a standalone registry.</summary>
    public ServiceRegistry() : this(new PluginLifecycleOptions().EffectCapacity) { }
    /// <summary>Sets the maximum retained registrations, layers, observers and preparations.</summary>
    public ServiceRegistry(int capacity)
    {
        Runtime = new ServiceRuntime(capacity);
        _rootOwner = Runtime.NewActivation("$registry");
    }
    internal ServiceRuntime Runtime { get; }
    internal Action<ServiceKey>? BindingChanged { get; set; }
    internal void SealOwner(ServiceActivation owner)
    {
        Runtime.RequireOwner(owner.Runtime);
        lock (_gate) owner.Publication.Seal();
    }
    internal int RetainedMetadataCount { get { lock (_gate) return _retained; } }
    internal ScopedServiceRegistry For(ServiceScope scope, ServiceActivation owner)
    {
        Runtime.RequireOwner(scope.Runtime);
        Runtime.RequireOwner(owner.Runtime);
        return new ScopedServiceRegistry(this, scope, owner);
    }
    /// <inheritdoc />
    public IDisposable Register<TService>(TService instance) where TService : class => Register(Runtime.RootScope, _rootOwner, instance);
    /// <inheritdoc />
    public IDisposable RegisterDecoratable<TService>(TService instance) where TService : class => RegisterDecoratable(Runtime.RootScope, _rootOwner, instance);
    /// <inheritdoc />
    public IDisposable Decorate<TService>(Func<TService, TService> factory, int priority = 0) where TService : class => Decorate(Runtime.RootScope, _rootOwner, factory, priority);
    /// <inheritdoc />
    public IDisposable RegisterMany<TService>(TService instance, int priority = 0) where TService : class => RegisterMany(Runtime.RootScope, _rootOwner, instance, priority);
    /// <inheritdoc />
    public IReadOnlyList<TService> GetAll<TService>() where TService : class => GetAll<TService>(Runtime.RootScope);
    /// <inheritdoc />
    public TService Get<TService>() where TService : class => Get<TService>(Runtime.RootScope);
    /// <inheritdoc />
    public bool TryGet<TService>(out TService? service) where TService : class => TryGet(Runtime.RootScope, out service);
    /// <inheritdoc />
    public IDisposable WhenAvailable<TService>(Action<TService> callback) where TService : class => WhenAvailable(Runtime.RootScope, callback);

    internal IDisposable Register<TService>(ServiceScope scope, ServiceActivation owner, TService instance) where TService : class => RegisterCore(scope, owner, instance, null);
    internal IDisposable RegisterDecoratable<TService>(ServiceScope scope, ServiceActivation owner, TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var chain = new DecoratableService<TService>(instance);
        return RegisterCore(scope, owner, (TService)chain.Reference, chain);
    }
    private IDisposable RegisterCore<TService>(ServiceScope scope, ServiceActivation owner, TService instance, IDecoratableService? chain) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var key = Key<TService>(scope);
        Registration registration;
        List<Waiter>? waiters;
        lock (_gate)
        {
            owner.Publication.EnsureOpen();
            if (_services.ContainsKey(key)) throw new InvalidOperationException($"Service '{key.Contract.FullName}' is already registered in realm {key.Realm.Id}.");
            RequireSingleMode(key);
            Reserve();
            try
            {
                var binding = ServiceBinding.Create(scope, owner, key.Contract, chain?.CaptureReference() ?? instance);
                registration = new Registration(this, key, instance, binding, chain) { Publishing = owner.Publication.IsReady };
                _services.Add(key, registration);
                if (owner.Publication.IsReady) _waiters.Remove(key, out waiters); else waiters = null;
            }
            catch { _retained--; throw; }
        }
        List<Exception>? failures = null;
        if (waiters is not null)
            foreach (var waiter in waiters)
            {
                try { waiter.Invoke(instance); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
        if (failures is not null)
        {
            registration.Dispose();
            throw new AggregateException($"Service '{key.Contract.FullName}' availability callback(s) failed; registration removed.", failures);
        }
        try
        {
            lock (_gate)
            {
                owner.Publication.EnsureOpen();
                registration.Publishing = false;
            }
        }
        catch { registration.Dispose(); throw; }
        if (owner.Publication.IsReady) BindingChanged?.Invoke(key);
        return registration;
    }
    internal IDisposable Decorate<TService>(ServiceScope scope, ServiceActivation owner, Func<TService, TService> factory, int priority) where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var key = Key<TService>(scope);
        Registration registration;
        DecoratableService<TService> chain;
        ServiceDecoration decoration;
        LayerRemoval removal;
        lock (_gate)
        {
            owner.Publication.EnsureOpen();
            RequireSingleMode(key);
            if (!_services.TryGetValue(key, out registration!)) throw new ServiceUnavailableException(key.Contract);
            if (!registration.BaseBinding.Registration.Provider.Publication.IsReady && registration.BaseBinding.Registration.Provider != owner)
                throw new ServiceUnavailableException(key.Contract);
            if (registration.Publishing) throw new InvalidOperationException("A service cannot be decorated until its availability callbacks settle.");
            if (registration.Chain is not DecoratableService<TService> selected)
                throw new InvalidOperationException($"Service '{key.Contract.FullName}' must opt in with RegisterDecoratable before it can be decorated.");
            chain = selected;
            Reserve();
            try { decoration = ServiceDecoration.Create(scope, owner, key.Contract, priority); }
            catch { _retained--; throw; }
        }
        try
        {
            // User factories execute before taking the publication lock.
            using var change = chain.PrepareAdd(factory, priority, decoration.Registration.Id, owner.Publication);
            lock (_gate)
            {
                owner.Publication.EnsureOpen();
                if (registration.Removed) throw new ServiceUnavailableException(key.Contract);
                var layers = registration.Layers.Values.Append(decoration).Where(layer => layer.Registration.Provider.Publication.IsReady).ToArray();
                var projection = owner.Publication.IsReady ? registration.Binding.WithDecorations(change.Reference, layers) : registration.Binding;
                change.Commit();
                registration.Layers.Add(decoration.Registration.Id, decoration);
                registration.Binding = projection;
                removal = new LayerRemoval(this, registration, decoration.Registration.Id);
            }
        }
        catch { lock (_gate) _retained--; throw; }
        if (owner.Publication.IsReady) BindingChanged?.Invoke(key);
        return removal;
    }
    internal IDisposable RegisterMany<TService>(ServiceScope scope, ServiceActivation owner, TService instance, int priority) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var key = Key<TService>(scope);
        Registration registration;
        lock (_gate)
        {
            owner.Publication.EnsureOpen();
            if (_services.ContainsKey(key) || _waiters.ContainsKey(key))
                throw new InvalidOperationException($"Service '{key.Contract.FullName}' is in singleton mode; RegisterMany cannot mix modes.");
            Reserve();
            try
            {
                var binding = ServiceBinding.Create(scope, owner, key.Contract, instance, ServiceCardinality.Multiple, priority);
                registration = new Registration(this, key, instance, binding);
                if (!_multiple.TryGetValue(key, out var entries)) _multiple[key] = entries = [];
                entries.Add(registration);
            }
            catch { _retained--; throw; }
        }
        if (owner.Publication.IsReady) BindingChanged?.Invoke(key);
        return registration;
    }
    internal IReadOnlyList<TService> GetAll<TService>(ServiceScope scope) where TService : class
    {
        var key = Key<TService>(scope);
        lock (_gate)
        {
            if ((_services.TryGetValue(key, out var single) && single.BaseBinding.Registration.Provider.Publication.IsReady) || _waiters.ContainsKey(key))
                throw new InvalidOperationException($"Service '{key.Contract.FullName}' is in singleton mode; use Get instead of GetAll.");
            return _multiple.TryGetValue(key, out var entries)
                ? entries.Where(entry => entry.BaseBinding.Registration.Provider.Publication.IsReady).OrderByDescending(entry => entry.Binding.Priority).ThenBy(entry => entry.Binding.Registration.Id).Select(entry => (TService)entry.Instance).ToArray() : [];
        }
    }
    internal TService Get<TService>(ServiceScope scope) where TService : class
    {
        if (TryGet<TService>(scope, out var value)) return value!;
        throw new ServiceUnavailableException(typeof(TService));
    }
    internal bool TryGet<TService>(ServiceScope scope, out TService? service) where TService : class
    {
        var key = Key<TService>(scope);
        lock (_gate)
        {
            if (_multiple.TryGetValue(key, out var multiple) && multiple.Any(entry => entry.BaseBinding.Registration.Provider.Publication.IsReady))
                throw new InvalidOperationException($"Service '{key.Contract.FullName}' is in multiple mode; use GetAll.");
            service = _services.TryGetValue(key, out var value) && value.BaseBinding.Registration.Provider.Publication.IsReady ? (TService)value.Instance : null;
            return service is not null;
        }
    }
    internal IDisposable WhenAvailable<TService>(ServiceScope scope, Action<TService> callback) where TService : class
    {
        ArgumentNullException.ThrowIfNull(callback);
        var key = Key<TService>(scope);
        Waiter waiter;
        object? existing = null;
        lock (_gate)
        {
            RequireSingleMode(key);
            Reserve();
            waiter = new Waiter(this, key, value => callback((TService)value));
            if (_services.TryGetValue(key, out var registration) && registration.BaseBinding.Registration.Provider.Publication.IsReady) existing = registration.Instance;
            else
            {
                if (!_waiters.TryGetValue(key, out var list)) _waiters[key] = list = [];
                list.Add(waiter);
            }
        }
        if (existing is not null) waiter.Invoke(existing);
        return waiter;
    }

    internal ServiceBindingSnapshot CaptureBindings(ServiceActivation? exclude = null, IReadOnlyCollection<Type>? bases = null)
    {
        var peers = exclude?.Definition;
        lock (_gate) return new ServiceBindingSnapshot(Runtime, AllRegistrations()
            .Where(entry => !entry.Publishing && entry.BaseBinding.Registration.Provider.Publication.IsReady && entry.BaseBinding.Registration.Provider != exclude &&
                (peers is null || entry.BaseBinding.Registration.Provider.Definition != peers))
            .Select(entry => bases?.Contains(entry.Key.Contract) == true ? entry.BaseBinding : entry.Binding).ToArray());
    }
    internal IReadOnlyList<ServiceBinding> OwnBindings(ServiceScope scope, ServiceActivation owner, Type contract)
    {
        Runtime.RequireOwner(scope.Runtime);
        Runtime.RequireOwner(owner.Runtime);
        var key = scope.Resolve(contract);
        lock (_gate) return AllRegistrations().Where(entry => entry.Key == key && entry.BaseBinding.Registration.Provider == owner)
            .Select(entry => entry.BaseBinding).OrderByDescending(binding => binding.Priority).ThenBy(binding => binding.Registration.Id).ToArray();
    }
    internal Type[] OwnedServices(ServiceActivation owner)
    {
        lock (_gate) return AllRegistrations().Where(entry => entry.BaseBinding.Registration.Provider == owner)
            .Select(entry => entry.Key.Contract).Distinct().ToArray();
    }
    internal Type[] OwnedDecorations(ServiceActivation owner)
    {
        lock (_gate) return _services.Values.SelectMany(entry => entry.Layers.Values)
            .Where(layer => layer.Registration.Provider == owner).Select(layer => layer.Registration.Key.Contract).Distinct().ToArray();
    }
    internal bool IsRegistered(Type type) => IsRegistered(Runtime.RootScope, type);
    internal bool IsRegistered(ServiceScope scope, Type type)
    {
        Runtime.RequireOwner(scope.Runtime);
        var key = scope.Resolve(type);
        lock (_gate) return (_services.TryGetValue(key, out var single) && !single.Publishing && single.BaseBinding.Registration.Provider.Publication.IsReady) ||
            (_multiple.TryGetValue(key, out var multiple) && multiple.Any(entry => entry.BaseBinding.Registration.Provider.Publication.IsReady));
    }
    internal IReadOnlyCollection<(Type Type, long Id)> Registrations
    {
        get { lock (_gate) return AllRegistrations().Select(entry => (entry.Key.Contract, entry.Binding.Registration.Id)).ToArray(); }
    }
    internal IReadOnlyCollection<(Type Type, long Id)> Decorations
    {
        get { lock (_gate) return _services.Values.SelectMany(entry => entry.Layers.Keys.Select(id => (entry.Key.Contract, id))).ToArray(); }
    }
    private IEnumerable<Registration> AllRegistrations() => _services.Values.Concat(_multiple.Values.SelectMany(entries => entries));
    private ServiceKey Key<TService>(ServiceScope scope)
    {
        Runtime.RequireOwner(scope.Runtime);
        return scope.Resolve(typeof(TService));
    }
    private void Reserve()
    {
        if (_retained == Runtime.Capacity) throw new InvalidOperationException($"Service metadata capacity of {Runtime.Capacity} is exhausted.");
        _retained++;
    }
    private void RequireSingleMode(ServiceKey key)
    {
        if (_multiple.ContainsKey(key))
            throw new InvalidOperationException($"Service '{key.Contract.FullName}' is in multiple mode; use RegisterMany/GetAll.");
    }
    private void RemoveRegistration(Registration registration)
    {
        var changed = false;
        lock (_gate)
        {
            if (registration.Removed) return;
            if (_services.TryGetValue(registration.Key, out var current) && ReferenceEquals(current, registration))
            {
                registration.Chain?.Close();
                _services.Remove(registration.Key);
                _retained -= 1 + registration.Layers.Count;
                registration.Layers.Clear();
                registration.Removed = true;
                changed = !registration.Publishing && registration.BaseBinding.Registration.Provider.Publication.IsReady;
            }
            else if (_multiple.TryGetValue(registration.Key, out var entries) && entries.Remove(registration))
            {
                if (entries.Count == 0) _multiple.Remove(registration.Key);
                registration.Removed = true;
                _retained--;
                changed = registration.BaseBinding.Registration.Provider.Publication.IsReady;
            }
        }
        if (changed) BindingChanged?.Invoke(registration.Key);
    }
    private void RemoveLayer(Registration registration, long id)
    {
        bool changed;
        lock (_gate)
        {
            if (registration.Removed || !registration.Layers.ContainsKey(id)) return;
            changed = registration.Layers[id].Registration.Provider.Publication.IsReady;
            using var change = registration.Chain!.PrepareRemove(id);
            var projection = changed ? registration.Binding.WithDecorations(change.Reference, registration.Layers.Values
                .Where(layer => layer.Registration.Id != id && layer.Registration.Provider.Publication.IsReady).ToArray()) : registration.Binding;
            change.Commit();
            registration.Binding = projection;
            registration.Layers.Remove(id);
            _retained--;
        }
        if (changed) BindingChanged?.Invoke(registration.Key);
    }
    private void RemoveWaiter(Waiter waiter)
    {
        if (!waiter.Retained) return;
        waiter.Retained = false;
        if (_waiters.TryGetValue(waiter.Key, out var list))
        {
            list.Remove(waiter);
            if (list.Count == 0) _waiters.Remove(waiter.Key);
        }
        _retained--;
    }
    private sealed class Registration(ServiceRegistry owner, ServiceKey key, object instance, ServiceBinding binding, IDecoratableService? chain = null) : IDisposable
    {
        public ServiceKey Key { get; } = key;
        public object Instance { get; } = instance;
        public IDecoratableService? Chain { get; } = chain;
        public ServiceBinding BaseBinding { get; } = binding;
        public ServiceBinding Binding { get; set; } = binding;
        public Dictionary<long, ServiceDecoration> Layers { get; } = [];
        public bool Publishing { get; set; }
        public bool Removed { get; set; }
        public void Dispose() => owner.RemoveRegistration(this);
    }
    private sealed class LayerRemoval(ServiceRegistry owner, Registration registration, long id) : IDisposable
    {
        public void Dispose() => owner.RemoveLayer(registration, id);
    }
    private sealed class Waiter(ServiceRegistry owner, ServiceKey key, Action<object> callback) : IDisposable
    {
        private Action<object>? _callback = callback;
        private bool _invoking;
        public ServiceKey Key { get; } = key;
        public bool Retained { get; set; } = true;
        public void Invoke(object instance)
        {
            Action<object>? invoke;
            lock (owner._gate)
            {
                invoke = _callback;
                if (invoke is null) return;
                _callback = null;
                _invoking = true;
            }
            try { invoke(instance); }
            finally { lock (owner._gate) { _invoking = false; owner.RemoveWaiter(this); } }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                _callback = null;
                if (!_invoking) owner.RemoveWaiter(this);
            }
        }
    }
}
