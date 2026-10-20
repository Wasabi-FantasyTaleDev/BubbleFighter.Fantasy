using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks that stopping a watcher cannot hide a failure already recorded by its activation.</summary>
public sealed class ActivationFaultTests
{
    /// <summary>A recorded failure wins even when shutdown cancels the pending observation immediately afterward.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedFaultSurvivesWatcherCancellation(bool observeAfterCancellation)
    {
        var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
            { id = "test.fault", version = "1.0.0" }), "test", 2);
        var context = new PluginContext(new PluginRow { Id = "fault", Plugin = "test.fault", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            for (var iteration = 0; iteration < 256; iteration++)
            {
                var activation = new RemoteActivation(null!, definition, context, 8, 8, TimeSpan.FromSeconds(10));
                using var stopping = new CancellationTokenSource();
                var pending = observeAfterCancellation ? null : activation.WaitForFaultAsync(stopping.Token);
                var failure = new RemoteTimeoutException("The callback deadline expired.");
                activation.Fault(failure);
                stopping.Cancel();
                var observed = pending ?? activation.WaitForFaultAsync(stopping.Token);
                Assert.Same(failure, await observed.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        finally { context.Unwind(); }
    }

    /// <summary>A healthy activation still permits its observer to stop without inventing a row failure.</summary>
    [Fact]
    public async Task HealthyWatcherStopsWithItsCancellationToken()
    {
        var definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new
            { id = "test.fault", version = "1.0.0" }), "test", 2);
        var context = new PluginContext(new PluginRow { Id = "fault", Plugin = "test.fault", Source = "test" },
            new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
        try
        {
            var activation = new RemoteActivation(null!, definition, context, 8, 8, TimeSpan.FromSeconds(10));
            using var stopping = new CancellationTokenSource();
            var pending = activation.WaitForFaultAsync(stopping.Token);
            stopping.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(stopping.Token, failure.CancellationToken);
            Assert.False(activation.Faulted.IsCompleted);
        }
        finally { context.Unwind(); }
    }
}
