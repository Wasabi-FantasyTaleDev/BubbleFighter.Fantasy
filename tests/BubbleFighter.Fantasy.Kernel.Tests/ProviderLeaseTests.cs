using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ProviderLeaseTests
{
    public interface IValue { int Number { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Layer(IValue Inner) : IValue { public int Number => Inner.Number + 1; }
    private sealed class Resource(Func<ValueTask> close) : IAsyncDisposable { public ValueTask DisposeAsync() => close(); }
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(ServiceRegistry registry, string id, PluginAttribute declaration)
    {
        var context = new PluginContext(new() { Id = id, Plugin = id, Source = "test" }, registry,
            new EventBus(NullLogger<EventBus>.Instance), NullLogger.Instance);
        registry.StageOwner(context.NativeServices!.Activation);
        context.BindServices(declaration);
        return context;
    }
    private static void Commit(ServiceRegistry registry, PluginContext context) =>
        Assert.Empty(registry.CommitOwner(context.NativeServices!.Activation)());

    [Fact]
    public async Task InProgressConsumerRetainsProviderUntilItsCleanupFinishes()
    {
        var registry = new ServiceRegistry(16); var alive = true;
        var provider = Context(registry, "provider", new("provider") { Provides = [typeof(IValue)] });
        await provider.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(() => { alive = false; return ValueTask.CompletedTask; })));
        provider.Services.Register<IValue>(new Value(7)); Commit(registry, provider);
        var consumer = Context(registry, "consumer", new("consumer") { Requires = [typeof(IValue)] });
        var entered = Signal(); var release = Signal();
        await consumer.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
        { entered.SetResult(); await release.Task; Assert.True(alive); Assert.Equal(7, consumer.Services.Get<IValue>().Number); })));
        var providerClose = provider.UnwindAsync().AsTask();
        Assert.False(providerClose.IsCompleted); Assert.False(registry.TryGet<IValue>(out _)); Assert.True(alive);
        var consumerClose = consumer.UnwindAsync().AsTask(); await entered.Task.WaitAsync(Limit);
        try { Assert.False(providerClose.IsCompleted); Assert.True(alive); }
        finally { release.TrySetResult(); }
        await consumerClose.WaitAsync(Limit); await providerClose.WaitAsync(Limit);
        Assert.False(alive); Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task UnknownConsumerCleanupFailureKeepsTheProviderLease()
    {
        var registry = new ServiceRegistry(16); var disposed = 0;
        var provider = Context(registry, "provider", new("provider") { Provides = [typeof(IValue)] });
        provider.Effect(() => disposed++); provider.Services.Register<IValue>(new Value(1)); Commit(registry, provider);
        var consumer = Context(registry, "consumer", new("consumer") { Requires = [typeof(IValue)] });
        var calls = 0; consumer.Effect(() => { calls++; throw new InvalidOperationException("unknown cleanup"); });
        await Assert.ThrowsAsync<AggregateException>(() => consumer.UnwindAsync().AsTask());
        var waiting = provider.UnwindAsync().AsTask();
        Assert.False(waiting.IsCompleted); Assert.Equal(0, disposed);
        await Assert.ThrowsAsync<AggregateException>(() => consumer.UnwindAsync().AsTask());
        Assert.Equal(1, calls); Assert.Equal(1, provider.NativeServices!.Activation.Publication.Dependents);
        Assert.Equal(1, consumer.Services.Get<IValue>().Number);
    }

    [Fact]
    public async Task RetiredDecoratorStaysUsableForOldConsumersButNotNewSelections()
    {
        var registry = new ServiceRegistry(24); var released = false;
        var provider = Context(registry, "base", new("base") { Provides = [typeof(IValue)] });
        provider.Services.RegisterDecoratable<IValue>(new Value(1)); Commit(registry, provider);
        var decorator = Context(registry, "layer", new("layer") { Decorates = [typeof(IValue)] });
        decorator.Effect(() => released = true); decorator.Services.Decorate<IValue>(inner => new Layer(inner)); Commit(registry, decorator);
        var old = Context(registry, "old", new("old") { Requires = [typeof(IValue)] });
        var closing = decorator.UnwindAsync().AsTask();
        Assert.False(closing.IsCompleted); Assert.False(released); Assert.Equal(2, old.Services.Get<IValue>().Number);
        var next = Context(registry, "next", new("next") { Requires = [typeof(IValue)] });
        Assert.Equal(1, next.Services.Get<IValue>().Number);
        await old.UnwindAsync(); await closing.WaitAsync(Limit); Assert.True(released);
        await next.UnwindAsync(); await provider.UnwindAsync(); Assert.Equal(0, registry.RetainedMetadataCount);
    }
}
