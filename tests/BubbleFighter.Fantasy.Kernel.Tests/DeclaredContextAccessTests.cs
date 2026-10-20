using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BubbleFighter.Fantasy.Kernel.Tests;
public sealed class DeclaredContextAccessTests
{
    public interface IValue { int Number { get; } }
    public interface IFlag { }
    private sealed record Value(int Number) : IValue;
    private sealed class Flag : IFlag;
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => release(); }
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(ServiceRegistry registry, PluginAttribute declaration)
    {
        var context = new PluginContext(new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = "access", Source = "test" },
            registry, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.BindServices(declaration);
        return context;
    }
    [Theory]
    [InlineData("get")]
    [InlineData("try")]
    [InlineData("many")]
    [InlineData("publish")]
    [InlineData("observe")]
    [InlineData("decorate")]
    public async Task UndeclaredOperationsFailBeforeChangingRegistrations(string operation)
    {
        var registry = new ServiceRegistry(16);
        using var basis = registry.RegisterDecoratable<IValue>(new Value(1));
        var context = Context(registry, new PluginAttribute("access"));
        var factories = 0;
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (operation)
            {
                case "get": context.Services.Get<IValue>(); break;
                case "try": context.Services.TryGet<IValue>(out _); break;
                case "many": context.Services.GetAll<IValue>(); break;
                case "publish": context.Services.Register<IFlag>(new Flag()); break;
                case "observe": context.Services.WhenAvailable<IValue>(_ => factories++); break;
                case "decorate": context.Services.Decorate<IValue>(_ => { factories++; return new Value(2); }); break;
            }
        });
        Assert.Equal(0, factories);
        Assert.Equal(1, registry.RetainedMetadataCount);
        Assert.Equal(0, context.RetainedEffectCount);
        await context.UnwindAsync();
    }
    [Fact]
    public async Task OwnServicesUseTheirBaseValueAndDoNotCreateAnExternalSelfDependency()
    {
        var registry = new ServiceRegistry(16);
        var context = Context(registry, new PluginAttribute("access") { Provides = [typeof(IValue)] });
        Assert.False(context.Services.TryGet<IValue>(out _));
        context.Services.RegisterDecoratable<IValue>(new Value(1));
        using var layer = registry.Decorate<IValue>(_ => new Value(2));
        Assert.Equal(1, context.Services.Get<IValue>().Number);
        Assert.Equal(2, registry.Get<IValue>().Number);
        Assert.Empty(context.NativeServices!.Access!.View.Providers);
        await context.UnwindAsync();
        Assert.Throws<ObjectDisposedException>(() => context.Services.Get<IValue>());
    }
    [Fact]
    public async Task OwnMultipleContributionsMergeWithOnlyTheCommittedExternalList()
    {
        var registry = new ServiceRegistry(16);
        using var external = registry.RegisterMany<IValue>(new Value(1));
        var context = Context(registry, new PluginAttribute("access")
        { RequiresMany = [typeof(IValue)], ProvidesMany = [typeof(IValue)] });
        context.Services.RegisterMany<IValue>(new Value(3), 10);
        using var later = registry.RegisterMany<IValue>(new Value(2), 20);
        Assert.Equal(new[] { 3, 1 }, context.Services.GetAll<IValue>().Select(value => value.Number));
        Assert.Equal(new[] { 2, 3, 1 }, registry.GetAll<IValue>().Select(value => value.Number));
        Assert.DoesNotContain(context.NativeServices!.Activation, context.NativeServices.Access!.View.Providers);
        Assert.Throws<InvalidOperationException>(() => context.Services.Get<IValue>());
        await context.UnwindAsync();
    }
    [Fact]
    public async Task ExplicitObservationDoesNotReplaceCommittedOptionalAbsence()
    {
        var registry = new ServiceRegistry(16);
        var context = Context(registry, new PluginAttribute("access")
        { Optional = [typeof(IValue)], Observes = [typeof(IValue)] });
        var observed = 0;
        context.Services.WhenAvailable<IValue>(value => observed = value.Number);
        using var later = registry.Register<IValue>(new Value(7));
        Assert.Equal(7, observed);
        Assert.False(context.Services.TryGet<IValue>(out _));
        Assert.Empty(context.NativeServices!.Access!.View.Providers);
        await context.UnwindAsync();
    }
    [Fact]
    public async Task ObservationAloneDoesNotGrantOrdinaryReads()
    {
        var registry = new ServiceRegistry(16);
        using var basis = registry.Register<IValue>(new Value(1));
        var context = Context(registry, new PluginAttribute("access") { Observes = [typeof(IValue)] });
        var observed = 0;
        context.Services.WhenAvailable<IValue>(value => observed = value.Number);
        Assert.Equal(1, observed);
        Assert.Throws<InvalidOperationException>(() => context.Services.Get<IValue>());
        await context.UnwindAsync();
    }
    [Fact]
    public async Task CleanupRetainsItsCommittedProviderUntilDrainCompletesThenRevokesTheFacade()
    {
        var registry = new ServiceRegistry(16);
        using var basis = registry.Register<IValue>(new Value(1));
        var context = Context(registry, new PluginAttribute("access") { Requires = [typeof(IValue)] });
        var entered = Signal();
        var finish = Signal();
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
        { entered.TrySetResult(); await finish.Task; Assert.Equal(1, context.Services.Get<IValue>().Number); })));
        var closing = context.UnwindAsync().AsTask();
        await entered.Task.WaitAsync(Limit);
        try { Assert.Equal(1, context.Services.Get<IValue>().Number); Assert.False(closing.IsCompleted); }
        finally { finish.TrySetResult(); }
        await closing.WaitAsync(Limit);
        Assert.Throws<ObjectDisposedException>(() => context.Services.TryGet<IValue>(out _));
        Assert.Null(context.NativeServices!.Access);
    }
    [Fact]
    public async Task UnknownCleanupFailureKeepsTheOldViewAvailable()
    {
        var registry = new ServiceRegistry(16);
        using var basis = registry.Register<IValue>(new Value(1));
        var context = Context(registry, new PluginAttribute("access") { Requires = [typeof(IValue)] });
        context.Effect(() => throw new InvalidOperationException("cleanup-failed"));
        await Assert.ThrowsAsync<AggregateException>(() => context.UnwindAsync().AsTask());
        Assert.Equal(1, context.Services.Get<IValue>().Number);
        Assert.NotNull(context.NativeServices!.Access);
    }
    [Fact]
    public async Task CanonicalRegistryReadsRemainBoundAndDeclarationsCannotBeMutatedOrRebound()
    {
        var registry = new ServiceRegistry(16);
        using var bridge = registry.Register<IServiceRegistry>(registry);
        var declarations = new[] { typeof(IServiceRegistry) };
        var context = Context(registry, new PluginAttribute("access") { Requires = declarations });
        declarations[0] = typeof(IValue);
        var access = context.Services.Get<IServiceRegistry>();
        Assert.Same(context.Services, access);
        Assert.Throws<InvalidOperationException>(() => access.Register<IValue>(new Value(1)));
        Assert.Throws<InvalidOperationException>(() => context.BindServices(new PluginAttribute("wider") { Provides = [typeof(IValue)] }));
        await context.UnwindAsync();
    }
}
