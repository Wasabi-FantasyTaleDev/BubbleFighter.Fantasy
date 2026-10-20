using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginFailureTests
{
    public interface IProvider { }
    public interface IAbsent { }
    public sealed class Provider : IProvider { }
    private static readonly ConcurrentDictionary<string, bool> Disposed = new();

    [Plugin("failure.provider", Provides = [typeof(IProvider)])]
    public sealed class ProviderPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Disposed[context.Row.Id] = false;
            context.Effect(context.Services.Register<IProvider>(new Provider()));
            context.Effect(() => Disposed[context.Row.Id] = true);
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("failure.throw", Requires = [typeof(IProvider)])]
    public sealed class ThrowPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(() => Disposed[context.Row.Id] = true);
            throw new InvalidOperationException("activation failure");
        }
    }

    [Plugin("failure.stall", Requires = [typeof(IAbsent)])]
    public sealed class StallPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Plugin("failure.cancel", Requires = [typeof(IProvider)])]
    public sealed class CancelPlugin : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(() => Disposed[context.Row.Id] = true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    [Theory]
    [InlineData("failure.throw")]
    [InlineData("failure.stall")]
    public async Task FailedStartUnwindsOnlyRowsStartedByThatAttempt(string failure)
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "failure.provider"), Row(id + "bad", failure)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.True(Disposed[id]);
        Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
        await host.LoadAsync(id, CancellationToken.None);
        Assert.False(Disposed[id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.False(Disposed[id]);
        Assert.Equal(PluginState.Active, host.Instances.Single(row => row.RowId == id).State);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationUnwindsPartialActivationAndEarlierRows()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "failure.provider"), Row(id + "cancel", "failure.cancel")]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(cancellation.Token).AsTask());
        Assert.True(Disposed[id]);
        Assert.True(Disposed[id + "cancel"]);
        Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedStartDoesNotActivateLiveRowsAgain()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "failure.provider")]);
        await host.StartAsync(CancellationToken.None);
        await host.StartAsync(CancellationToken.None);
        Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
        await host.ShutdownAsync(CancellationToken.None);
        Assert.True(Disposed[id]);
    }

    [Fact]
    public void DuplicateRowIdsFailBeforeActivation()
    {
        Assert.Throws<ConfigException>(() => Host([Row("same", "failure.provider"), Row("same", "failure.throw")]));
    }

    private static PluginHost Host(PluginRow[] rows) => new(rows,
        PluginCatalog.ScanAssemblies([typeof(PluginFailureTests).Assembly]), NullLoggerFactory.Instance);

    private static PluginRow Row(string id, string plugin) => new() { Id = id, Plugin = plugin, Source = "test" };
}
