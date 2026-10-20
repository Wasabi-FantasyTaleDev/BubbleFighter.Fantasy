using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class OptionalDependencyTests
{
    public interface IValue { }
    public interface ICycleA { }
    public interface ICycleB { }
    private static readonly ConcurrentDictionary<string, IValue> Captured = new();
    private static readonly ConcurrentDictionary<string, bool> Availability = new();

    [Plugin("optional.provider", Provides = [typeof(IValue)])]
    public sealed class Provider : IPlugin, IValue
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IValue>(this));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("optional.consumer", Optional = [typeof(IValue)])]
    public sealed class Consumer : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Availability[context.Row.Id] = context.Services.TryGet<IValue>(out var value);
            if (value is not null)
                Captured[context.Row.Id] = value;
            context.Effect(() => Captured.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("optional.conditional", ConditionalProvides = [typeof(IValue)])]
    public sealed class Conditional : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Plugin("optional.broken", Provides = [typeof(IValue)])]
    public sealed class Broken : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Plugin("optional.cycle-a", Optional = [typeof(ICycleB)], Provides = [typeof(ICycleA)])]
    public sealed class CycleA : IPlugin, ICycleA
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<ICycleA>(this));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("optional.cycle-b", Optional = [typeof(ICycleA)], Provides = [typeof(ICycleB)])]
    public sealed class CycleB : IPlugin, ICycleB
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<ICycleB>(this));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SelectedProviderPrecedesConsumerAndParticipatesInReloadAndUnload()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "optional.consumer"), Row("provider", "optional.provider")]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.True(Availability[id]);
            var first = Captured[id];
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("provider", CancellationToken.None).AsTask());
            Assert.Contains(id, error.Message);
            Assert.Equal(new[] { "provider", id }, (await host.ReloadAsync("provider", CancellationToken.None)).ReloadedRowIds);
            Assert.NotSame(first, Captured[id]);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrAbsentProvidersAllowAnExplicitUnavailableState(bool includeDisabled)
    {
        var id = Guid.NewGuid().ToString("N");
        var rows = new List<PluginRow> { Row(id, "optional.consumer") };
        if (includeDisabled)
            rows.Add(Row("disabled", "optional.provider") with { Disabled = true });
        var host = Host([.. rows]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.False(Availability[id]);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ConditionalProviderMayCompleteWithoutPublishing()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "optional.consumer"), Row("conditional", "optional.conditional")]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            Assert.False(Availability[id]);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PromisedServiceNotPublishedFailsAndRollsBack()
    {
        var host = Host([Row("broken", "optional.broken")]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains(nameof(IValue), error.Message);
        Assert.Contains("broken", error.Message);
        Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
    }

    [Fact]
    public async Task OptionalCycleNamesBothProviderRows()
    {
        var host = Host([Row("a", "optional.cycle-a"), Row("b", "optional.cycle-b")]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None).AsTask());
        Assert.Contains("row 'a'", error.Message);
        Assert.Contains("row 'b'", error.Message);
        Assert.Contains("optional provider", error.Message);
        Assert.DoesNotContain(host.Instances, row => row.State == PluginState.Active);
    }

    [Fact]
    public async Task LateProviderCannotSilentlyChangeAnActiveConsumersFallback()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "optional.consumer"), Row("provider", "optional.provider") with { Disabled = true }]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("provider", CancellationToken.None).AsTask());
            Assert.Contains(id, error.Message);
            Assert.Contains("Unload", error.Message);
            Assert.False(Availability[id]);
            await host.UnloadAsync(id, CancellationToken.None);
            await host.LoadAsync("provider", CancellationToken.None);
            await host.LoadAsync(id, CancellationToken.None);
            Assert.True(Availability[id]);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static PluginHost Host(PluginRow[] rows) => new(rows,
        PluginCatalog.ScanAssemblies([typeof(OptionalDependencyTests).Assembly]), NullLoggerFactory.Instance);

    private static PluginRow Row(string id, string plugin) => new() { Id = id, Plugin = plugin, Source = "test" };
}
