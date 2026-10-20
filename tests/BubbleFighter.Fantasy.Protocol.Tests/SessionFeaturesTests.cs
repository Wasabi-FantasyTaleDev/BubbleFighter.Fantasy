using BubbleFighter.Fantasy.Tests;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class SessionFeaturesTests
{
    [Fact]
    public void ExactContractKeyDoesNotPublishConcreteOrOtherInterfaceTypes()
    {
        var session = NewSession();
        using var lifetime = (IDisposable)session;
        var state = new State();
        using var registration = session.RegisterFeature<IState>(state);
        Assert.Same(state, session.Feature<IState>());
        Assert.True(session.TryFeature<IState>(out var found));
        Assert.Same(state, found);
        Assert.False(session.TryFeature<State>(out _));
        Assert.False(session.TryFeature<IDisposable>(out _));
        Assert.Empty(session.Items);
    }

    [Fact]
    public void DuplicateIsRejectedAndOldHandleCannotRemoveReplacement()
    {
        var session = NewSession();
        using var lifetime = (IDisposable)session;
        var original = new State();
        var old = session.RegisterFeature<IState>(original);
        Assert.Throws<InvalidOperationException>(() => session.RegisterFeature<IState>(new State()));
        Assert.Same(original, session.Feature<IState>());
        old.Dispose();
        Assert.False(session.TryFeature<IState>(out _));
        var replacement = new State();
        using var current = session.RegisterFeature<IState>(replacement);
        old.Dispose();
        Assert.Same(replacement, session.Feature<IState>());
        Assert.Equal(1, session.Features.Count);
    }

    [Fact]
    public void CapacityIsReusableAndFailureLeavesExistingValueUntouched()
    {
        var session = NewSession(1);
        using var lifetime = (IDisposable)session;
        var first = new State();
        var registration = session.RegisterFeature<IState>(first);
        Assert.Equal(1, session.Features.Capacity);
        Assert.Throws<InvalidOperationException>(() => session.RegisterFeature(new object()));
        Assert.Same(first, session.Feature<IState>());
        registration.Dispose();
        using var next = session.RegisterFeature(new object());
        Assert.Equal(1, session.Features.Count);
    }

    [Fact]
    public void NullAndMissingFeaturesFailWithoutMutation()
    {
        var session = NewSession();
        using var lifetime = (IDisposable)session;
        Assert.Throws<ArgumentNullException>(() => session.RegisterFeature<IState>(null!));
        Assert.Throws<InvalidOperationException>(() => session.Feature<IState>());
        Assert.False(session.TryFeature<IState>(out var missing));
        Assert.Null(missing);
        Assert.Equal(0, session.Features.Count);
    }

    [Fact]
    public void RemovalAndClosureDoNotDisposeBorrowedStateOrReviveCollection()
    {
        var session = NewSession();
        var state = new State();
        using var registration = session.RegisterFeature<IState>(state);
        var borrowed = session.Feature<IState>();
        ((IDisposable)session).Dispose();
        ((IDisposable)session).Dispose();
        Assert.True(session.Features.IsClosed);
        Assert.Equal(0, session.Features.Count);
        Assert.False(session.TryFeature<IState>(out _));
        Assert.Throws<ObjectDisposedException>(() => session.Feature<IState>());
        Assert.Throws<ObjectDisposedException>(() => session.RegisterFeature<IState>(new State()));
        registration.Dispose();
        Assert.Same(state, borrowed);
        Assert.False(state.Disposed);
    }

    [Fact]
    public void DisposingOneRegistrationDoesNotDisposeValue()
    {
        var session = NewSession();
        using var lifetime = (IDisposable)session;
        var state = new State();
        session.RegisterFeature<IState>(state).Dispose();
        Assert.False(state.Disposed);
        Assert.Equal(0, session.Features.Count);
    }

    [Fact]
    public async Task ConcurrentDuplicateRegistrationsHaveExactlyOneWinner()
    {
        var session = NewSession();
        using var lifetime = (IDisposable)session;
        var leases = new ConcurrentBag<IDisposable>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try { leases.Add(session.RegisterFeature<IState>(new State())); }
            catch (InvalidOperationException) { }
        })));
        Assert.Single(leases);
        Assert.Equal(1, session.Features.Count);
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, session.Features.Count);
    }

    [Fact]
    public async Task RegistrationRacingClosureCannotLeaveAnAssociation()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var session = NewSession(1);
            IDisposable? registration = null;
            await Task.WhenAll(
                Task.Run(() =>
                {
                    try { registration = session.RegisterFeature<IState>(new State()); }
                    catch (ObjectDisposedException) { }
                }),
                Task.Run(() => ((IDisposable)session).Dispose()));
            registration?.Dispose();
            Assert.True(session.Features.IsClosed);
            Assert.Equal(0, session.Features.Count);
        }
    }

    [Fact]
    public void DifferentSessionsNeverShareFeatureAssociations()
    {
        var first = NewSession();
        var second = NewSession();
        using var firstLifetime = (IDisposable)first;
        using var secondLifetime = (IDisposable)second;
        using var firstRegistration = first.RegisterFeature<IState>(new State());
        using var secondRegistration = second.RegisterFeature<IState>(new State());
        Assert.NotSame(first.Feature<IState>(), second.Feature<IState>());
        firstLifetime.Dispose();
        Assert.True(second.TryFeature<IState>(out _));
    }

    [Fact]
    public void LegacySessionDoesNotSilentlyStoreFeaturesInItems()
    {
        ISession session = new LegacySession();
        Assert.Throws<NotSupportedException>(() => session.Feature<IState>());
        Assert.Throws<NotSupportedException>(() => session.RegisterFeature<IState>(new State()));
        Assert.False(session.TryFeature<IState>(out _));
        Assert.Empty(session.Items);
    }

    [Fact]
    public void NullSessionIsAnArgumentError()
    {
        ISession session = null!;
        Assert.Throws<ArgumentNullException>(() => session.Feature<IState>());
        Assert.Throws<ArgumentNullException>(() => session.RegisterFeature<IState>(new State()));
        Assert.Throws<ArgumentNullException>(() => session.TryFeature<IState>(out _));
    }

    private static ISessionWithFeatures NewSession(int capacity = 4)
    {
        var type = typeof(ProtocolPlugin).Assembly.GetType("BubbleFighter.Fantasy.Protocol.WireSession")!;
        return (ISessionWithFeatures)Activator.CreateInstance(type,
            new ProtocolHandshakeTests.LoopbackConnection(), TestWireDialects.Get("kr-1256").Profile,
            ProtocolPlugin.GameProtocolName, TestWireDialects.Get("kr-1256"),
            TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), capacity, 64,
            (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance)!;
    }

    private interface IState;

    private sealed class State : IState, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class LegacySession : ISession
    {
        public long Id => 42;
        public IConnection Connection { get; } = new ProtocolHandshakeTests.LoopbackConnection();
        public ClientProfile Profile => TestWireDialects.Get("kr-1256").Profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
