using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class MessageRouterTests
{
    public sealed record Ping(int Value = 0) : IMessage;
    private const string Protocol = "test/game";

    internal sealed class Session : ISession
    {
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 1304, "Test session");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<ushort> Replies { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Replies.Add(opcode);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness(int capacity = 100, int depth = 8) : IDisposable
    {
        public EventBus Events { get; } = new(NullLogger.Instance);
        public Session Session { get; } = new();
        public MessageRouter Router { get; } = new(new EventBus(NullLogger.Instance), NullLogger.Instance,
            new() { MaxRegistrations = capacity, MaxPipelineDepth = depth });
        private readonly List<PluginContext> _owners = [];
        public PluginContext Owner(string id, params string[] targets)
        {
            var owner = new PluginContext(new() { Id = id, Plugin = id, Source = "test" },
                new ServiceRegistry(), Events, NullLogger.Instance, targets);
            _owners.Add(owner);
            return owner;
        }
        public Task<bool> Dispatch(CancellationToken ct = default) => Router.DispatchAsync(Protocol, Session, new Ping(5), ct).AsTask();
        public void Dispose() { foreach (var owner in _owners.AsEnumerable().Reverse()) owner.Unwind(); }
    }

    [Fact]
    public async Task UnloadWaitsForTheRunningTypedHandlerAfterCancellingIt()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        owner.Effect(h.Router.For(owner).On<Ping>(Protocol, async (_, _, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            await Task.Delay(100, CancellationToken.None);
            finished = true;
        }));
        var call = h.Dispatch();
        await entered.Task;

        await Task.Run(owner.Unwind);

        Assert.True(finished);
        await call;
        Assert.False(await h.Dispatch());
    }

    [Fact]
    public async Task MiddlewareUsesPriorityThenRegistrationOrderAndCanIntercept()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var routes = h.Router.For(owner);
        var trace = new List<string>();
        owner.Effect(routes.On<Ping>(Protocol, (_, _, _) => { trace.Add("handler"); return ValueTask.CompletedTask; }));
        foreach (var (name, priority) in new[] { ("low", 0), ("high", 10), ("tie", 0) })
            owner.Effect(routes.Use<Ping>(Protocol, async (s, m, next, ct) =>
            { trace.Add(name); await next(s, m, ct); trace.Add(name + "-after"); }, priority));
        Assert.True(await h.Dispatch());
        Assert.Equal(new[] { "high", "low", "tie", "handler", "tie-after", "low-after", "high-after" }, trace);
        trace.Clear();
        using var intercept = routes.Use<Ping>(Protocol, (_, _, _, _) => { trace.Add("intercept"); return ValueTask.CompletedTask; }, 99);
        Assert.True(await h.Dispatch());
        Assert.Equal(new[] { "intercept" }, trace);
    }

    [Fact]
    public async Task OverridesRequireDeclarationsAndRestoreWithoutStaleDisposerDamage()
    {
        using var h = new Harness();
        var first = h.Owner("first");
        var second = h.Owner("second", $"first:{typeof(Ping).FullName}");
        var third = h.Owner("third", $"second:{typeof(Ping).FullName}");
        var called = "";
        var baseHandle = first.Effect(h.Router.For(first).On<Ping>(Protocol, (_, _, _) => { called = "first"; return ValueTask.CompletedTask; }));
        Assert.Throws<InvalidOperationException>(() => h.Router.For(second).On<Ping>(Protocol, (_, _, _) => ValueTask.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => h.Router.For(first).Override<Ping>(Protocol, "first", (_, _, _) => ValueTask.CompletedTask));
        var replacement = second.Effect(h.Router.For(second).Override<Ping>(Protocol, "first", (_, _, _) => { called = "second"; return ValueTask.CompletedTask; }));
        var outer = third.Effect(h.Router.For(third).Override<Ping>(Protocol, "second", (_, _, _) => { called = "third"; return ValueTask.CompletedTask; }));
        await h.Dispatch(); Assert.Equal("third", called);
        outer.Dispose();
        await h.Dispatch(); Assert.Equal("second", called);
        replacement.Dispose();
        await h.Dispatch(); Assert.Equal("first", called);
        baseHandle.Dispose();
        first.Effect(h.Router.For(first).On<Ping>(Protocol, (_, _, _) => { called = "new"; return ValueTask.CompletedTask; }));
        baseHandle.Dispose(); replacement.Dispose(); outer.Dispose();
        await h.Dispatch(); Assert.Equal("new", called);
    }

    [Fact]
    public async Task RemovingBaseInvalidatesDependentOverrides()
    {
        using var h = new Harness();
        var first = h.Owner("first");
        var second = h.Owner("second", $"first:{typeof(Ping).FullName}");
        var registration = first.Effect(h.Router.For(first).On<Ping>(Protocol, (_, _, _) => ValueTask.CompletedTask));
        second.Effect(h.Router.For(second).Override<Ping>(Protocol, "first", (_, _, _) => ValueTask.CompletedTask));
        registration.Dispose();
        Assert.Empty(h.Router.Handlers);
        Assert.False(await h.Dispatch());
    }

    [Fact]
    public async Task PipelineSnapshotSurvivesRegistrationRemovalAcrossAwait()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var handler = owner.Effect(h.Router.For(owner).On<Ping>(Protocol, (_, _, _) => { calls.Add("old"); return ValueTask.CompletedTask; }));
        var middleware = owner.Effect(h.Router.For(owner).Use<Ping>(Protocol, async (s, m, next, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); await next(s, m, ct); }));
        var pending = h.Dispatch();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        middleware.Dispose(); handler.Dispose();
        owner.Effect(h.Router.For(owner).On<Ping>(Protocol, (_, _, _) => { calls.Add("new"); return ValueTask.CompletedTask; }));
        await h.Dispatch();
        release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "new", "old" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddlewareCanCatchSynchronousOrAsynchronousDownstreamFailure(bool asynchronous)
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var caught = false;
        owner.Effect(h.Router.For(owner).On<Ping>(Protocol, async (_, _, _) =>
        { if (asynchronous) await Task.Yield(); throw new InvalidDataException("expected"); }));
        owner.Effect(h.Router.For(owner).Use<Ping>(Protocol, async (s, m, next, ct) =>
        { try { await next(s, m, ct); } catch (InvalidDataException) { caught = true; } }));
        Assert.True(await h.Dispatch());
        Assert.True(caught);
    }

    [Fact]
    public async Task ForgottenAwaitIsDrainedAndItsFailurePropagates()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Effect(h.Router.For(owner).On<Ping>(Protocol, async (_, _, ct) =>
        { await release.Task.WaitAsync(ct); throw new InvalidDataException("forgotten"); }));
        owner.Effect(h.Router.For(owner).Use<Ping>(Protocol, (s, m, next, ct) => { _ = next(s, m, ct); return ValueTask.CompletedTask; }));
        var task = h.Dispatch();
        Assert.False(task.IsCompleted);
        release.TrySetResult();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("forgotten", error.Message);
    }

    [Fact]
    public async Task NextCannotRunTwiceOrEscapeMiddlewareInvocation()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var calls = 0;
        MessageHandler<Ping>? escaped = null;
        owner.Effect(h.Router.For(owner).On<Ping>(Protocol, (_, _, _) => { calls++; return ValueTask.CompletedTask; }));
        owner.Effect(h.Router.For(owner).Use<Ping>(Protocol, async (s, m, next, ct) =>
        { escaped = next; await next(s, m, ct); Assert.Throws<InvalidOperationException>(() => next(s, m, ct)); }));
        await h.Dispatch();
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => escaped!(h.Session, new Ping(), default));
    }

    [Fact]
    public async Task OwnerUnloadCancelsRunningHandlerAndRefusesLateRegistration()
    {
        using var h = new Harness();
        var owner = h.Owner("base");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = h.Router.For(owner);
        owner.Effect(routes.On<Ping>(Protocol, async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }));
        var pending = h.Dispatch();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Unwind();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(h.Router.Handlers);
        Assert.ThrowsAny<OperationCanceledException>(() => routes.On<Ping>(Protocol, (_, _, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task UnknownMessagesAndProtocolScopesRemainSeparate()
    {
        var bus = new EventBus(NullLogger.Instance);
        var router = new MessageRouter(bus, NullLogger.Instance, new());
        var session = new Session();
        var messages = new List<MessageUnhandledEvent>();
        using var eventHandle = bus.On<MessageUnhandledEvent>(messages.Add);
        Assert.False(await router.DispatchAsync(Protocol, session, new Ping(), default));
        Assert.Equal(typeof(Ping), Assert.Single(messages).MessageType);
        using var h = new Harness();
        var owner = h.Owner("base");
        owner.Effect(router.For(owner).On<Ping>("other/game", (_, _, _) => throw new Exception("wrong scope")));
        Assert.False(await router.DispatchAsync(Protocol, session, new Ping(), default));
    }

    [Fact]
    public void RegistrationAndPipelineBoundsAreReleasedWithTheirHandles()
    {
        using var h = new Harness(capacity: 2, depth: 1);
        var owner = h.Owner("base");
        var routes = h.Router.For(owner);
        var handler = owner.Effect(routes.On<Ping>(Protocol, (_, _, _) => ValueTask.CompletedTask));
        var middleware = owner.Effect(routes.Use<Ping>(Protocol, (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => routes.On<Ping>("other", (_, _, _) => ValueTask.CompletedTask));
        handler.Dispose();
        Assert.Throws<InvalidOperationException>(() => routes.Use<Ping>(Protocol, (_, _, _, _) => ValueTask.CompletedTask));
        middleware.Dispose();
        owner.Effect(routes.On<Ping>(Protocol, (_, _, _) => ValueTask.CompletedTask));
        Assert.Single(h.Router.Handlers);
    }
}
