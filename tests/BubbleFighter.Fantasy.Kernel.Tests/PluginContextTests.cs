using BubbleFighter.Fantasy.Abstractions;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginContextTests
{
    private static PluginContext Create(ILogger? logger = null) => new(
        new PluginRow { Id = "test", Plugin = "test", Source = "test" },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), logger ?? NullLogger.Instance);

    private static int RetainedEffects(PluginContext context) => context.RetainedEffectCount;

    [Fact]
    public void RepeatedEarlyDisposalDoesNotRetainRegistrationHistory()
    {
        var context = Create();
        var calls = 0;
        for (var i = 0; i < 10000; i++)
        {
            var handle = context.Effect(() => calls++);
            handle.Dispose();
            handle.Dispose();
        }
        Assert.Equal(10000, calls);
        Assert.Equal(0, RetainedEffects(context));
        context.Unwind();
        Assert.Equal(10000, calls);
    }

    [Fact]
    public void RemovingMiddleEntriesPreservesReverseOrderOfSurvivors()
    {
        var context = Create();
        var calls = new List<int>();
        context.Effect(() => calls.Add(1));
        var middle = context.Effect(() => calls.Add(2));
        context.Effect(() => calls.Add(3));
        middle.Dispose();
        context.Effect(() => calls.Add(4));
        Assert.Equal(3, RetainedEffects(context));
        context.Unwind();
        Assert.Equal(new[] { 2, 4, 3, 1 }, calls);
        Assert.Equal(0, RetainedEffects(context));
    }

    [Fact]
    public void EarlyFailureIsRetainedAndReportedAtUnwindWithoutBeingRetried()
    {
        var context = Create();
        var failures = 0;
        var survivor = false;
        var handle = context.Effect(() =>
        {
            failures++;
            throw new InvalidOperationException("early failure");
        });
        context.Effect(() => survivor = true);
        Assert.Equal("early failure", Assert.Throws<InvalidOperationException>(handle.Dispose).Message);
        Assert.Equal(2, RetainedEffects(context));
        Assert.Throws<InvalidOperationException>(handle.Dispose);
        Assert.Throws<AggregateException>(context.Unwind);
        Assert.Equal(1, failures);
        Assert.True(survivor);
    }

    [Fact]
    public void UnwindAndRejectedRegistrationFailuresAreReportedWithoutSkippingSurvivors()
    {
        var logger = new RecordingLogger();
        var context = Create(logger);
        var survivor = false;
        context.Lifetime.Register(() => throw new InvalidOperationException("cancel"));
        context.Effect(() => survivor = true);
        context.Effect(() => throw new InvalidOperationException("unwind"));
        var error = Assert.Throws<AggregateException>(context.Unwind);
        var rejected = Assert.Throws<AggregateException>(() => context.Effect(() => throw new InvalidOperationException("rejected")));
        Assert.True(survivor);
        Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message == "unwind");
        Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message == "cancel");
        Assert.Contains(rejected.InnerExceptions, failure => failure.Message == "rejected");
        Assert.Contains(rejected.InnerExceptions, failure => failure is ObjectDisposedException);
        Assert.Equal(0, RetainedEffects(context));
    }

    [Fact]
    public void EarlyDisposerRejectsSelfWaitButCanRegisterOtherEffects()
    {
        var context = Create();
        var calls = new List<int>();
        context.Effect(() => calls.Add(1));
        IDisposable? handle = null;
        handle = context.Effect(() =>
        {
            calls.Add(2);
            Assert.Throws<InvalidOperationException>(handle!.Dispose);
            context.Effect(() => calls.Add(3));
            Assert.Throws<InvalidOperationException>(context.Unwind);
        });
        handle.Dispose();
        context.Unwind();
        Assert.Equal(new[] { 2, 3, 1 }, calls);
        Assert.Equal(0, RetainedEffects(context));
    }

    [Fact]
    public void UserCleanupRunsOutsideTheOwnerLock()
    {
        var context = Create();
        var calls = 0;
        var early = context.Effect(() =>
        {
            Task.Run(() => context.Effect(() => Interlocked.Increment(ref calls)).Dispose())
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });
        early.Dispose();
        Assert.Equal(1, calls);
        Assert.Equal(0, RetainedEffects(context));
        context.Unwind();
    }

    [Fact]
    public async Task ConcurrentEarlyDisposalRegistrationAndUnwindReleaseEveryEffectOnce()
    {
        var context = Create();
        var counts = new int[256];
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, counts.Length).Select(index => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                var handle = context.Effect(() => Interlocked.Increment(ref counts[index]));
                handle.Dispose();
                handle.Dispose();
            }
            catch (ObjectDisposedException) { }
        })).ToArray();
        start.Set();
        var unwind = Task.Run(context.Unwind);
        await Task.WhenAll(workers.Append(unwind)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(counts, count => Assert.Equal(1, count));
        Assert.Equal(0, RetainedEffects(context));
    }

    [Fact]
    public async Task ConcurrentCallsOnOneHandleCannotDoubleDisposeDuringUnwind()
    {
        var context = Create();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        var handle = context.Effect(() =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
        });
        var first = Task.Run(handle.Dispose);
        Task? second = null;
        Task? unwind = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, RetainedEffects(context));
            second = Task.Run(handle.Dispose);
            unwind = context.UnwindAsync().AsTask();
            Assert.False(unwind.IsCompleted);
            Assert.Equal(1, calls);
        }
        finally { finish.Set(); }
        await Task.WhenAll(first, second!, unwind!).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null) Errors.Enqueue(exception);
        }
    }

    [Fact]
    public void EarlyDisposalAndRepeatedUnwindReleaseEachEffectOnce()
    {
        var context = Create();
        var calls = new List<int>();
        var early = context.Effect(() => calls.Add(1));
        context.Effect(() => calls.Add(2));
        context.Effect(() => calls.Add(3));
        early.Dispose();
        early.Dispose();
        context.Unwind();
        context.Unwind();
        Assert.Equal(new[] { 1, 3, 2 }, calls);
    }

    [Fact]
    public void CancellationAndDisposerFailuresCannotAbandonRemainingEffects()
    {
        var context = Create();
        var disposed = false;
        context.Lifetime.Register(() => throw new InvalidOperationException("cancellation failure"));
        context.Effect(() => disposed = context.Lifetime.IsCancellationRequested);
        context.Effect(() => throw new InvalidOperationException("disposal failure"));
        Assert.Throws<AggregateException>(context.Unwind);
        Assert.True(disposed);
        Assert.Throws<AggregateException>(context.Unwind);
    }

    [Fact]
    public void EffectsAddedDuringOrAfterUnwindAreRejectedAndReleased()
    {
        var context = Create();
        var disposed = 0;
        context.Effect(() =>
        {
            Assert.Throws<InvalidOperationException>(context.Unwind);
            Assert.Throws<ObjectDisposedException>(() => context.Effect(() => disposed++));
        });
        context.Unwind();
        Assert.Throws<ObjectDisposedException>(() => context.Effect(() => disposed++));
        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task RegistrationRacingWithUnwindNeverLeaksAnEffect()
    {
        var context = Create();
        var disposed = 0;
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            try { context.Effect(() => Interlocked.Increment(ref disposed)); }
            catch (ObjectDisposedException) { }
        })).ToArray();
        start.Set();
        context.Unwind();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(32, disposed);
    }
}
