using BubbleFighter.Fantasy.Abstractions;
namespace BubbleFighter.Fantasy.Kernel;
/// <summary>Routes native service operations through one activation and its optional committed access policy.</summary>
internal sealed class ScopedServiceRegistry(ServiceRegistry registry, ServiceScope scope, ServiceActivation activation) : IServiceRegistry
{
    private ActivationServiceAccess? _access;
    private int _closed;
    internal ServiceRegistry Registry { get; } = registry;
    internal ServiceScope Scope { get; } = scope;
    internal ServiceActivation Activation { get; } = activation;
    internal ActivationServiceAccess? Access => Volatile.Read(ref _access);
    internal void RevokeReadView()
    {
        if (Access is null) return;
        Volatile.Write(ref _closed, 1);
        Interlocked.Exchange(ref _access, null);
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
    internal void Bind(PluginAttribute declaration)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(declaration);
        if (Access is not null || Registry.OwnedServices(Activation).Length != 0 || Registry.OwnedDecorations(Activation).Length != 0)
            throw new InvalidOperationException("Service access must be bound once, before publication.");
        var access = new ActivationServiceAccess(this, declaration);
        if (Interlocked.CompareExchange(ref _access, access, null) is not null)
            throw new InvalidOperationException("Service access is already bound.");
    }
    public IDisposable Register<TService>(TService instance) where TService : class
    { EnsureOpen(); Access?.RequirePublish(typeof(TService), ServiceCardinality.Single); return Registry.Register(Scope, Activation, instance); }
    public IDisposable RegisterDecoratable<TService>(TService instance) where TService : class
    { EnsureOpen(); Access?.RequirePublish(typeof(TService), ServiceCardinality.Single); return Registry.RegisterDecoratable(Scope, Activation, instance); }
    public IDisposable RegisterMany<TService>(TService instance, int priority = 0) where TService : class
    { EnsureOpen(); Access?.RequirePublish(typeof(TService), ServiceCardinality.Multiple); return Registry.RegisterMany(Scope, Activation, instance, priority); }
    public IDisposable Decorate<TService>(Func<TService, TService> factory, int priority = 0) where TService : class
    { EnsureOpen(); Access?.RequireDecoration(typeof(TService)); return Registry.Decorate(Scope, Activation, factory, priority); }
    public TService Get<TService>() where TService : class
    { if (TryGet<TService>(out var service)) return service!; throw new ServiceUnavailableException(typeof(TService)); }
    public bool TryGet<TService>(out TService? service) where TService : class
    {
        EnsureOpen();
        TService? value;
        var found = Access is { } access ? access.TryGet<TService>(out value) : Registry.TryGet<TService>(Scope, out value);
        service = found ? Adapt(value!) : null;
        return found;
    }
    public IReadOnlyList<TService> GetAll<TService>() where TService : class
    { EnsureOpen(); return (Access is { } access ? access.GetAll<TService>() : Registry.GetAll<TService>(Scope)).Select(Adapt).ToArray(); }
    public IDisposable WhenAvailable<TService>(Action<TService> callback) where TService : class
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(callback);
        Access?.RequireObservation(typeof(TService));
        return Registry.WhenAvailable<TService>(Scope, value => callback(Adapt(value)));
    }
    private TService Adapt<TService>(TService value) where TService : class =>
        typeof(TService) == typeof(IServiceRegistry) && ReferenceEquals(value, Registry) ? (TService)(object)this : value;
}
