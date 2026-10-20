using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Tests for <see cref="ServiceRegistry"/>.</summary>
public sealed class ServiceRegistryTests
{
    private interface IGreeter { string Greet(); }

    private sealed class Greeter : IGreeter { public string Greet() => "hi"; }

    [Fact]
    public void MultipleRegistrationsKeepPriorityOrderAndIndependentIdentity()
    {
        var registry = new ServiceRegistry();
        var first = new Greeter();
        var second = new Greeter();
        using var low = registry.RegisterMany<IGreeter>(first, -1);
        using var high = registry.RegisterMany<IGreeter>(second, 10);
        var repeated = registry.RegisterMany<IGreeter>(second, 10);
        var snapshot = registry.GetAll<IGreeter>();
        Assert.Equal(new IGreeter[] { second, second, first }, snapshot);
        repeated.Dispose();
        using var replacement = registry.RegisterMany<IGreeter>(second, 10);
        repeated.Dispose();
        Assert.Equal(new IGreeter[] { second, second, first }, registry.GetAll<IGreeter>());
        high.Dispose();
        Assert.Equal(new IGreeter[] { second, first }, registry.GetAll<IGreeter>());
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void SingleAndMultipleModesCannotBeMixedOrResolvedImplicitly()
    {
        var registry = new ServiceRegistry();
        Assert.Empty(registry.GetAll<IGreeter>());
        var many = registry.RegisterMany<IGreeter>(new Greeter());
        Assert.Throws<InvalidOperationException>(() => registry.Get<IGreeter>());
        Assert.Throws<InvalidOperationException>(() => registry.TryGet<IGreeter>(out _));
        Assert.Throws<InvalidOperationException>(() => registry.Register<IGreeter>(new Greeter()));
        Assert.Throws<InvalidOperationException>(() => registry.WhenAvailable<IGreeter>(_ => { }));
        many.Dispose();
        Assert.Empty(registry.GetAll<IGreeter>());
        using var single = registry.Register<IGreeter>(new Greeter());
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMany<IGreeter>(new Greeter()));
        Assert.Throws<InvalidOperationException>(() => registry.GetAll<IGreeter>());
    }

    [Fact]
    public void PendingSingletonObserverPreventsMultipleModeUntilDisposed()
    {
        var registry = new ServiceRegistry();
        var observer = registry.WhenAvailable<IGreeter>(_ => { });
        Assert.Throws<InvalidOperationException>(() => registry.RegisterMany<IGreeter>(new Greeter()));
        observer.Dispose();
        using var many = registry.RegisterMany<IGreeter>(new Greeter());
        Assert.Single(registry.GetAll<IGreeter>());
    }

    [Fact]
    public void RegisterThenGetReturnsInstance()
    {
        var registry = new ServiceRegistry();
        var greeter = new Greeter();
        using var registration = registry.Register<IGreeter>(greeter);
        Assert.Same(greeter, registry.Get<IGreeter>());
    }

    [Fact]
    public void RegisterSameTypeTwiceThrows()
    {
        var registry = new ServiceRegistry();
        registry.Register<IGreeter>(new Greeter());
        Assert.Throws<InvalidOperationException>(() => registry.Register<IGreeter>(new Greeter()));
    }

    [Fact]
    public void DisposeRemovesRegistration()
    {
        var registry = new ServiceRegistry();
        var registration = registry.Register<IGreeter>(new Greeter());
        registration.Dispose();
        Assert.False(registry.TryGet<IGreeter>(out _));
        Assert.Throws<ServiceUnavailableException>(() => registry.Get<IGreeter>());
    }

    [Fact]
    public void TryGetReturnsFalseWhenAbsent()
    {
        var registry = new ServiceRegistry();
        Assert.False(registry.TryGet<IGreeter>(out var service));
        Assert.Null(service);
    }

    [Fact]
    public void WhenAvailableFiresImmediatelyWhenRegistered()
    {
        var registry = new ServiceRegistry();
        var greeter = new Greeter();
        registry.Register<IGreeter>(greeter);
        IGreeter? seen = null;
        using var sub = registry.WhenAvailable<IGreeter>(g => seen = g);
        Assert.Same(greeter, seen);
    }

    [Fact]
    public void WhenAvailableFiresOnLaterRegistration()
    {
        var registry = new ServiceRegistry();
        IGreeter? seen = null;
        using var sub = registry.WhenAvailable<IGreeter>(g => seen = g);
        Assert.Null(seen);
        var greeter = new Greeter();
        registry.Register<IGreeter>(greeter);
        Assert.Same(greeter, seen);
    }

    [Fact]
    public void WhenAvailableDisposalPreventsCallback()
    {
        var registry = new ServiceRegistry();
        var fired = false;
        var sub = registry.WhenAvailable<IGreeter>(_ => fired = true);
        sub.Dispose();
        registry.Register<IGreeter>(new Greeter());
        Assert.False(fired);
    }

    [Fact]
    public void OldHandleCannotRemoveReregisteredSameInstance()
    {
        var registry = new ServiceRegistry();
        var instance = new Greeter();
        var first = registry.Register<IGreeter>(instance);
        first.Dispose();
        using var second = registry.Register<IGreeter>(instance);
        first.Dispose();
        Assert.Same(instance, registry.Get<IGreeter>());
    }

    [Fact]
    public void CallbackFailuresNotifyRemainingWaitersAndRollBackRegistration()
    {
        var registry = new ServiceRegistry();
        var observed = 0;
        registry.WhenAvailable<IGreeter>(_ => throw new InvalidOperationException("observer failure"));
        registry.WhenAvailable<IGreeter>(_ => observed++);
        var error = Assert.Throws<AggregateException>(() => registry.Register<IGreeter>(new Greeter()));
        Assert.Equal("observer failure", Assert.Single(error.InnerExceptions).Message);
        Assert.Equal(1, observed);
        Assert.False(registry.TryGet<IGreeter>(out _));
        using var retry = registry.Register<IGreeter>(new Greeter());
        Assert.Equal(1, observed);
    }

    [Fact]
    public void DisposedSubscriptionCannotCancelAnotherSubscriptionWithTheSameDelegate()
    {
        var registry = new ServiceRegistry();
        var observed = 0;
        Action<IGreeter> callback = _ => observed++;
        var first = registry.WhenAvailable(callback);
        first.Dispose();
        using var second = registry.WhenAvailable(callback);
        first.Dispose();
        using var registration = registry.Register<IGreeter>(new Greeter());
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task DisposalCancelsAnExtractedCallbackBeforeDispatchBegins()
    {
        var registry = new ServiceRegistry();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        registry.WhenAvailable<IGreeter>(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        });
        var invoked = false;
        var pending = registry.WhenAvailable<IGreeter>(_ => invoked = true);
        var register = Task.Run(() => registry.Register<IGreeter>(new Greeter()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            pending.Dispose();
        }
        finally { release.Set(); }
        using var registration = await register.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(invoked);
    }
}
