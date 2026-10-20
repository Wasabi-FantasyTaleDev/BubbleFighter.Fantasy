using System.Runtime.CompilerServices;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks that a shared SDK process does not retain the contexts of rows it no longer serves.</summary>
[Collection("SDK compilation")]
public sealed class ServiceSourceLifetimeTests
{
    /// <summary>Removing a row releases its fault observer even while other rows could continue using the process.</summary>
    [Fact]
    public async Task RetiredActivationCanBeCollectedWhileItsSourceRemainsAlive()
    {
        using var process = new FakePlugin();
        var config = RemoteLoaderConfig.Parse(new PluginRow
        {
            Id = "source", Plugin = "loader.process", Source = "test",
            Config = new Dictionary<string, object?> { ["plugins"] = new List<object?> { process.Entry("normal") } },
        });
        await using var source = (RemoteSource)await RemoteSource.AcquireAsync(config.Entries[0], config, NullLogger.Instance, default);
        var retired = Retire(source);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(retired.TryGetTarget(out _));
        Assert.False(source.IsFaulted);
    }

    /// <summary>A shared process failure reaches every registered row, excludes retired rows and refuses new rows.</summary>
    [Fact]
    public async Task ProcessFailureNotifiesOnlyItsCurrentRows()
    {
        using var process = new FakePlugin();
        var config = RemoteLoaderConfig.Parse(new PluginRow
        {
            Id = "source", Plugin = "loader.process", Source = "test",
            Config = new Dictionary<string, object?> { ["plugins"] = new List<object?> { process.Entry("crash-deliver") } },
        });
        await using var source = (RemoteSource)await RemoteSource.AcquireAsync(config.Entries[0], config, NullLogger.Instance, default);
        var retired = Create(source);
        var first = Create(source);
        var second = Create(source);
        var late = Create(source);
        try
        {
            source.Add(retired);
            source.Add(first);
            source.Add(second);
            source.Remove(retired);
            await Assert.ThrowsAsync<RemoteProcessException>(() => source.RequestAsync(first, "deliver", new { }, default));
            var cause = await source.Faulted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(cause, await first.Faulted.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(cause, await second.Faulted.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(retired.Faulted.IsCompleted);
            Assert.Throws<RemoteProcessException>(() => source.Add(late));
        }
        finally
        {
            foreach (var activation in new[] { retired, first, second, late })
            {
                source.Remove(activation);
                ((PluginContext)activation.Context).Unwind();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<RemoteActivation> Retire(RemoteSource source)
    {
        var activation = Create(source);
        source.Add(activation);
        source.Remove(activation);
        ((PluginContext)activation.Context).Unwind();
        return new WeakReference<RemoteActivation>(activation);
    }

    private static RemoteActivation Create(RemoteSource source)
    {
        var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
            { id = "test.retired", version = "1.0.0" }), "test", 2);
        var context = new PluginContext(new PluginRow { Id = "retired", Plugin = "test.retired", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        return new RemoteActivation(source, definition, context, 8, 8, TimeSpan.FromSeconds(10));
    }
}
