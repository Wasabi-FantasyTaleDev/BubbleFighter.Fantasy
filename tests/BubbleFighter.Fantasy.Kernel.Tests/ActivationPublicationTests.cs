using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ActivationPublicationTests
{
    public interface IValue { int Number { get; } }
    private sealed record Value(int Number) : IValue;
    private sealed record Layer(IValue Inner) : IValue { public int Number => Inner.Number + 1; }
    private sealed class Resource(Action release) : IAsyncDisposable
    { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(ServiceRegistry registry) => new(
        new() { Id = "owner", Plugin = "owner", Source = "test" }, registry,
        new EventBus(NullLogger<EventBus>.Instance), NullLogger.Instance);

    [Fact]
    public async Task APreparedDecoratorCannotCommitAfterItsOwnerIsSuperseded()
    {
        var registry = new ServiceRegistry();
        using var registration = registry.RegisterDecoratable<IValue>(new Value(1));
        var context = Context(registry);
        context.BindServices(new("owner") { Decorates = [typeof(IValue)] });
        var entered = Signal(); var release = Signal();
        var attempt = Task.Run(() => context.Services.Decorate<IValue>(inner =>
        { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return new Layer(inner); }));
        await entered.Task.WaitAsync(Limit);
        context.Supersede(); release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => attempt.WaitAsync(Limit));
        Assert.Equal(1, registry.Get<IValue>().Number);
        Assert.Equal(1, registry.RetainedMetadataCount);
        Assert.Equal(0, context.RetainedEffectCount);
        await context.UnwindAsync();
    }

    [Fact]
    public async Task AvailabilityCallbackCannotPublishAnOwnerItSuperseded()
    {
        var registry = new ServiceRegistry(); var context = Context(registry);
        context.BindServices(new("owner") { Provides = [typeof(IValue)] });
        using var observer = registry.WhenAvailable<IValue>(_ => context.Supersede());
        Assert.Throws<ObjectDisposedException>(() => context.Services.Register<IValue>(new Value(1)));
        Assert.False(registry.TryGet<IValue>(out _));
        Assert.Equal(0, registry.RetainedMetadataCount);
        Assert.Equal(0, context.RetainedEffectCount);
        await context.UnwindAsync();
    }

    [Fact]
    public async Task SealedAcquisitionReleasesLateResourceWithoutStartingOwnerTeardown()
    {
        var scope = new EffectScope(4); var entered = Signal(); var release = Signal(); var disposed = 0;
        var acquire = scope.AcquireAsync(async _ =>
        { entered.TrySetResult(); await release.Task; return new Resource(() => disposed++); }).AsTask();
        await entered.Task.WaitAsync(Limit);
        scope.SealAdmission();
        Assert.False(scope.IsClosing);
        release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquire.WaitAsync(Limit));
        Assert.Equal(1, disposed);
        Assert.Equal(0, scope.RetainedCount);
        await scope.DisposeAsync();
        Assert.Equal(1, disposed);
    }
}
