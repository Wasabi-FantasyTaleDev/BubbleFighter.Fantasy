using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BubbleFighter.Fantasy.Loader.Remote.Tests.ServiceProviderTests;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks that teardown still reaches an SDK plugin whose bounded callback slots are all in flight.</summary>
[Collection("SDK compilation")]
public sealed class ControlReserveTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>Ordinary requests keep the configured bound; deactivation is admitted without a response slot being free.</summary>
    [Fact]
    public async Task DeactivateIsAdmittedBeyondTheOrdinaryInFlightBound()
    {
        var options = new TransportOptions(MaxLineBytes: 4096, MaxInFlight: 2, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        await using var process = RemoteProcess.Start(new SilentChannel(), options, NullLogger.Instance, new SilentSink(), "test");

        // Two ordinary requests occupy the whole bound and never answer, so a third one is refused.
        var first = process.RequestAsync("deliver", new { }, CancellationToken.None);
        var second = process.RequestAsync("services.invoke", new { }, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<RemoteBackpressureException>(
            () => process.RequestAsync("deliver", new { }, CancellationToken.None));
        Assert.Contains("2 requests in flight", refused.Message);

        // Deactivation uses the control reserve: it is written and waits for the response instead of failing above the bound.
        var control = process.RequestAsync("deactivate", new { }, CancellationToken.None);
        Assert.False(control.IsCompleted);

        await process.DisposeAsync();
        await Assert.ThrowsAnyAsync<RemoteProcessException>(() => control);
        await Assert.ThrowsAnyAsync<RemoteProcessException>(() => first);
        await Assert.ThrowsAnyAsync<RemoteProcessException>(() => second);
    }

    /// <summary>Two long-running deliveries fill max_in_flight, and deactivation still unloads the row.</summary>
    [PythonFact]
    public async Task DeactivationUsesTheControlReserveWhenCallbacksFillTheSlots()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness(
            [EventBridgeTests.EventsEntry("python", false)],
            new Dictionary<string, object?> { ["max_in_flight"] = 2 },
            initialRows:
            [
                Row("policy", "test.sdk-event-policy"),
                Row("worker", "conformance.events", ("behavior", "deliver-hold"), ("modes", Array.Empty<string>())),
            ]);
        await host.StartAsync();

        // Two admitted deliveries hold both callback slots until the row starts stopping.
        var first = host.DispatchLoginAsync("alice");
        var second = host.DispatchLoginAsync("bob");
        await WaitForCountAsync(host, "deliver-entered", 2);
        await host.Host.UnloadAsync("worker", default).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginState.Unloaded, host.Instance("worker").State);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-deactivated:worker", StringComparison.Ordinal));

        // Releasing the row cancels the in-flight deliveries; either outcome proves the plugin answered instead of
        // rejecting the deactivate for exceeding its callback bound.
        var failures = await Task.WhenAll(Record.ExceptionAsync(() => first), Record.ExceptionAsync(() => second));
        Assert.All(failures, failure => Assert.True(failure is null or OperationCanceledException,
            $"An admitted delivery failed with {failure}"));
        Assert.Null(await host.ShutdownAsync());
    }

    private static async Task WaitForCountAsync(RemoteHarness host, string marker, int count)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (host.Loggers.Entries.Count(entry => entry.Contains(marker, StringComparison.Ordinal)) < count)
            await Task.Delay(20, timeout.Token);
    }
}
