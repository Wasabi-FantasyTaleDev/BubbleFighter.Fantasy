using BubbleFighter.Fantasy.Host;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises signal-handler isolation and actual cancellation callback ownership in the shared host helper source.</summary>
public sealed class OwnedShutdownSignalTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>A callback failure belongs to asynchronous completion and never escapes the operating-system request path.</summary>
    [Fact]
    public async Task ThrowingCallbackIsObservedThroughSharedDisposalResult()
    {
        var signal = new OwnedShutdownSignal();
        var entered = Signal();
        var callbacks = 0;
        using var registration = signal.Token.Register(() =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            throw new InvalidOperationException("shutdown callback failed");
        });
        try
        {
            Assert.Null(Record.Exception(signal.RequestCancellation));
            await entered.Task.WaitAsync(Limit);
            Assert.Null(Record.Exception(signal.RequestCancellation));
            var first = signal.DisposeAsync().AsTask();
            var repeated = signal.DisposeAsync().AsTask();
            Assert.Same(first, repeated);
            var failure = await Record.ExceptionAsync(() => first.WaitAsync(Limit));
            Assert.NotNull(failure);
            Assert.Contains("shutdown callback failed", failure.ToString());
            var repeatedFailure = await Record.ExceptionAsync(() => repeated.WaitAsync(Limit));
            Assert.Same(failure, repeatedFailure);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            Assert.Null(Record.Exception(signal.RequestCancellation));
        }
        finally
        {
            var cleanup = await Record.ExceptionAsync(() => signal.DisposeAsync().AsTask().WaitAsync(Limit));
            Assert.IsNotType<TimeoutException>(cleanup);
        }
    }

    /// <summary>Concurrent and repeated requests dispatch each registered callback only once.</summary>
    [Fact]
    public async Task ConcurrentRequestsShareOneCancellationDispatch()
    {
        var signal = new OwnedShutdownSignal();
        var start = Signal();
        var callbacks = 0;
        using var registration = signal.Token.Register(() => Interlocked.Increment(ref callbacks));
        Task[] requests = [];
        try
        {
            requests = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                signal.RequestCancellation();
                signal.RequestCancellation();
            })).ToArray();
            start.TrySetResult();
            await Task.WhenAll(requests).WaitAsync(Limit);
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            Assert.True(signal.Token.IsCancellationRequested);
            Assert.Null(Record.Exception(signal.RequestCancellation));
        }
        finally
        {
            start.TrySetResult();
            await Task.WhenAll(requests).WaitAsync(Limit);
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    /// <summary>Disposal remains incomplete while an actual callback is blocked and later requests cannot detach that ownership.</summary>
    [Fact]
    public async Task DisposalWaitsForBlockedCallbackBeforeClosingSource()
    {
        var signal = new OwnedShutdownSignal();
        var entered = Signal();
        var release = Signal();
        var completed = 0;
        using var registration = signal.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref completed);
        });
        try
        {
            signal.RequestCancellation();
            await entered.Task.WaitAsync(Limit);
            var cleanup = signal.DisposeAsync().AsTask();
            var repeated = signal.DisposeAsync().AsTask();
            Assert.Same(cleanup, repeated);
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref completed));
            Assert.Null(Record.Exception(signal.RequestCancellation));
            Assert.False(cleanup.IsCompleted);
            release.TrySetResult();
            await cleanup.WaitAsync(Limit);
            Assert.Equal(1, Volatile.Read(ref completed));
        }
        finally
        {
            release.TrySetResult();
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    /// <summary>A signal arriving after owner disposal is a safe no-op and the captured token remains readable.</summary>
    [Fact]
    public async Task RequestsAfterDisposalAreSafeNoOps()
    {
        var signal = new OwnedShutdownSignal();
        var token = signal.Token;
        var callbacks = 0;
        using var registration = token.Register(() => Interlocked.Increment(ref callbacks));
        try
        {
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
            var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(signal.RequestCancellation)).ToArray();
            await Task.WhenAll(requests).WaitAsync(Limit);
            Assert.Equal(token, signal.Token);
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(0, Volatile.Read(ref callbacks));
        }
        finally { await signal.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    /// <summary>Closing an idle owner does not manufacture cancellation or invoke plugin callbacks.</summary>
    [Fact]
    public async Task IdleDisposalDoesNotDispatchCancellationCallbacks()
    {
        var signal = new OwnedShutdownSignal();
        using var registration = signal.Token.Register(() => throw new InvalidOperationException("idle callback must not run"));
        try
        {
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.False(signal.Token.IsCancellationRequested);
        }
        finally { await signal.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    /// <summary>A cancellation callback can repeat the signal without recursive dispatch or deadlocking its owner.</summary>
    [Fact]
    public async Task CallbackCanRepeatSignalWithoutReenteringDispatch()
    {
        var signal = new OwnedShutdownSignal();
        var entered = Signal();
        var callbacks = 0;
        using var registration = signal.Token.Register(() =>
        {
            Interlocked.Increment(ref callbacks);
            signal.RequestCancellation();
            entered.TrySetResult();
        });
        try
        {
            signal.RequestCancellation();
            await entered.Task.WaitAsync(Limit);
            await signal.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, Volatile.Read(ref callbacks));
        }
        finally { await signal.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
