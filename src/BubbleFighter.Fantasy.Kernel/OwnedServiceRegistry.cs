using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Reserves activation ownership before publishing registrations into the shared registry.</summary>
internal sealed class OwnedServiceRegistry(PluginContext owner, IServiceRegistry inner, IEventBus events) : IServiceRegistry
{
    public IDisposable Register<TService>(TService instance) where TService : class =>
        owner.OwnRegistration(() => inner.Register(instance));

    public IDisposable RegisterMany<TService>(TService instance, int priority = 0) where TService : class =>
        owner.OwnRegistration(() => inner.RegisterMany(instance, priority));

    public IDisposable RegisterDecoratable<TService>(TService instance) where TService : class =>
        owner.OwnRegistration(() => inner.RegisterDecoratable(instance));

    public IDisposable Decorate<TService>(Func<TService, TService> factory, int priority = 0) where TService : class =>
        owner.OwnRegistration(() => inner.Decorate(factory, priority));

    public TService Get<TService>() where TService : class => Adapt(inner.Get<TService>());

    public bool TryGet<TService>(out TService? service) where TService : class
    {
        var found = inner.TryGet<TService>(out var result);
        service = found ? Adapt(result!) : null;
        return found;
    }

    public IReadOnlyList<TService> GetAll<TService>() where TService : class =>
        inner.GetAll<TService>().Select(Adapt).ToArray();

    public IDisposable WhenAvailable<TService>(Action<TService> callback) where TService : class
    {
        ArgumentNullException.ThrowIfNull(callback);
        return owner.OwnCallbackRegistration(registration => inner.WhenAvailable<TService>(value =>
        {
            if (!registration.TryEnter(out var invocation)) return;
            using (invocation)
                try { callback(Adapt(value)); }
                catch (Exception error) { owner.ReportAvailabilityFailure(error); throw; }
        }));
    }

    private TService Adapt<TService>(TService value) where TService : class
    {
        if (typeof(TService) == typeof(IServiceRegistry) && ReferenceEquals(value, inner))
            return (TService)(object)owner.Services;
        if (typeof(TService) == typeof(IEventBus) && ReferenceEquals(value, events))
            return (TService)(object)owner.Events;
        return value;
    }
}
