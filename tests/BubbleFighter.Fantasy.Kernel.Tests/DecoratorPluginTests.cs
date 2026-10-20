using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class DecoratorPluginTests
{
    public interface IText { string Read(); }
    public sealed class Text : IText { public string Read() => "base"; }
    public sealed class Wrapper(IText inner, string name) : IText
    {
        public string Read() => $"{name}({inner.Read()})";
    }

    private static readonly ConcurrentDictionary<string, IText> Captured = new();
    private static readonly ConcurrentDictionary<string, int> Activations = new();

    [Plugin("decorator.provider", Provides = [typeof(IText)])]
    public sealed class Provider : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.RegisterDecoratable<IText>(new Text()));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("decorator.wrapper", Decorates = [typeof(IText)])]
    public sealed class Decorator : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Decorate<IText>(inner => new Wrapper(inner, context.Row.Id)));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("decorator.consumer", Requires = [typeof(IText)])]
    public sealed class Consumer : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Captured[context.Row.Id] = context.Services.Get<IText>();
            Activations.AddOrUpdate(context.Row.Id, 1, (_, count) => count + 1);
            context.Effect(() => Captured.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("decorator.undeclared", Requires = [typeof(IText)])]
    public sealed class Undeclared : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Decorate<IText>(inner => new Wrapper(inner, "undeclared")));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("decorator.missing", Decorates = [typeof(IText)])]
    public sealed class Missing : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LayerReloadRebindsDependentConsumersWithoutMutatingOldReferences()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row(id, "decorator.consumer"), Row("outer", "decorator.wrapper"),
            Row("inner", "decorator.wrapper"), Row("provider", "decorator.provider")]);
        try
        {
            await host.StartAsync(default);
            var original = Captured[id];
            Assert.Equal("outer(inner(base))", original.Read());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("outer", default).AsTask());
            Assert.Contains(id, error.Message);
            Assert.Same(original, Captured[id]);
            Assert.Equal(1, Activations[id]);
            var inner = await host.ReloadAsync("inner", default);
            Assert.Equal(new[] { "inner", id }, inner.ReloadedRowIds);
            Assert.NotSame(original, Captured[id]);
            Assert.Equal(2, Activations[id]);
            var outer = await host.ReloadAsync("outer", default);
            Assert.Equal(new[] { "outer", id }, outer.ReloadedRowIds);
            Assert.Equal("inner(outer(base))", Captured[id].Read());
            Assert.Equal("outer(inner(base))", original.Read());
            Assert.Equal(3, Activations[id]);
            var beforeBaseReload = Captured[id];
            var reload = await host.ReloadAsync("provider", default);
            Assert.Equal(4, reload.ReloadedRowIds.Count);
            Assert.NotSame(beforeBaseReload, Captured[id]);
            Assert.Equal(4, Activations[id]);
            Assert.Equal("inner(outer(base))", Captured[id].Read());
            Assert.Equal("outer(inner(base))", original.Read());
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Fact]
    public async Task LateLayerLoadIsRefusedWithoutChangingPinnedConsumers()
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([Row("provider", "decorator.provider"), Row(id, "decorator.consumer"),
            Row("later", "decorator.wrapper") with { Disabled = true }]);
        try
        {
            await host.StartAsync(default);
            var original = Captured[id];
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("later", default).AsTask());
            Assert.Contains(id, error.Message);
            Assert.Same(original, Captured[id]);
            Assert.Equal("base", original.Read());
            Assert.Equal(1, Activations[id]);
        }
        finally { await host.ShutdownAsync(default); }
    }
    [Theory]
    [InlineData("decorator.missing")]
    [InlineData("decorator.undeclared")]
    public async Task DecorationMetadataMismatchRollsBackOnlyTheFailingRow(string plugin)
    {
        var id = Guid.NewGuid().ToString("N");
        var host = Host([
            Row("provider", "decorator.provider"), Row(id, "decorator.consumer") with { Disabled = true },
            Row("invalid", plugin) with { Disabled = true },
        ]);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("invalid", CancellationToken.None).AsTask());
            Assert.Contains("Decorates", error.Message);
            await host.LoadAsync(id, CancellationToken.None);
            Assert.Equal("base", Captured[id].Read());
            Assert.Equal(PluginState.Active, host.Instances.Single(row => row.RowId == id).State);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
    }

    private static PluginHost Host(PluginRow[] rows) => new(rows,
        PluginCatalog.ScanAssemblies([typeof(DecoratorPluginTests).Assembly]), NullLoggerFactory.Instance);

    private static PluginRow Row(string id, string plugin) => new() { Id = id, Plugin = plugin, Source = "test" };
}
