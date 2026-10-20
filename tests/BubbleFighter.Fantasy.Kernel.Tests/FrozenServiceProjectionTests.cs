using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BubbleFighter.Fantasy.Kernel.Tests;
public sealed class FrozenServiceProjectionTests
{
    public interface IValue { int Number { get; } Task<int> ReadAsync(Task gate); }
    public interface IFlag { bool Enabled { get; } }
    private sealed record Value(int Number) : IValue
    { public async Task<int> ReadAsync(Task gate) { await gate; return Number; } }
    private sealed record Flag(bool Enabled) : IFlag;
    private sealed class Layer(IValue inner, int factor, int addition) : IValue
    {
        public int Number => inner.Number * factor + addition;
        public async Task<int> ReadAsync(Task gate) { await gate; return (await inner.ReadAsync(gate)) * factor + addition; }
    }
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ServiceBinding Binding(ServiceRegistry registry) => Assert.Single(registry.CaptureBindings()
        .Resolve(registry.Runtime.RootScope.Resolve(typeof(IValue))));
    private static IValue Capture(ServiceRegistry registry) => (IValue)Binding(registry).Instance;

    [Fact]
    public void CapturedTargetsKeepTheirLayersAfterLaterLayerEditsAndBaseRevocation()
    {
        var registry = new ServiceRegistry(8);
        var basis = registry.RegisterDecoratable<IValue>(new Value(3));
        var dynamicReference = registry.Get<IValue>();
        var plain = Capture(registry);
        var factories = 0;
        var first = registry.Decorate<IValue>(inner => { factories++; return new Layer(inner, 2, 1); });
        var oneLayer = Capture(registry);
        var second = registry.Decorate<IValue>(inner => { factories++; return new Layer(inner, 3, 4); }, 10);
        var twoLayers = Capture(registry);
        Assert.Equal(25, twoLayers.Number);
        Assert.Equal(25, dynamicReference.Number);
        first.Dispose();
        Assert.Equal(13, dynamicReference.Number);
        Assert.Equal(25, twoLayers.Number);
        Assert.Equal(7, oneLayer.Number);
        basis.Dispose();
        second.Dispose();
        Assert.Throws<ServiceUnavailableException>(() => dynamicReference.Number);
        Assert.Equal(3, plain.Number);
        Assert.Equal(7, oneLayer.Number);
        Assert.Equal(25, twoLayers.Number);
        Assert.Equal(2, factories);
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task FrozenAsyncCallsRetainTheirInvocationSnapshotAcrossAwait()
    {
        var registry = new ServiceRegistry(8);
        var basis = registry.RegisterDecoratable<IValue>(new Value(3));
        var first = registry.Decorate<IValue>(inner => new Layer(inner, 2, 1));
        var old = Capture(registry);
        var gate = Signal();
        var inFlight = old.ReadAsync(gate.Task);
        var second = registry.Decorate<IValue>(inner => new Layer(inner, 3, 4), 10);
        var recent = Capture(registry);
        var newerCall = recent.ReadAsync(gate.Task);
        first.Dispose();
        second.Dispose();
        basis.Dispose();
        gate.TrySetResult();
        Assert.Equal(7, await inFlight.WaitAsync(Limit));
        Assert.Equal(25, await newerCall.WaitAsync(Limit));
        Assert.Equal(7, await old.ReadAsync(Task.CompletedTask));
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task PendingFactoryLeavesOldBindingVisibleWithoutHoldingTheRegistryLock()
    {
        var registry = new ServiceRegistry(8);
        var basis = registry.RegisterDecoratable<IValue>(new Value(1));
        var before = Binding(registry);
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        var adding = Task.Run(() => registry.Decorate<IValue>(inner =>
        { entered.TrySetResult(); finish.Wait(); return new Layer(inner, 1, 10); }));
        await entered.Task.WaitAsync(Limit);
        try
        {
            using var unrelated = await Task.Run(() => registry.Register<IFlag>(new Flag(true))).WaitAsync(Limit);
            Assert.Same(before, Binding(registry));
            Assert.Equal(1, Capture(registry).Number);
            Assert.Throws<InvalidOperationException>(() => basis.Dispose());
            var otherFactoryCalls = 0;
            Assert.Throws<InvalidOperationException>(() => registry.Decorate<IValue>(inner =>
            { otherFactoryCalls++; return new Layer(inner, 1, 20); }));
            Assert.Equal(0, otherFactoryCalls);
        }
        finally { finish.Set(); }
        var layer = await adding.WaitAsync(Limit);
        Assert.NotEqual(before.Revision, Binding(registry).Revision);
        Assert.Equal(11, Capture(registry).Number);
        layer.Dispose();
        basis.Dispose();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }

    [Fact]
    public void FailedFactoriesDoNotChangeThePublishedRevisionOrConsumeSlots()
    {
        var registry = new ServiceRegistry(2);
        using var basis = registry.RegisterDecoratable<IValue>(new Value(1));
        var before = Binding(registry);
        Assert.Throws<ArgumentException>(() => registry.Decorate<IValue>(_ => throw new ArgumentException("factory-failed")));
        Assert.Same(before, Binding(registry));
        Assert.Equal(1, registry.RetainedMetadataCount);
        using var layer = registry.Decorate<IValue>(inner => new Layer(inner, 1, 1));
        Assert.Equal(2, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task ConcurrentSnapshotsNeverMixLayerMetadataAndInvocationTargets()
    {
        var registry = new ServiceRegistry(4);
        using var basis = registry.RegisterDecoratable<IValue>(new Value(1));
        var start = Signal();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 200; i++)
            { using var layer = registry.Decorate<IValue>(inner => new Layer(inner, 1, 10)); }
        });
        var reader = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < 1000; i++)
            {
                var binding = Binding(registry);
                Assert.Equal(binding.Decorations.Count == 0 ? 1 : 11, ((IValue)binding.Instance).Number);
            }
        });
        start.TrySetResult();
        await Task.WhenAll(reader, writer).WaitAsync(Limit);
        Assert.Equal(1, registry.RetainedMetadataCount);
    }

    [Fact]
    public async Task ActualContextDecorationsCarryTheDecoratingActivationIdentity()
    {
        var registry = new ServiceRegistry(8);
        var events = new EventBus(NullLogger.Instance);
        var provider = new PluginContext(new PluginRow { Id = "base", Plugin = "test", Source = "test" }, registry, events, NullLogger.Instance);
        var decorator = new PluginContext(new PluginRow { Id = "layer", Plugin = "test", Source = "test" }, registry, events, NullLogger.Instance);
        provider.Services.RegisterDecoratable<IValue>(new Value(1));
        decorator.Services.Decorate<IValue>(inner => new Layer(inner, 1, 2));
        var binding = Binding(registry);
        Assert.Equal(provider.NativeServices!.Activation, binding.Registration.Provider);
        Assert.Equal(decorator.NativeServices!.Activation, Assert.Single(binding.Decorations).Registration.Provider);
        await decorator.UnwindAsync();
        Assert.Empty(Binding(registry).Decorations);
        Assert.Equal(3, ((IValue)binding.Instance).Number);
        await provider.UnwindAsync();
        Assert.Equal(0, registry.RetainedMetadataCount);
    }
}
