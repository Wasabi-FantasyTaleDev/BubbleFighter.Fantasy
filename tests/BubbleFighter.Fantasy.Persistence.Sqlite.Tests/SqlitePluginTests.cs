using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqlitePluginTests
{
    [Fact]
    public async Task ActivationPublishesSharedIdentityAndDisposalRemovesServicesAndReleasesSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-sqlite-plugin-" + Guid.NewGuid().ToString("N"), "save.db");
        var registry = new FakeRegistry();
        try
        {
            var context = new FakeContext(registry, new() { DatabasePath = path, MaxMovementActors = 1 });
            await new SqlitePlugin().ActivateAsync(context, default);
            var players = registry.Get<IPlayerRepository>();
            Assert.True(players.PersistsState);
            Assert.True(players.CreatesStarterInventoryAtomically);
            var user = await players.CreateAsync("one", "nickname", default);
            Assert.Equal(user.UserNum, (await registry.Get<ILauncherAccountRepository>().FindAsync("one", default))!.UserNumber);
            Assert.NotNull(registry.Get<IInventoryRepository>());
            var movement = registry.Get<IPlayerMovementState>();
            Assert.Null(await movement.MergeAsync(1, 1, 0, 0, 0, 0, 0, 0, default));
            Assert.NotNull(await movement.MergeAsync(1, PlayerMovementState.FullMask, 1, 2, 3, 4, 5, 6, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => movement.MergeAsync(2, PlayerMovementState.FullMask,
                1, 2, 3, 4, 5, 6, default).AsTask());
            foreach (var effect in registry.Disposables.AsEnumerable().Reverse()) effect.Dispose();
            registry.Disposables.Clear();
            Assert.False(registry.TryGet<IPlayerRepository>(out _));
            Assert.False(registry.TryGet<IPlayerMovementState>(out _));
            using var reopened = new SqliteDatabase(new() { DatabasePath = path });
            Assert.Equal(user.UserNum, (await new SqliteRepositories(reopened).FindByUsernameAsync("one", default))!.UserNum);
        }
        finally
        {
            foreach (var effect in registry.Disposables.AsEnumerable().Reverse()) effect.Dispose();
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task ConflictingProviderFailsActivationAndCleanupReleasesTheLease()
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-sqlite-conflict-" + Guid.NewGuid().ToString("N"), "save.db");
        var registry = new FakeRegistry();
        using var other = new SqliteDatabase(new() { DatabasePath = path + ".other" });
        registry.Register<ILauncherAccountRepository>(new SqliteRepositories(other));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlitePlugin().ActivateAsync(
                new FakeContext(registry, new() { DatabasePath = path }), default).AsTask());
            foreach (var effect in registry.Disposables.AsEnumerable().Reverse()) effect.Dispose();
            registry.Disposables.Clear();
            using var reopened = new SqliteDatabase(new() { DatabasePath = path });
        }
        finally
        {
            foreach (var effect in registry.Disposables.AsEnumerable().Reverse()) effect.Dispose();
            other.Dispose();
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private sealed class FakeContext(FakeRegistry registry, SqliteOptions options) : IPluginContext
    {
        public PluginRow Row { get; } = new()
        {
            Id = "persistence.sqlite",
            Plugin = "persistence.sqlite",
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

    private sealed class FakeConfig(SqliteOptions options) : IPluginConfig
    {
        public T Bind<T>() where T : class, new()
        {
            if (typeof(T) != typeof(SqliteOptions))
                throw new NotSupportedException($"The test row only binds {nameof(SqliteOptions)}.");
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
