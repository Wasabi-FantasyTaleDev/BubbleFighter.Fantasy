using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks that native fixture teardown releases its process-wide dialect lookup.</summary>
public sealed class DialectStateLifetimeTests
{
    /// <summary>Shutdown and unload/reload remove only the current fixture's lookup without starting an SDK process.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DialectLookupFollowsItsActivationLifetime(bool reload)
    {
        var key = Guid.NewGuid().ToString("N");
        var catalog = PluginCatalog.ScanAssemblies([
            typeof(MessageRouterPlugin).Assembly, typeof(TestDialect).Assembly,
        ]);
        var host = new PluginHost([
            new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" },
            new PluginRow
            {
                Id = "dialect", Plugin = "test.dialect", Source = "test",
                Config = new Dictionary<string, object?> { ["key"] = key },
            },
        ], catalog, NullLoggerFactory.Instance);
        Assert.False(TestDialect.States.ContainsKey(key));
        try
        {
            await host.StartAsync(default);
            Assert.True(TestDialect.States.TryGetValue(key, out var first));
            if (reload)
            {
                await host.UnloadAsync("dialect", default);
                Assert.False(TestDialect.States.ContainsKey(key));
                await host.LoadAsync("dialect", default);
                Assert.True(TestDialect.States.TryGetValue(key, out var second));
                Assert.NotSame(first, second);
            }
            await host.ShutdownAsync(default);
            Assert.False(TestDialect.States.ContainsKey(key));
        }
        finally
        {
            try { await host.ShutdownAsync(default); }
            finally { TestDialect.States.TryRemove(key, out _); }
        }
    }
}
