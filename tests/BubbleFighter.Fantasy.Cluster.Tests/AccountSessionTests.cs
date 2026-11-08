using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class AccountSessionTests
{
    [Fact]
    public async Task MutationBindingIsRemovedBeforeItsOriginalHoldCanBeReleased()
    {
        var store = new Store();
        var mutations = new MutationScopes();
        await using var admission = new AccountSessions(store, new AccountSessionOptions(), NullLogger.Instance, mutations);
        var session = new Session(1);
        Assert.True(await admission.TryAdmitAsync(session, 7, default));
        Assert.Same(session, mutations.Bound);
        store.Holds[7].BeforeRelease = () =>
        {
            Assert.True(mutations.Removed);
            Assert.False(session.TryFeature<IAccountSession>(out _));
            return ValueTask.CompletedTask;
        };
        await admission.ReleaseAsync(session);
    }

    [Fact]
    public async Task FailedMutationBindingReleasesTheHold()
    {
        var store = new Store();
        var mutations = new MutationScopes { FailBind = true };
        await using var admission = new AccountSessions(store, new AccountSessionOptions(), NullLogger.Instance, mutations);
        var session = new Session(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.TryAdmitAsync(session, 7, default).AsTask());
        Assert.Empty(store.Holds);
        Assert.False(session.TryFeature<IAccountSession>(out _));
        mutations.FailBind = false;
        Assert.True(await admission.TryAdmitAsync(new Session(2), 7, default));
    }

    [Fact]
    public async Task PacketDecoratorRestoresTheCallerScopeAfterHandlerFailureAndUnloadsCleanly()
    {
        var services = new ServiceRegistry();
        var mutations = new MutationScopes();
        var inner = new PacketRouter(mutations);
        using var routerRegistration = services.RegisterDecoratable<IPacketRouter>(inner);
        var original = services.Get<IPacketRouter>();
        using var mutationRegistration = services.Register<IAccountMutationScopes>(mutations);
        var context = new PluginContext(new PluginRow { Id = "writes", Plugin = "cluster.account-mutations", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var session = new Session(1);
        try
        {
            await new AccountMutationsPlugin().ActivateAsync(context, default);
            var router = services.Get<IPacketRouter>();
            await Assert.ThrowsAsync<IOException>(() => router.DispatchAsync("test/game", session, new InboundPacket(1, ReadOnlyMemory<byte>.Empty), default).AsTask());
            Assert.Same(session, inner.Observed);
            Assert.Null(mutations.Current);
        }
        finally { await context.UnwindAsync(); }
        Assert.Same(original, services.Get<IPacketRouter>());
    }

    [Fact]
    public async Task PluginEffectsRetireClosedConnectionsAndDrainActiveHoldsOnUnload()
    {
        var store = new Store();
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        using var registration = services.Register<IAccountOwnershipStore>(store);
        var context = new PluginContext(new PluginRow
        {
            Id = "accounts", Plugin = "cluster.account-sessions", Source = "test",
            Config = new Dictionary<string, object?> { ["verification_seconds"] = 1 },
        }, services, events, NullLogger.Instance);
        var first = new Session(1);
        var second = new Session(2);
        try
        {
            await new AccountSessionsPlugin().ActivateAsync(context, default);
            var admission = services.Get<IAccountSessionAdmission>();
            Assert.True(await admission.TryAdmitAsync(first, 7, default));
            await first.Connection.CloseAsync("test closed");
            events.Emit(new ConnectionClosedEvent(first.Connection, "test closed"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (store.Holds.ContainsKey(7)) await Task.Delay(10, deadline.Token);
            Assert.False(first.TryFeature<IAccountSession>(out _));
            Assert.True(await admission.TryAdmitAsync(second, 8, default));
        }
        finally { await context.UnwindAsync(); }
        Assert.True(second.Connection.IsClosed);
        Assert.False(second.TryFeature<IAccountSession>(out _));
        Assert.Empty(store.Holds);
        Assert.False(services.TryGet<IAccountSessionAdmission>(out _));
    }

    [Fact]
    public async Task IndependentNodesCannotAdmitTheSameAccountUntilItsHoldRetires()
    {
        var store = new Store();
        await using var first = NewScope(store);
        await using var second = NewScope(store);
        var one = new Session(1);
        var two = new Session(2);
        Assert.True(await first.TryAdmitAsync(one, 7, default));
        Assert.False(await second.TryAdmitAsync(two, 7, default));
        Assert.False(two.TryFeature<IAccountSession>(out _));
        await first.ReleaseAsync(one);
        Assert.False(one.TryFeature<IAccountSession>(out _));
        Assert.True(await second.TryAdmitAsync(two, 7, default));
    }

    [Fact]
    public async Task RepeatedAdmissionKeepsOneHoldAndRejectsChangedIdentity()
    {
        var store = new Store();
        await using var scope = NewScope(store);
        var session = new Session(1);
        Assert.True(await scope.TryAdmitAsync(session, 7, default));
        var feature = session.Feature<IAccountSession>();
        Assert.True(await scope.TryAdmitAsync(session, 7, default));
        Assert.Same(feature, session.Feature<IAccountSession>());
        Assert.Equal(1, store.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.TryAdmitAsync(session, 8, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.TryAdmitAsync(new Session(1), 7, default).AsTask());
        Assert.Throws<InvalidOperationException>(() => scope.ReleaseAsync(new Session(1)));
        Assert.True(await feature.IsHeldAsync(default));
    }

    [Fact]
    public async Task PendingAdmissionIsRetainedAndCannotPublishAfterClose()
    {
        var ready = Signal();
        var resume = Signal();
        var store = new Store { BeforeAcquire = async _ => { ready.SetResult(); await resume.Task; } };
        await using var scope = NewScope(store);
        var session = new Session(1);
        var pending = scope.TryAdmitAsync(session, 7, default).AsTask();
        await ready.Task;
        Assert.Equal(1, scope.RetainedSessions);
        await session.Connection.CloseAsync("test close");
        scope.MarkClosed(1);
        resume.SetResult();
        Assert.False(await pending);
        Assert.Empty(store.Holds);
        Assert.False(session.TryFeature<IAccountSession>(out _));
        store.BeforeAcquire = null;
        Assert.True(await scope.TryAdmitAsync(new Session(2), 8, default));
    }

    [Fact]
    public async Task ClosedFeatureCollectionIsARefusedLoginAndReleasesItsHold()
    {
        var store = new Store();
        await using var scope = NewScope(store);
        var session = new Session(1);
        session.Slots.Dispose();
        Assert.False(await scope.TryAdmitAsync(session, 7, default));
        Assert.Empty(store.Holds);
    }

    [Fact]
    public async Task CancellationCannotLetAWaitingDuplicateObserveUnpublishedAuthority()
    {
        var entered = Signal();
        var resume = Signal();
        using var cancellation = new CancellationTokenSource();
        var store = new Store { BeforeAcquire = async _ => { entered.SetResult(); await resume.Task; } };
        await using var scope = NewScope(store);
        var session = new Session(1);
        var first = scope.TryAdmitAsync(session, 7, cancellation.Token).AsTask();
        await entered.Task;
        var duplicate = scope.TryAdmitAsync(session, 7, default).AsTask();
        cancellation.Cancel();
        resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(await duplicate);
        Assert.False(session.TryFeature<IAccountSession>(out _));
        Assert.Empty(store.Holds);
    }

    [Fact]
    public async Task DuplicateFeatureRegistrationRemainsAnErrorAndCleansUpTheNewHold()
    {
        var store = new Store();
        await using var first = NewScope(store);
        await using var second = NewScope(store);
        var session = new Session(1);
        Assert.True(await first.TryAdmitAsync(session, 7, default));
        var original = session.Feature<IAccountSession>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.TryAdmitAsync(session, 8, default).AsTask());
        Assert.Same(original, session.Feature<IAccountSession>());
        Assert.Equal(7, Assert.Single(store.Holds).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrFailedAuthorityClosesTheSessionAndCannotBeReacquired(bool throws)
    {
        var store = new Store();
        await using var scope = NewScope(store);
        var session = new Session(1);
        Assert.True(await scope.TryAdmitAsync(session, 7, default));
        var feature = session.Feature<IAccountSession>();
        var hold = store.Holds[7];
        hold.Held = false;
        if (throws) hold.Failure = new IOException("authority unavailable");
        await scope.VerifyAsync(default);
        Assert.True(session.Connection.IsClosed);
        Assert.False(session.TryFeature<IAccountSession>(out _));
        Assert.False(await feature.IsHeldAsync(default));
        Assert.Empty(store.Holds);
        Assert.Equal(1, store.Attempts);
    }

    [Fact]
    public async Task AdmissionCannotCommitWhileCallerCancellationCallbacksAreStillDraining()
    {
        var entered = Signal();
        var resume = Signal();
        var callbackEntered = Signal();
        var callbackRelease = Signal();
        var store = new Store { BeforeAcquire = async _ => { entered.SetResult(); await resume.Task; } };
        await using var scope = NewScope(store);
        using var cancellation = new CancellationTokenSource();
        var session = new Session(1);
        var pending = scope.TryAdmitAsync(session, 7, cancellation.Token).AsTask();
        await entered.Task;
        // Registered last, this callback delays propagation to the admission's linked token.
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.SetResult();
            callbackRelease.Task.GetAwaiter().GetResult();
        });
        var canceled = cancellation.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resume.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(store.Holds);
            Assert.False(session.TryFeature<IAccountSession>(out _));
        }
        finally
        {
            resume.TrySetResult();
            callbackRelease.SetResult();
            await canceled;
        }
        store.BeforeAcquire = null;
        Assert.True(await scope.TryAdmitAsync(new Session(2), 8, default));
    }

    [Fact]
    public async Task EveryDisposeWaitsForPendingAcquisitionAndRetirement()
    {
        var entered = Signal();
        var resume = Signal();
        var store = new Store { BeforeAcquire = async _ => { entered.SetResult(); await resume.Task; } };
        var scope = NewScope(store);
        var session = new Session(1);
        var pending = scope.TryAdmitAsync(session, 7, default).AsTask();
        await entered.Task;
        var first = scope.DisposeAsync().AsTask();
        var second = scope.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.WhenAll(first, second);
        Assert.True(session.Connection.IsClosed);
        Assert.Empty(store.Holds);
        Assert.False(session.TryFeature<IAccountSession>(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.TryAdmitAsync(new Session(2), 8, default).AsTask());
    }

    [Fact]
    public async Task RetiringHoldIsRetainedUntilCleanupCompletes()
    {
        var store = new Store();
        await using var scope = NewScope(store);
        var session = new Session(1);
        Assert.True(await scope.TryAdmitAsync(session, 7, default));
        var release = Signal();
        store.Holds[7].BeforeRelease = () => new ValueTask(release.Task);
        var retirement = scope.ReleaseAsync(session).AsTask();
        Assert.False(retirement.IsCompleted);
        Assert.Equal(1, scope.RetainedSessions);
        release.SetResult();
        await retirement;
        Assert.Equal(0, scope.RetainedSessions);
        Assert.True(await scope.TryAdmitAsync(new Session(2), 8, default));
    }

    [Fact]
    public async Task StoreFailurePropagatesWithoutLeavingAFeature()
    {
        var store = new Store { BeforeAcquire = _ => ValueTask.FromException(new IOException("unavailable")) };
        await using var scope = NewScope(store);
        var session = new Session(1);
        await Assert.ThrowsAsync<IOException>(() => scope.TryAdmitAsync(session, 7, default).AsTask());
        Assert.False(session.TryFeature<IAccountSession>(out _));
        store.BeforeAcquire = null;
        Assert.True(await scope.TryAdmitAsync(session, 7, default));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AccountSessions NewScope(Store store) =>
        new(store, new AccountSessionOptions(), NullLogger.Instance);

    private sealed class MutationScopes : IAccountMutationScopes
    {
        private readonly AsyncLocal<ISession?> _current = new();
        internal ISession? Current => _current.Value;
        internal ISession? Bound;
        internal bool Removed;
        internal bool FailBind;

        public IDisposable Bind(ISession session, IAccountOwnership ownership)
        {
            if (FailBind) throw new InvalidOperationException("test refused binding");
            Bound = session;
            return new Removal(() => Removed = true);
        }

        public IDisposable Enter(ISession session)
        {
            var previous = _current.Value;
            _current.Value = session;
            return new Removal(() => _current.Value = previous);
        }
    }

    private sealed class Removal(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    private sealed class PacketRouter(MutationScopes scopes) : IPacketRouter
    {
        internal ISession? Observed;
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler) => throw new NotSupportedException();
        public async ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct)
        {
            await Task.Yield();
            Observed = scopes.Current;
            throw new IOException("test handler failure");
        }
    }

    private sealed class Store : IAccountOwnershipStore
    {
        internal ConcurrentDictionary<long, Hold> Holds { get; } = new();
        internal Func<CancellationToken, ValueTask>? BeforeAcquire { get; set; }
        internal int Attempts;

        public async ValueTask<IAccountOwnership?> TryAcquireAsync(long userNum, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (BeforeAcquire is { } before) await before(cancellationToken);
            var hold = new Hold(this, userNum);
            return Holds.TryAdd(userNum, hold) ? hold : null;
        }
    }

    private sealed class Hold(Store store, long userNum) : IAccountOwnership
    {
        internal bool Held { get; set; } = true;
        internal Exception? Failure { get; set; }
        internal Func<ValueTask>? BeforeRelease { get; set; }
        public ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken) =>
            Failure is { } failure ? ValueTask.FromException<bool>(failure) : ValueTask.FromResult(Held);
        public async ValueTask DisposeAsync()
        {
            if (BeforeRelease is { } before) await before();
            store.Holds.TryRemove(new KeyValuePair<long, Hold>(userNum, this));
            Held = false;
        }
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        internal SessionFeatures Slots { get; } = new(4);
        public ISessionFeatures Features => Slots;
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
