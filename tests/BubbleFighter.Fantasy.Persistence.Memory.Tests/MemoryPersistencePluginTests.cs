using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>
/// Activation of the in-memory persistence row: the plugin registers exactly the
/// repository interfaces it declares through context effects, the shared
/// table honours the bound row config, and unloading the row removes every
/// registration.
/// </summary>
public sealed class MemoryPersistencePluginTests
{
    /// <summary>The relationship provider uses the row's capacity limits and unregisters with its owned effects.</summary>
    [Fact]
    public async Task CoupleProviderUsesConfiguredLimitsAndUnloads()
    {
        var registry = new FakeRegistry();
        var context = new FakeContext(registry, new MemoryPersistenceOptions
        {
            Couples = new MemoryCoupleOptions { MaxPendingInvitations = 1 },
        });
        await new MemoryPersistencePlugin().ActivateAsync(context, default);
        var store = registry.Get<ICoupleStore>();
        Assert.False(store.PersistsState);
        var now = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        await store.CreateInvitationAsync(new("alpha", 1),
            new(Guid.NewGuid(), new("alpha", 2), "Hello", now, now.AddMinutes(1)), default);
        var second = await store.CreateInvitationAsync(new("alpha", 3),
            new(Guid.NewGuid(), new("alpha", 4), "Hello", now, now.AddMinutes(1)), default);
        Assert.Equal(CoupleStoreResult.CapacityReached, second.Result);
        foreach (var effect in registry.Disposables.AsEnumerable().Reverse()) effect.Dispose();
        Assert.False(registry.TryGet<ICoupleStore>(out _));
    }

    private sealed class FakeContext(FakeRegistry registry, MemoryPersistenceOptions options) : IPluginContext
    {
        public PluginRow Row { get; } = new()
        {
            Id = "persistence.memory",
            Plugin = "persistence.memory",
            Source = "test",
        };

        public IServiceRegistry Services => registry;

        public IEventBus Events => throw new NotSupportedException("The memory persistence row subscribes to no events.");

        public IPluginConfig Config => new FakeConfig(options);

        public ILogger Logger => NullLogger.Instance;

        public CancellationToken Lifetime => default;

        public ValueTask<IResourceScope> CreateResourceScopeAsync(string name) =>
            throw new NotSupportedException("This fixture exercises synchronous persistence registration only.");

        public ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fixture exercises synchronous persistence registration only.");

        public ValueTask<IAsyncDisposable> AcquireEffectAsync(Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire) =>
            throw new NotSupportedException("This fixture exercises synchronous memory registrations only.");

        public ValueTask<IOwnedTask> RunTaskAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This fixture exercises synchronous memory registrations only.");

        public IDisposable Effect(IDisposable disposer)
        {
            ArgumentNullException.ThrowIfNull(disposer);
            registry.Disposables.Add(disposer);
            return disposer;
        }

        public IDisposable Effect(Action dispose) => Effect(new ActionDisposer(dispose));

        private sealed class ActionDisposer(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class FakeConfig(MemoryPersistenceOptions options) : IPluginConfig
    {
        public T Bind<T>() where T : class, new()
        {
            if (typeof(T) != typeof(MemoryPersistenceOptions))
                throw new NotSupportedException($"The test row only binds {nameof(MemoryPersistenceOptions)}.");
            return options as T ?? new T();
        }
    }

    /// <summary>A minimal dictionary-backed registry: Register stores, TryGet reads, the disposer removes.</summary>
    private sealed class FakeRegistry : IServiceRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<Type, object> _services = [];

        public List<IDisposable> Disposables { get; } = [];

        public IDisposable RegisterDecoratable<TService>(TService instance) where TService : class => Register(instance);

        public IDisposable Decorate<TService>(Func<TService, TService> factory, int priority = 0) where TService : class =>
            throw new NotSupportedException("This fixture does not compose decorators.");

        public IDisposable RegisterMany<TService>(TService instance, int priority = 0) where TService : class =>
            throw new NotSupportedException("This fixture only exercises singleton repositories.");

        public IReadOnlyList<TService> GetAll<TService>() where TService : class =>
            throw new NotSupportedException("This fixture only exercises singleton repositories.");

        public IDisposable Register<TService>(TService instance) where TService : class
        {
            ArgumentNullException.ThrowIfNull(instance);
            lock (_gate)
            {
                if (_services.ContainsKey(typeof(TService)))
                    throw new InvalidOperationException($"Service '{typeof(TService).FullName}' is already registered.");
                _services[typeof(TService)] = instance;
            }

            return new Registration(this, typeof(TService), instance);
        }

        public TService Get<TService>() where TService : class
        {
            if (TryGet<TService>(out var service))
                return service!;
            throw new ServiceUnavailableException(typeof(TService));
        }

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            lock (_gate)
            {
                if (_services.TryGetValue(typeof(TService), out var value))
                {
                    service = (TService)value;
                    return true;
                }
            }

            service = null;
            return false;
        }

        public IDisposable WhenAvailable<TService>(Action<TService> callback) where TService : class =>
            throw new NotSupportedException("The test registry resolves synchronously only.");

        private void Remove(Type type, object instance)
        {
            lock (_gate)
                if (_services.TryGetValue(type, out var current) && ReferenceEquals(current, instance))
                    _services.Remove(type);
        }

        private sealed class Registration(FakeRegistry owner, Type type, object instance) : IDisposable
        {
            public void Dispose() => owner.Remove(type, instance);
        }
    }
}
