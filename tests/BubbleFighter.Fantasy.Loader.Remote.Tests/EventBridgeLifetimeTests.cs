using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;
using static BubbleFighter.Fantasy.Loader.Remote.Tests.ServiceProviderTests;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Proves continuation isolation and actual native completion across event cancellation and unload.</summary>
[Collection("SDK compilation")]
public sealed class EventBridgeLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly EquipmentSelectionApplying Event = new("test/game", 9007199254740993, 13, true);

    /// <summary>Raw foreign and expired invocation tokens cannot bypass the captured pipeline's admission.</summary>
    [Fact]
    public async Task ForeignAndExpiredEventContinuationsAreRejectedByTheHost()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "scope");
        await host.StartAsync();
        var bus = SdkEventProbe.Contexts[key].Events;
        var calls = 0;
        ValueTask<int> Terminal(CancellationToken _) { Interlocked.Increment(ref calls); return ValueTask.FromResult(10); }
        Assert.Equal(20, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, Terminal, default));
        Assert.Equal(20, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, Terminal, default));
        Assert.Equal(4, calls);
        Assert.Equal(2, host.Loggers.Entries.Count(entry => entry.Contains("foreign-event-rejected", StringComparison.Ordinal)));
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("expired-event-rejected", StringComparison.Ordinal));
    }

    /// <summary>A callback response cannot release ownership of a continuation already running in native code.</summary>
    [Fact]
    public async Task DetachedNextMustFinishBeforeCallbackOwnershipAndUnloadComplete()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "detach");
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loggers.OnEntry = entry => { if (entry.Contains("detached-event-admitted", StringComparison.Ordinal)) returned.TrySetResult(); };
        await host.StartAsync();
        var state = SdkLifetimeBase.States[key];
        var call = SdkEventProbe.Contexts[key].Events.WaterfallAsync<EquipmentSelectionApplying, int>(Event,
            async ct => { await state.ListByUserNumAsync(1, ct); return 10; }, default).AsTask();
        Task? unload = null;
        try
        {
            await returned.Task.WaitAsync(Limit);
            Assert.True(state.Entered.Task.IsCompleted);
            Assert.False(call.IsCompleted);
            unload = host.Host.UnloadAsync("listener", default).AsTask();
            await state.Cancelled.Task.WaitAsync(Limit);
            Assert.False(unload.IsCompleted);
            state.Release.TrySetResult();
            Assert.Equal(123, await call.WaitAsync(Limit));
            await unload.WaitAsync(Limit);
            Assert.Equal(1, state.Completed);
            Assert.Equal(PluginState.Unloaded, host.Instance("listener").State);
        }
        finally
        {
            state.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => call.WaitAsync(Limit));
            if (unload is not null) _ = await Record.ExceptionAsync(() => unload.WaitAsync(Limit));
        }
    }

    /// <summary>A deadline faults the row but its admitted noncooperative continuation still has to drain.</summary>
    [Fact]
    public async Task CallbackDeadlineDoesNotAbandonNativeNextWork()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "detach");
        await host.StartAsync();
        var state = SdkLifetimeBase.States[key];
        var call = SdkEventProbe.Contexts[key].Events.WaterfallAsync<EquipmentSelectionApplying, int>(Event,
            async ct => { await state.ListByUserNumAsync(1, ct); return 10; }, default).AsTask();
        try
        {
            await state.Entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<RemoteTimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
            await state.Cancelled.Task.WaitAsync(Limit);
            Assert.Equal(0, state.Completed);
            state.Release.TrySetResult();
            await state.Finished.Task.WaitAsync(Limit);
            Exception failure = Assert.IsType<AggregateException>(await host.ShutdownAsync());
            while (failure.InnerException is { } inner) failure = inner;
            Assert.IsType<RemoteTimeoutException>(failure);
            Assert.Equal(PluginState.Failed, host.Instance("listener").State);
            Assert.DoesNotContain(host.Host.Instances, row => row.State is PluginState.CleanupIncomplete or PluginState.CleanupFailed);
            Assert.Single(host.Loggers.Entries, entry => entry.Contains("event-lifetime-deactivated", StringComparison.Ordinal));
        }
        finally { state.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => call.WaitAsync(Limit)); }
    }

    /// <summary>Canceling a Python publication await does not release its still-reachable native terminal.</summary>
    [Fact]
    public async Task CancelledSdkAwaitRetainsTerminalUntilActualPublicationCompletes()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Create(key, "publish");
        await host.StartAsync();
        var state = SdkLifetimeBase.States[key];
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var native = SdkEventProbe.Contexts[key].Events.Intercept<EquipmentSelectionApplying, int>(async (_, next, ct) =>
        {
            await state.ListByUserNumAsync(1, ct);
            var result = await next(ct);
            finished.TrySetResult(result);
            return result;
        });
        try
        {
            Assert.True(await host.DispatchLoginAsync("publish"));
            Assert.Single(host.Loggers.Entries, entry => entry.Contains("publication-wait-cancelled", StringComparison.Ordinal));
            Assert.False(finished.Task.IsCompleted);
            state.Release.TrySetResult();
            Assert.Equal(100, await finished.Task.WaitAsync(Limit));
            Assert.Single(host.Loggers.Entries, entry => entry.Contains("event-terminal-retained", StringComparison.Ordinal));
            Assert.Equal(PluginState.Active, host.Instance("listener").State);
        }
        finally { state.Release.TrySetResult(); }
    }

    /// <summary>All process SDKs receive native cancellation while their listener remains owned until it returns.</summary>
    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    [InlineData("go")]
    [InlineData("rust")]
    public async Task NativeCancellationReachesEachSdkListener(string language)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([EventBridgeTests.EventsEntry(language, false)], initialRows:
            [Row("policy", "test.sdk-event-policy"), Row("probe", "test.sdk-event-probe", ("key", key)),
                Row("listener", "conformance.events", ("behavior", "wait"), ("modes", new[] { "async" }))]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loggers.OnEntry = entry => { if (entry.Contains("event-call-started", StringComparison.Ordinal)) entered.TrySetResult(); };
        await host.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var dispatch = SdkEventProbe.Contexts[key].Events.SerialAsync(Event, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancellation.Cancel();
        await dispatch.WaitAsync(Limit);
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("event-call-cancelled", StringComparison.Ordinal));
        Assert.Equal(PluginState.Active, host.Instance("listener").State);
    }

    private static RemoteHarness Create(string key, string behavior) => new([new Dictionary<string, object?>
    {
        ["command"] = Python.Command, ["source_id"] = "event-lifetime",
        ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, "sdk/python/tests/event_lifecycle.py") },
    }], initialRows:
    [
        Row("base", "test.sdk-lifetime-base", ("key", key)), Row("probe", "test.sdk-event-probe", ("key", key)),
        Row("observer", "conformance.event-observer"), Row("listener", "conformance.event-lifetime", ("behavior", behavior)),
    ]);
}
