using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Tests for <see cref="PluginHost"/> activation, effects, and unwinding.</summary>
public sealed class PluginHostTests
{
    public interface ITestService { }

    public sealed class TestService : ITestService { }

    public interface INeverRegistered { }

    private static readonly List<string> ActivationOrder = [];
    private static readonly List<string> DisposeOrder = [];

    [Plugin("test.service", Provides = [typeof(ITestService)])]
    public sealed class ServicePlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            lock (ActivationOrder) ActivationOrder.Add(context.Row.Id);
            context.Effect(context.Services.Register<ITestService>(new TestService()));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.consumer", Requires = [typeof(ITestService)])]
    public sealed class ConsumerPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            lock (ActivationOrder) ActivationOrder.Add(context.Row.Id);
            _ = context.Services.Get<ITestService>();
            context.Effect(() => { lock (DisposeOrder) DisposeOrder.Add(context.Row.Id + ":first"); });
            context.Effect(() => { lock (DisposeOrder) DisposeOrder.Add(context.Row.Id + ":second"); });
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.needy", Requires = [typeof(INeverRegistered)])]
    public sealed class NeedyPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    public interface IPeerService { }

    public sealed class PeerService : IPeerService { }

    [Plugin("test.peer", OptionalMany = [typeof(IPeerService)], ConditionalProvidesMany = [typeof(IPeerService)])]
    public sealed class PeerPlugin : IPlugin
    {
        internal static int PeerActivations;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PeerActivations);
            context.Effect(context.Services.RegisterMany<IPeerService>(new PeerService()));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Instances of one definition that read what they also provide neither wait for nor restart each other.</summary>
    [Fact]
    public async Task SiblingInstancesOfOneDefinitionDoNotDeadlockOnOptionalReads()
    {
        PeerPlugin.PeerActivations = 0;
        // Both rows become pending together, as they do when a multi-build server profile is admitted.
        var host = new PluginHost([Row("peer-a", "test.peer") with { Disabled = true }, Row("peer-b", "test.peer") with { Disabled = true }],
            Catalog(), NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        var result = await host.ReconcileConfigurationAsync([Row("peer-a", "test.peer"), Row("peer-b", "test.peer")])
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(2, host.Instances.Count);
        Assert.Equal(2, Volatile.Read(ref PeerPlugin.PeerActivations));
        Assert.All(host.Instances, i => Assert.Equal(PluginState.Active, i.State));
        await host.ShutdownAsync(CancellationToken.None);
    }

    private static PluginCatalog Catalog() =>
        PluginCatalog.ScanAssemblies([typeof(PluginHostTests).Assembly]);

    private static PluginRow Row(string id, string plugin) =>
        new() { Id = id, Plugin = plugin, Source = "test" };

    [Fact]
    public async Task ActivationFollowsDependenciesRegardlessOfRowOrder()
    {
        lock (ActivationOrder) ActivationOrder.Clear();
        // Consumer listed before the provider it depends on.
        var host = new PluginHost(
            [Row("consumer-row", "test.consumer"), Row("service-row", "test.service")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);

        string[] order;
        lock (ActivationOrder) order = [.. ActivationOrder];
        Assert.Equal(["service-row", "consumer-row"], order);
        Assert.All(host.Instances, i => Assert.Equal(PluginState.Active, i.State));
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MissingDependencyFailsWithBothSidesNamed()
    {
        var host = new PluginHost(
            [Row("needy-row", "test.needy")],
            Catalog(),
            NullLoggerFactory.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains("needy-row", ex.Message);
        Assert.Contains("test.needy", ex.Message);
        Assert.Contains(nameof(INeverRegistered), ex.Message);
    }

    [Fact]
    public async Task UnloadRunsEffectsInReverseOrder()
    {
        lock (DisposeOrder) DisposeOrder.Clear();
        var host = new PluginHost(
            [Row("service-row", "test.service"), Row("consumer-row", "test.consumer")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await host.UnloadAsync("consumer-row", CancellationToken.None);

        string[] order;
        lock (DisposeOrder) order = [.. DisposeOrder];
        Assert.Equal(["consumer-row:second", "consumer-row:first"], order);
        Assert.Equal(PluginState.Unloaded, host.Instances.Single(i => i.RowId == "consumer-row").State);
    }

    [Fact]
    public async Task UnloadRemovesPublishedServices()
    {
        var host = new PluginHost(
            [Row("service-row", "test.service")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        Assert.True(host.Instances.Single().State == PluginState.Active);
        await host.UnloadAsync("service-row", CancellationToken.None);
        // The row is unloadable again without duplicate-registration errors.
        await host.LoadAsync("service-row", CancellationToken.None);
        Assert.Equal(PluginState.Active, host.Instances.Single().State);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DisabledRowsAreNotActivated()
    {
        var disabled = new PluginRow { Id = "off", Plugin = "test.service", Disabled = true, Source = "test" };
        var host = new PluginHost([disabled], Catalog(), NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        Assert.Equal(PluginState.Unloaded, host.Instances.Single().State);
    }

    [Fact]
    public async Task UnknownPluginFailsLoud()
    {
        var host = new PluginHost(
            [Row("ghost", "does.not.exist")],
            Catalog(),
            NullLoggerFactory.Instance);
        var ex = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains("ghost", ex.Message);
        Assert.Contains("does.not.exist", ex.Message);
    }

    [Fact]
    public async Task UnloadRefusesWhenDependentsAreLive()
    {
        var host = new PluginHost(
            [Row("service-row", "test.service"), Row("consumer-row", "test.consumer")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.UnloadAsync("service-row", CancellationToken.None).AsTask());
        Assert.Contains("service-row", ex.Message);
        Assert.Contains("consumer-row", ex.Message);
        Assert.Equal(PluginState.Active, host.Instances.Single(i => i.RowId == "service-row").State);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnloadSucceedsOnceDependentsAreGone()
    {
        var host = new PluginHost(
            [Row("service-row", "test.service"), Row("consumer-row", "test.consumer")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        await host.UnloadAsync("consumer-row", CancellationToken.None);
        await host.UnloadAsync("service-row", CancellationToken.None);
        Assert.All(host.Instances, i => Assert.Equal(PluginState.Unloaded, i.State));
    }

    [Fact]
    public async Task ReloadCascadesDependentsInActivationOrder()
    {
        lock (ActivationOrder) ActivationOrder.Clear();
        var host = new PluginHost(
            [Row("service-row", "test.service"), Row("consumer-row", "test.consumer")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);

        var result = await host.ReloadAsync("service-row", CancellationToken.None);

        Assert.Equal(["service-row", "consumer-row"], result.ReloadedRowIds);
        string[] order;
        lock (ActivationOrder) order = [.. ActivationOrder];
        Assert.Equal(["service-row", "consumer-row", "service-row", "consumer-row"], order);
        Assert.All(host.Instances, i => Assert.Equal(PluginState.Active, i.State));
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReloadWithoutDependentsAffectsOnlyTheTarget()
    {
        lock (ActivationOrder) ActivationOrder.Clear();
        var host = new PluginHost(
            [Row("service-row", "test.service"), Row("consumer-row", "test.consumer")],
            Catalog(),
            NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);

        var result = await host.ReloadAsync("consumer-row", CancellationToken.None);

        Assert.Equal(["consumer-row"], result.ReloadedRowIds);
        string[] order;
        lock (ActivationOrder) order = [.. ActivationOrder];
        Assert.Equal(["service-row", "consumer-row", "consumer-row"], order);
        await host.ShutdownAsync(CancellationToken.None);
    }
}
