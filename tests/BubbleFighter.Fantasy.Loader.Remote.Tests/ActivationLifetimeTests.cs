using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;
using static BubbleFighter.Fantasy.Loader.Remote.Tests.ServiceProviderTests;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks activation cancellation through the native owner and real SDK transports.</summary>
[Collection("SDK compilation")]
public sealed class ActivationLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly EquipmentSelectionApplying Event = new("test/game", 9007199254740993, 13, true);

    /// <summary>Repeated readiness requests cannot consume extra slots from the activation's bounded native resource owner.</summary>
    [Fact]
    public async Task RepeatedReadinessReusesOneOwnedObserver()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create("python", false, key,
            Row("ready", "conformance.events", ("behavior", "lifetime-ready-repeat"), ("modes", Array.Empty<string>())));
        await host.StartAsync();
        Assert.Equal(PluginState.Active, host.Instance("ready").State);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-ready-repeated", StringComparison.Ordinal));
        await host.Host.UnloadAsync("ready", default).AsTask().WaitAsync(Limit);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-deactivated:ready", StringComparison.Ordinal));
        Assert.Null(await host.ShutdownAsync());
    }

    /// <summary>Unload must signal the activation before its admitted callback drains, without cancelling another row in the same process.</summary>
    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    [InlineData("go")]
    [InlineData("rust")]
    public async Task CancellationPrecedesCallbackDrainAndIsIsolatedByActivation(string language)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(language, false, key,
            Row("waiting", "conformance.events", ("behavior", "lifetime-wait"), ("modes", new[] { "async" })),
            Row("other", "conformance.events", ("behavior", "lifetime-alive"), ("modes", new[] { "async" })));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loggers.OnEntry = entry => { if (entry.Contains("lifetime-wait-entered", StringComparison.Ordinal)) entered.TrySetResult(); };
        await host.StartAsync();
        var bus = SdkEventProbe.Contexts[key].Events;
        var dispatch = bus.SerialAsync(Event, default).AsTask();
        await entered.Task.WaitAsync(Limit);
        await host.Host.UnloadAsync("waiting", default).AsTask().WaitAsync(Limit);
        await dispatch.WaitAsync(Limit);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-wait-cancelled", StringComparison.Ordinal));
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-deactivated:waiting", StringComparison.Ordinal));
        Assert.Equal(PluginState.Unloaded, host.Instance("waiting").State);
        Assert.Equal(PluginState.Active, host.Instance("other").State);
        await bus.SerialAsync(Event, default);
        Assert.Equal(2, host.Loggers.Entries.Count(entry => entry.Contains("lifetime-other-alive", StringComparison.Ordinal)));
        Assert.Null(await host.ShutdownAsync());
    }

    /// <summary>SDK rollback cancels its lifetime before running effects, including single-threaded modules.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task FailedActivationCancelsBeforeRollback(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(language, wasm, key);
        await host.StartAsync();
        _ = await Record.ExceptionAsync(() => host.AddRowAsync("failing", "conformance.events",
            new() { ["behavior"] = "lifetime-fail" }));
        Assert.Equal(PluginState.Failed, host.Instance("failing").State);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("lifetime-rollback-cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Loggers.Entries, entry => entry.Contains("rollback did not cancel lifetime", StringComparison.Ordinal));
        _ = await host.ShutdownAsync();
    }

    /// <summary>SDK background work is admitted by the native task owner and is canceled before unload completes.</summary>
    [Fact]
    public async Task PythonTaskUsesNativeOwnerAndJoinsCancellation()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create("python", false, key,
            Row("worker", "conformance.events", ("behavior", "task-wait"), ("modes", Array.Empty<string>())));
        await host.StartAsync();
        // The task starts after activation returns, so its first log line can arrive a little later.
        await LoggedAsync(host, "task-entered");
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("task-entered", StringComparison.Ordinal));
        await host.Host.UnloadAsync("worker", default).AsTask().WaitAsync(Limit);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("task-cancelled", StringComparison.Ordinal));
        Assert.Equal(PluginState.Unloaded, host.Instance("worker").State);
        Assert.Null(await host.ShutdownAsync());
    }

    /// <summary>SDK resource scopes and release callbacks remain owned by the native activation scope.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task SdkResourceScopeUsesNativeOwnerAndReleasesCallback(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(language, wasm, key,
            Row("resource", "conformance.events", ("behavior", "resource-release"), ("modes", Array.Empty<string>())));
        await host.StartAsync();
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("resource-acquired", StringComparison.Ordinal));
        await host.Host.UnloadAsync("resource", default).AsTask().WaitAsync(Limit);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("resource-released", StringComparison.Ordinal));
        Assert.Equal(PluginState.Unloaded, host.Instance("resource").State);
        Assert.Null(await host.ShutdownAsync());
    }

    private static async Task LoggedAsync(RemoteHarness host, string marker)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!host.Loggers.Entries.Any(entry => entry.Contains(marker, StringComparison.Ordinal)))
            await Task.Delay(20, timeout.Token);
    }

    private static RemoteHarness Create(string language, bool wasm, string key, params PluginRow[] rows) =>
        new([EventBridgeTests.EventsEntry(language, wasm)], loader: wasm ? "loader.wasm" : "loader.process", initialRows:
            new[] { Row("policy", "test.sdk-event-policy"), Row("probe", "test.sdk-event-probe", ("key", key)) }.Concat(rows));
}
