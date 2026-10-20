using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BubbleFighter.Fantasy.Kernel.Tests;
public sealed class NativeServiceBindingTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Flag(bool Enabled) : IFlag;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(ServiceRegistry registry, ServiceScope scope, string id) => new(
        new PluginRow { Id = id, Plugin = "native.test", Source = "test" },
        registry.For(scope, registry.Runtime.NewActivation("input-view")),
        new EventBus(NullLogger.Instance), NullLogger.Instance);
    private static ServiceBinding Binding(ServiceRegistry registry, ServiceScope scope) =>
        Assert.Single(registry.CaptureBindings().Resolve(scope.Resolve(typeof(IValue))));

    [Fact]
    public async Task ActualContextsRegisterTheSameInterfaceInIndependentRealms()
    {
        var registry = new ServiceRegistry(16);
        var root = registry.Runtime.RootScope;
        using var inherited = registry.Register<IFlag>(new Flag(true));
        var leftScope = root.CreateChild([typeof(IValue)]);
        var rightScope = root.CreateChild([typeof(IValue)]);
        var left = Context(registry, leftScope, "left");
        var right = Context(registry, rightScope, "right");
        left.Services.Register<IValue>(new Value(1));
        right.Services.Register<IValue>(new Value(2));
        Assert.Equal(1, left.Services.Get<IValue>().Number);
        Assert.Equal(2, right.Services.Get<IValue>().Number);
        Assert.True(left.Services.Get<IFlag>().Enabled);
        Assert.False(registry.TryGet<IValue>(out _));
        Assert.Equal(left.NativeServices!.Activation, Binding(registry, leftScope).Registration.Provider);
        Assert.Equal(right.NativeServices!.Activation, Binding(registry, rightScope).Registration.Provider);
        Assert.NotEqual(Binding(registry, leftScope).Registration.Key, Binding(registry, rightScope).Registration.Key);
        await left.UnwindAsync();
        Assert.Equal(2, right.Services.Get<IValue>().Number);
        await right.UnwindAsync();
        Assert.Equal(1, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task MultipleProvidersAndAvailabilityObserversUseTheirOwnRealms()
    {
        var registry = new ServiceRegistry(16);
        var root = registry.Runtime.RootScope;
        var a = Context(registry, root.CreateChild([typeof(IValue)]), "a");
        var b = Context(registry, root.CreateChild([typeof(IValue)]), "b");
        var seen = 0;
        b.Services.WhenAvailable<IValue>(value => seen = value.Number);
        a.Services.RegisterMany<IValue>(new Value(1), 0);
        a.Services.RegisterMany<IValue>(new Value(2), 10);
        Assert.Equal(0, seen);
        b.Services.Register<IValue>(new Value(9));
        Assert.Equal(9, seen);
        Assert.Equal(new[] { 2, 1 }, a.Services.GetAll<IValue>().Select(value => value.Number));
        Assert.Throws<InvalidOperationException>(() => a.Services.Get<IValue>());
        Assert.Throws<InvalidOperationException>(() => b.Services.GetAll<IValue>());
        await a.UnwindAsync();
        await b.UnwindAsync();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task GettingTheRegistryDoesNotEscapeTheActivationOrRealm()
    {
        var registry = new ServiceRegistry(8);
        using var canonical = registry.Register<IServiceRegistry>(registry);
        var scope = registry.Runtime.RootScope.CreateChild([typeof(IValue)]);
        var context = Context(registry, scope, "owner");
        var nested = context.Services.Get<IServiceRegistry>();
        Assert.Same(context.Services, nested);
        nested.Register<IValue>(new Value(7));
        Assert.False(registry.TryGet<IValue>(out _));
        Assert.Equal("owner", Binding(registry, scope).Registration.Provider.EntryId);
        IServiceRegistry? observed = null;
        context.Services.WhenAvailable<IServiceRegistry>(value => observed = value);
        Assert.Same(context.Services, observed);
        await context.UnwindAsync();
        Assert.Empty(registry.CaptureBindings().Resolve(scope.Resolve(typeof(IValue))));
    }

    [Fact]
    public async Task RepeatedCaptureReusesIdentityButReactivationAllocatesANewGeneration()
    {
        var registry = new ServiceRegistry(8);
        var scope = registry.Runtime.RootScope;
        var first = Context(registry, scope, "same-entry");
        var value = new Value(7);
        first.Services.Register<IValue>(value);
        var before = Binding(registry, scope);
        Assert.Same(before, Binding(registry, scope));
        await first.UnwindAsync();
        var second = Context(registry, scope, "same-entry");
        second.Services.Register<IValue>(value);
        var after = Binding(registry, scope);
        Assert.NotEqual(before.Registration.Provider.Generation, after.Registration.Provider.Generation);
        Assert.NotEqual(before.Registration.Id, after.Registration.Id);
        Assert.Same(value, after.Instance);
        await second.UnwindAsync();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public void CapacityRejectsAnArbitraryFactoryBeforeItRunsAndRecoversOnRemoval()
    {
        var registry = new ServiceRegistry(1);
        var basis = registry.RegisterDecoratable<IValue>(new Value(1));
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => registry.Decorate<IValue>(inner => { calls++; return inner; }));
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => registry.WhenAvailable<IFlag>(_ => { }));
        Assert.Equal(1, registry.RetainedMetadataCount);
        basis.Dispose();
        using var observer = registry.WhenAvailable<IFlag>(_ => { });
        Assert.Equal(1, registry.RetainedMetadataCount);
        observer.Dispose();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task ProvisionalPublicationAndRunningObserversRetainTheirSlots()
    {
        var registry = new ServiceRegistry(2);
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        var observer = registry.WhenAvailable<IValue>(_ => { entered.TrySetResult(); finish.Wait(); });
        var registering = Task.Run(() => registry.RegisterDecoratable<IValue>(new Value(1)));
        await entered.Task.WaitAsync(Limit);
        try
        {
            observer.Dispose();
            Assert.Equal(2, registry.RetainedMetadataCount);
            Assert.Empty(registry.CaptureBindings().Resolve(registry.Runtime.RootScope.Resolve(typeof(IValue))));
            Assert.Throws<InvalidOperationException>(() => registry.Register<IFlag>(new Flag(true)));
            Assert.Throws<InvalidOperationException>(() => registry.Decorate<IValue>(inner => inner));
        }
        finally { finish.Set(); }
        var handle = await registering.WaitAsync(Limit);
        Assert.Single(registry.CaptureBindings().Resolve(registry.Runtime.RootScope.Resolve(typeof(IValue))));
        Assert.Equal(1, registry.RetainedMetadataCount);
        handle.Dispose();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public void FailedAvailabilityRollsBackPublicationAndMetadataWithoutRunningADecorator()
    {
        var registry = new ServiceRegistry(4);
        var factories = 0;
        registry.WhenAvailable<IValue>(_ =>
        {
            Assert.Empty(registry.CaptureBindings().Resolve(registry.Runtime.RootScope.Resolve(typeof(IValue))));
            Assert.Throws<InvalidOperationException>(() => registry.Decorate<IValue>(inner => { factories++; return inner; }));
            throw new ArgumentException("observer-failed");
        });
        var failure = Assert.Throws<AggregateException>(() => registry.RegisterDecoratable<IValue>(new Value(1)));
        Assert.Contains("observer-failed", failure.ToString());
        Assert.False(registry.TryGet<IValue>(out _));
        Assert.Equal(0, factories);
        Assert.Equal(0, registry.RetainedMetadataCount);
    }
}
