using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BubbleFighter.Fantasy.Kernel.Tests;
public sealed class ServiceOwnerAttributionTests
{
    public interface IValue { int Number { get; } }
    private sealed record Value(int Number) : IValue;
    private static readonly ConcurrentDictionary<string, IPluginContext> Contexts = new();
    private static readonly ConcurrentDictionary<string, string> Targets = new();
    [Plugin("access.owner", ConditionalProvides = [typeof(IValue)])]
    public sealed class Owner : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Contexts[context.Row.Id] = context;
            context.Effect(() => Contexts.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }
    [Plugin("access.misattributed", Provides = [typeof(IValue)])]
    public sealed class Misattributed : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Contexts[Targets[context.Row.Id]].Services.Register<IValue>(new Value(7));
            return ValueTask.CompletedTask;
        }
    }
    [Fact]
    public async Task AnotherOwnersPublicationCannotSatisfyOrBeRolledBackAsTheCurrentRowsPromise()
    {
        var owner = Guid.NewGuid().ToString("N");
        var failing = Guid.NewGuid().ToString("N");
        Targets[failing] = owner;
        var host = new PluginHost([
            new PluginRow { Id = owner, Plugin = "access.owner", Source = "test" },
            new PluginRow { Id = failing, Plugin = "access.misattributed", Source = "test", Disabled = true }],
            PluginCatalog.ScanAssemblies([typeof(ServiceOwnerAttributionTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(failing, default).AsTask());
            Assert.Contains("not published", failure.Message);
            Assert.Equal(PluginState.Failed, host.Instances.Single(row => row.RowId == failing).State);
            Assert.Equal(PluginState.Active, host.Instances.Single(row => row.RowId == owner).State);
            Assert.Equal(7, Contexts[owner].Services.Get<IValue>().Number);
        }
        finally { await host.ShutdownAsync(default); Targets.TryRemove(failing, out _); }
        Assert.False(Contexts.ContainsKey(owner));
    }
}
