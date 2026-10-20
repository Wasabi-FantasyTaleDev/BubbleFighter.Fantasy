using System.Diagnostics;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BubbleFighter.Fantasy.Loader.Remote.Tests.ServiceProviderTests;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Checks that host-to-plugin callbacks that never answer are cancelled, bounded and fatal to their row.</summary>
[Collection("SDK compilation")]
public sealed class CallbackDeadlineTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>An unanswered callback is sent its cancel notification, then times out after shutdown_timeout_ms.</summary>
    [Fact]
    public async Task UnansweredCallbackIsCancelledThenTimesOut()
    {
        var channel = new SilentChannel();
        var options = new TransportOptions(MaxLineBytes: 4096, MaxInFlight: 8, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        await using var process = RemoteProcess.Start(channel, options, NullLogger.Instance, new SilentSink(), "test");

        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RemoteTimeoutException>(() => process.RequestUntilResponseAsync(
            "messages.invoke", new { }, new { activation = "a", invocation = "i" }, CancellationToken.None, "messages.cancel"));
        watch.Stop();

        Assert.Contains("messages.cancel", channel.Written);
        Assert.Contains("(request_timeout_ms)", error.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
    }

    /// <summary>A task callback has no deadline while it runs, and cancellation then bounds the remaining wait.</summary>
    [Fact]
    public async Task TaskCallbackIsBoundedOnlyAfterCancellationIsRequested()
    {
        var options = new TransportOptions(MaxLineBytes: 4096, MaxInFlight: 8, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        await using var process = RemoteProcess.Start(new SilentChannel(), options, NullLogger.Instance, new SilentSink(), "test");
        using var cancellation = new CancellationTokenSource();

        var pending = process.RequestUntilResponseAsync("tasks.invoke", new { }, new { }, cancellation.Token, "tasks.cancel",
            responseDeadline: false);
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);

        await cancellation.CancelAsync();
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RemoteTimeoutException>(() => pending);
        watch.Stop();

        Assert.Contains("(shutdown_timeout_ms)", error.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(3));
    }

    /// <summary>An SDK middleware callback that never answers faults its row within the configured bounds.</summary>
    [PythonFact]
    public async Task UnansweredMiddlewareCallbackFaultsTheRowWithinTheDeadline()
    {
        // The deadlines also bound loader startup, so leave the interpreter enough time to answer initialize.
        await using var host = new RemoteHarness([MiddlewareEntry()],
            new Dictionary<string, object?> { ["request_timeout_ms"] = 1000, ["shutdown_timeout_ms"] = 1000 },
            initialRows: [Row("target", "test.middleware-target")]);
        await host.StartAsync();
        await host.AddRowAsync("middleware", "conformance.middleware",
            new() { ["protocol"] = TestDialect.Protocol, ["behavior"] = "hang" });

        var watch = Stopwatch.StartNew();
        var dispatch = host.DispatchLoginAsync("alice");
        await Assert.ThrowsAsync<RemoteTimeoutException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(10)));
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(8));
        // The row is faulted (owned work failed) instead of waiting for a callback that will never answer.
        await WaitForStateAsync(host, PluginState.WorkFailed);
        _ = await host.ShutdownAsync();
    }

    private static async Task WaitForStateAsync(RemoteHarness host, PluginState state)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (host.Instance("middleware").State != state)
            await Task.Delay(20, timeout.Token);
    }

    private static Dictionary<string, object?> MiddlewareEntry() => new()
    {
        ["command"] = Python.Command,
        ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, "sdk/python/tests/conformance_plugins.py") },
        ["source_id"] = "deadline",
        ["max_definitions"] = 4,
    };
}
