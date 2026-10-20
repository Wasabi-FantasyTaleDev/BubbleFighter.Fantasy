using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class MultipleProviderTests
{
    public interface IExtension { string Name { get; } }
    public sealed record Extension(string Name) : IExtension;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<IExtension>> Snapshots = new();

    [Plugin("many.provider", ProvidesMany = [typeof(IExtension)])]
    public sealed class Provider : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.RegisterMany<IExtension>(new Extension(context.Row.Id)));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("many.consumer", RequiresMany = [typeof(IExtension)])]
    public sealed class Consumer : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Snapshots[context.Row.Id] = context.Services.GetAll<IExtension>();
            context.Effect(() => Snapshots.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ConsumerWaitsForAllProvidersAndTracksTheSecondProvidersReload()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row("first", "many.provider"), Row(id, "many.consumer"), Row("second", "many.provider")]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var before = Snapshots[id];
            Assert.Equal(new[] { "first", "second" }, before.Select(item => item.Name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("second", CancellationToken.None).AsTask());
            Assert.Equal(new[] { "second", id }, (await host.ReloadAsync("second", CancellationToken.None)).ReloadedRowIds);
            Assert.Same(before[0], Snapshots[id][0]);
            Assert.NotSame(before[1], Snapshots[id][1]);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LateProviderCannotLeaveAnActiveExtensionSnapshotStale()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row("first", "many.provider"), Row(id, "many.consumer"), Row("second", "many.provider") with { Disabled = true }]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("second", CancellationToken.None).AsTask());
            Assert.Equal(PluginState.Failed, host.Instances.Single(row => row.RowId == "second").State);
            Assert.Equal(PluginState.Active, host.Instances.Single(row => row.RowId == id).State);
            Assert.Single(Snapshots[id]);
            await host.UnloadAsync(id, CancellationToken.None);
            await host.LoadAsync("second", CancellationToken.None);
            await host.LoadAsync(id, CancellationToken.None);
            Assert.Equal(2, Snapshots[id].Count);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static PluginHost Host(PluginRow[] rows) => new(rows,
        PluginCatalog.ScanAssemblies([typeof(MultipleProviderTests).Assembly]), NullLoggerFactory.Instance);

    private static PluginRow Row(string id, string plugin) => new() { Id = id, Plugin = plugin, Source = "test" };
}
