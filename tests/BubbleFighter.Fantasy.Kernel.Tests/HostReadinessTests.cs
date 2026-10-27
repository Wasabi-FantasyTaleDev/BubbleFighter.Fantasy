using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>The host's readiness service: not ready after row activation alone, ready once the server command marks it.</summary>
public sealed class HostReadinessTests
{
    [Fact]
    public async Task ReadinessIsSetOnlyByMarkReadyAndStaysSet()
    {
        var host = new PluginHost([new PluginRow { Id = "probe", Plugin = "readiness.probe", Source = "test" }],
            PluginCatalog.ScanAssemblies([typeof(HostReadinessTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            var readiness = Assert.IsAssignableFrom<IHostReadiness>(Probe.Seen);
            Assert.False(readiness.IsReady);
            host.MarkReady();
            Assert.True(readiness.IsReady);
            host.MarkReady();
            Assert.True(readiness.IsReady);
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Plugin("readiness.probe", Description = "Captures the host readiness service.", Requires = [typeof(IHostReadiness)])]
    public sealed class Probe : IPlugin
    {
        internal static IHostReadiness? Seen;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Seen = context.Services.Get<IHostReadiness>();
            return ValueTask.CompletedTask;
        }
    }
}
