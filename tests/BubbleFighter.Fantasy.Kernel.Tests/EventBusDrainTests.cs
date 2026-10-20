using System.Collections.Concurrent;
using System.Diagnostics;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class EventBusDrainTests
{
    private sealed record Ping : IEvent;

    private sealed class ErrorLog : ILogger
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Enqueue(formatter(state, exception));
        }
    }

    private static EventBus Bus(int drainMs, ILogger? logger = null) =>
        new(logger ?? NullLogger.Instance, new EventBusOptions { DrainTimeoutMs = drainMs });

    [Fact]
    public async Task RemovalCancelsAndWaitsForTheRunningCall()
    {
        var bus = Bus(10_000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var subscription = bus.OnAsync<Ping>(async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            await Task.Delay(100, CancellationToken.None);
            finished = true;
        });
        var call = bus.SerialAsync(new Ping(), CancellationToken.None).AsTask();
        await entered.Task;

        await Task.Run(subscription.Dispose);

        Assert.True(finished);
        await call;
    }

    [Fact]
    public async Task HandlerIgnoringCancellationIsReportedAfterTheDrainLimit()
    {
        var log = new ErrorLog();
        var bus = Bus(200, log);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = bus.OnAsync<Ping>(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        var call = bus.ParallelAsync(new Ping(), CancellationToken.None).AsTask();
        await entered.Task;

        var watch = Stopwatch.StartNew();
        await Task.Run(subscription.Dispose);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
        Assert.Single(log.Errors);

        release.SetResult();
        await call;
    }

    [Fact]
    public async Task HandlerRemovingItsOwnSubscriptionDoesNotWaitForItself()
    {
        var bus = Bus(30_000);
        IDisposable? subscription = null;
        subscription = bus.OnAsync<Ping>(async (_, _) =>
        {
            await Task.Yield();
            subscription!.Dispose();
        });
        var watch = Stopwatch.StartNew();
        await bus.SerialAsync(new Ping(), CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RemovedSubscriptionReceivesNoNewDispatchesEvenFromAnEarlierSnapshot()
    {
        var bus = Bus(10_000);
        var calls = 0;
        IDisposable? second = null;
        bus.OnAsync<Ping>((_, _) => { second!.Dispose(); return ValueTask.CompletedTask; }, priority: 10);
        second = bus.OnAsync<Ping>((_, _) => { calls++; return ValueTask.CompletedTask; });

        await bus.SerialAsync(new Ping(), CancellationToken.None);
        await bus.SerialAsync(new Ping(), CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RemovedMiddlewareIsSkippedAndSyncHandlersStopAfterRemoval()
    {
        var bus = Bus(10_000);
        var syncCalls = 0;
        var sync = bus.On<Ping>(_ => syncCalls++);
        var middleware = bus.Intercept<Ping, int>(async (_, next, ct) => await next(ct) + 1);
        Assert.Equal(2, await bus.WaterfallAsync(new Ping(), _ => ValueTask.FromResult(1), CancellationToken.None));
        bus.Emit(new Ping());

        middleware.Dispose();
        sync.Dispose();

        Assert.Equal(1, await bus.WaterfallAsync(new Ping(), _ => ValueTask.FromResult(1), CancellationToken.None));
        bus.Emit(new Ping());
        Assert.Equal(1, syncCalls);
    }

    [Fact]
    public async Task ConcurrentRemovalAndDispatchNeverCallAfterRemovalReturns()
    {
        var bus = Bus(10_000);
        for (var round = 0; round < 200; round++)
        {
            var removed = 0;
            var late = 0;
            var subscription = bus.OnAsync<Ping>(async (_, ct) =>
            {
                if (Volatile.Read(ref removed) == 1)
                    Interlocked.Increment(ref late);
                await Task.Yield();
            });
            var dispatches = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(async () => await bus.ParallelAsync(new Ping(), CancellationToken.None)))
                .ToArray();
            await Task.Run(() =>
            {
                subscription.Dispose();
                Volatile.Write(ref removed, 1);
            });
            await Task.WhenAll(dispatches);
            Assert.Equal(0, late);
        }
    }

    [Fact]
    public void DrainLimitIsValidated()
    {
        Assert.ThrowsAny<System.ComponentModel.DataAnnotations.ValidationException>(() => Bus(0));
    }
}
