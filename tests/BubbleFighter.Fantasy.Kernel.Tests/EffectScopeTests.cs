using System.Collections.Concurrent;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class EffectScopeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ValueTask<IAsyncDisposable> Resource(Func<ValueTask> release) =>
        ValueTask.FromResult<IAsyncDisposable>(new AsyncResource(release));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapacityMustBePositive(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectScope(capacity));

    [Fact]
    public async Task EmptyClosureIsSharedAndRejectsNewFactories()
    {
        var scope = new EffectScope(1);
        var close = scope.DisposeAsync().AsTask();
        Assert.Same(close, scope.DisposeAsync().AsTask());
        await close.WaitAsync(Limit);
        Assert.True(scope.IsClosing);
        Assert.True(scope.Lifetime.IsCancellationRequested);
        var invoked = false;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.AcquireAsync(_ =>
        {
            invoked = true;
            return Resource(() => ValueTask.CompletedTask);
        }).AsTask());
        Assert.False(invoked);
    }

    [Fact]
    public async Task CapacityIncludesAcquiringAndReleasingResourcesAndCanBeReused()
    {
        var scope = new EffectScope(1);
        var acquired = Signal();
        var released = Signal();
        var releaseEntered = Signal();
        var acquire = scope.AcquireAsync(async _ =>
        {
            Assert.Equal(1, scope.RetainedCount);
            await acquired.Task;
            return new AsyncResource(async () =>
            {
                releaseEntered.SetResult();
                await released.Task;
            });
        }).AsTask();
        var rejectedFactories = 0;
        ValueTask<IAsyncDisposable> Rejected(CancellationToken _)
        {
            rejectedFactories++;
            return Resource(() => ValueTask.CompletedTask);
        }
        Task? release = null;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(Rejected).AsTask());
            acquired.SetResult();
            var handle = await acquire.WaitAsync(Limit);
            release = handle.DisposeAsync().AsTask();
            await releaseEntered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(Rejected).AsTask());
            Assert.Equal(0, rejectedFactories);
            Assert.Equal(1, scope.RetainedCount);
        }
        finally { acquired.TrySetResult(); released.TrySetResult(); }
        await release!.WaitAsync(Limit);
        Assert.Equal(0, scope.RetainedCount);
        var replacement = await scope.AcquireAsync(Rejected);
        Assert.Equal(1, rejectedFactories);
        await replacement.DisposeAsync();
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task CloseWaitsInReverseOrderAndExternalDisposalCannotOvertakeIt()
    {
        var scope = new EffectScope(3);
        var calls = new List<int>();
        var lastEntered = Signal();
        var finishLast = Signal();
        var first = await scope.AcquireAsync(_ => Resource(() =>
        {
            calls.Add(1);
            return ValueTask.CompletedTask;
        }));
        await scope.AcquireAsync(_ => Resource(() =>
        {
            calls.Add(2);
            return ValueTask.CompletedTask;
        }));
        await scope.AcquireAsync(_ => Resource(async () =>
        {
            Assert.True(scope.Lifetime.IsCancellationRequested);
            calls.Add(3);
            lastEntered.SetResult();
            await finishLast.Task;
        }));
        var close = scope.DisposeAsync().AsTask();
        Task firstRelease;
        try
        {
            await lastEntered.Task.WaitAsync(Limit);
            firstRelease = first.DisposeAsync().AsTask();
            Assert.False(firstRelease.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Equal(new[] { 3 }, calls);
        }
        finally { finishLast.TrySetResult(); }
        await Task.WhenAll(close, firstRelease).WaitAsync(Limit);
        Assert.Equal(new[] { 3, 2, 1 }, calls);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task LateResourcesReleaseInReservationOrderBeforeAcquisitionFails()
    {
        var scope = new EffectScope(2);
        var firstReady = Signal();
        var secondReady = Signal();
        var firstReturned = Signal();
        var calls = new List<int>();
        var first = scope.AcquireAsync(async _ =>
        {
            await firstReady.Task;
            firstReturned.SetResult();
            return new AsyncResource(() => { calls.Add(1); return ValueTask.CompletedTask; });
        }).AsTask();
        var second = scope.AcquireAsync(async _ =>
        {
            await secondReady.Task;
            return new AsyncResource(() => { calls.Add(2); return ValueTask.CompletedTask; });
        }).AsTask();
        var close = scope.DisposeAsync().AsTask();
        try
        {
            firstReady.SetResult();
            await firstReturned.Task.WaitAsync(Limit);
            Assert.True(scope.Lifetime.IsCancellationRequested);
            Assert.False(close.IsCompleted);
            Assert.False(first.IsCompleted);
            Assert.Empty(calls);
        }
        finally { firstReady.TrySetResult(); secondReady.TrySetResult(); }
        await close.WaitAsync(Limit);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(Limit));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.WaitAsync(Limit));
        Assert.Equal(new[] { 2, 1 }, calls);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task LateCleanupFailureIsReportedToBothCloserAndAcquirer()
    {
        var scope = new EffectScope(1);
        var ready = Signal();
        var failure = new InvalidOperationException("late cleanup");
        var acquire = scope.AcquireAsync(async _ =>
        {
            await ready.Task;
            return new AsyncResource(() => ValueTask.FromException(failure));
        }).AsTask();
        var close = scope.DisposeAsync().AsTask();
        ready.SetResult();
        var closeError = await Assert.ThrowsAsync<AggregateException>(() => close.WaitAsync(Limit));
        var acquireError = await Assert.ThrowsAsync<AggregateException>(() => acquire.WaitAsync(Limit));
        Assert.Contains(failure, closeError.Flatten().InnerExceptions);
        Assert.Contains(failure, acquireError.Flatten().InnerExceptions);
        Assert.Contains(acquireError.InnerExceptions, error => error is ObjectDisposedException);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task ConcurrentDisposalsAndClosureJoinTheSameInProgressRelease()
    {
        var scope = new EffectScope(1);
        var entered = Signal();
        var finish = Signal();
        var count = 0;
        var handle = await scope.AcquireAsync(_ => Resource(async () =>
        {
            Interlocked.Increment(ref count);
            entered.SetResult();
            await finish.Task;
        }));
        var first = handle.DisposeAsync().AsTask();
        Task second;
        Task close;
        try
        {
            await entered.Task.WaitAsync(Limit);
            second = handle.DisposeAsync().AsTask();
            close = scope.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.Same(close, scope.DisposeAsync().AsTask());
            Assert.False(second.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Equal(1, scope.RetainedCount);
        }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(first, second, close).WaitAsync(Limit);
        await handle.DisposeAsync();
        Assert.Equal(1, count);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task AcquisitionFailureAndNullReturnReleaseTheirCapacity()
    {
        var scope = new EffectScope(1);
        var failure = new InvalidOperationException("factory");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(
            _ => ValueTask.FromException<IAsyncDisposable>(failure)).AsTask()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(
            _ => ValueTask.FromResult<IAsyncDisposable>(null!)).AsTask());
        Assert.Equal(0, scope.RetainedCount);
        await scope.AcquireAsync(_ => Resource(() => ValueTask.CompletedTask));
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task LifetimeCancellationUnblocksAcquisitionAndCloseJoinsIt()
    {
        var scope = new EffectScope(1);
        var started = Signal();
        var acquire = scope.AcquireAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new AsyncResource(() => ValueTask.CompletedTask);
        }).AsTask();
        await started.Task.WaitAsync(Limit);
        await scope.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire.WaitAsync(Limit));
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task CancellationAndMultipleCleanupFailuresDoNotSkipSurvivors()
    {
        var scope = new EffectScope(3);
        var calls = new List<int>();
        using var cancellation = scope.Lifetime.Register(() => throw new InvalidOperationException("cancel"));
        await scope.AcquireAsync(_ => Resource(() => { calls.Add(1); return ValueTask.CompletedTask; }));
        await scope.AcquireAsync(_ => Resource(() => { calls.Add(2); throw new InvalidOperationException("release"); }));
        await scope.AcquireAsync(_ => Resource(() => { calls.Add(3); return ValueTask.FromCanceled(new CancellationToken(true)); }));
        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(3, error.Flatten().InnerExceptions.Count);
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cancel");
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "release");
        Assert.Contains(error.Flatten().InnerExceptions, e => e is OperationCanceledException);
        Assert.Equal(new[] { 3, 2, 1 }, calls);
        Assert.Equal(0, scope.RetainedCount);
        Assert.Same(error, await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task EarlyReleaseFailureOccupiesCapacityAndCannotDisappearFromClosure()
    {
        var scope = new EffectScope(1);
        var attempts = 0;
        var failure = new InvalidOperationException("early");
        var handle = await scope.AcquireAsync(_ => Resource(() => { attempts++; throw failure; }));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => handle.DisposeAsync().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => handle.DisposeAsync().AsTask()));
        Assert.Equal(1, attempts);
        Assert.Equal(1, scope.RetainedCount);
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(_ =>
        {
            invoked = true;
            return Resource(() => ValueTask.CompletedTask);
        }).AsTask());
        Assert.False(invoked);
        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.Same(failure, Assert.Single(error.InnerExceptions));
        Assert.Equal(0, scope.RetainedCount);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RepeatedEarlyReleasesDoNotConsumeLifetimeCapacity()
    {
        var scope = new EffectScope(1);
        var count = 0;
        for (var i = 0; i < 10000; i++)
        {
            var handle = await scope.AcquireAsync(_ => Resource(() => { count++; return ValueTask.CompletedTask; }));
            await handle.DisposeAsync();
            await handle.DisposeAsync();
        }
        Assert.Equal(10000, count);
        Assert.Equal(0, scope.RetainedCount);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task AcquisitionCannotWaitForItsOwnScopeClosure()
    {
        var scope = new EffectScope(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.AcquireAsync(async _ =>
        {
            await scope.DisposeAsync();
            return new AsyncResource(() => ValueTask.CompletedTask);
        }).AsTask().WaitAsync(Limit));
        Assert.False(scope.IsClosing);
        Assert.Equal(0, scope.RetainedCount);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task CleanupCannotWaitForItsOwnHandleOrScope()
    {
        var scope = new EffectScope(1);
        IAsyncDisposable? handle = null;
        handle = await scope.AcquireAsync(_ => Resource(async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handle!.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.DisposeAsync().AsTask());
        }));
        await scope.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task ClosingCleanupCannotAwaitAnEarlierPeerButThatPeerStillReleases()
    {
        var scope = new EffectScope(2);
        var peerReleased = false;
        var peer = await scope.AcquireAsync(_ => Resource(() => { peerReleased = true; return ValueTask.CompletedTask; }));
        await scope.AcquireAsync(_ => Resource(() => peer.DisposeAsync()));
        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
        Assert.True(peerReleased);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task UserCallbacksCanAccessScopeFromAnotherThread()
    {
        var scope = new EffectScope(3);
        var releases = 0;
        var outer = await scope.AcquireAsync(async _ =>
        {
            await Task.Run(async () =>
            {
                var child = await scope.AcquireAsync(_ => Resource(() => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; }));
                await child.DisposeAsync();
            }).WaitAsync(Limit);
            return new AsyncResource(async () =>
            {
                await Task.Run(async () =>
                {
                    var child = await scope.AcquireAsync(_ => Resource(() => { Interlocked.Increment(ref releases); return ValueTask.CompletedTask; }));
                    await child.DisposeAsync();
                }).WaitAsync(Limit);
            });
        });
        await outer.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(2, releases);
        Assert.Equal(0, scope.RetainedCount);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task CallbackCannotAdmitNewResourcesDuringClosure()
    {
        var scope = new EffectScope(2);
        var factoryCalled = false;
        await scope.AcquireAsync(_ => Resource(async () =>
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.AcquireAsync(_ =>
            {
                factoryCalled = true;
                return Resource(() => ValueTask.CompletedTask);
            }).AsTask());
        }));
        await scope.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task CompletedCallbackContextDoesNotPreventLaterClosureFromItsChildTask()
    {
        var scope = new EffectScope(1);
        var finish = Signal();
        Task? child = null;
        await scope.AcquireAsync(_ =>
        {
            child = Task.Run(async () => { await finish.Task; await scope.DisposeAsync(); });
            return Resource(() => ValueTask.CompletedTask);
        });
        finish.SetResult();
        await child!.WaitAsync(Limit);
        Assert.True(scope.IsClosing);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task NestedCleanupCycleFailsInsteadOfWaitingForItsAncestor()
    {
        var scope = new EffectScope(2);
        IAsyncDisposable? first = null;
        var second = await scope.AcquireAsync(_ => Resource(() => first!.DisposeAsync()));
        first = await scope.AcquireAsync(_ => Resource(() => second.DisposeAsync()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(2, scope.RetainedCount);
        var error = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(0, scope.RetainedCount);
    }

    [Fact]
    public async Task AcquisitionAndEarlyReleaseRacingClosureNeverLeakOrDoubleRelease()
    {
        var scope = new EffectScope(128);
        var start = Signal();
        var admitted = Signal();
        var acquired = new ConcurrentDictionary<int, int>();
        var released = new ConcurrentDictionary<int, int>();
        var workers = Enumerable.Range(0, 128).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                var handle = await scope.AcquireAsync(async _ =>
                {
                    acquired.AddOrUpdate(index, 1, (_, count) => count + 1);
                    if (index == 0) admitted.SetResult();
                    await Task.Yield();
                    return new AsyncResource(async () =>
                    {
                        await Task.Yield();
                        released.AddOrUpdate(index, 1, (_, count) => count + 1);
                    });
                });
                await handle.DisposeAsync();
                await handle.DisposeAsync();
            }
            catch (ObjectDisposedException) { }
        })).ToArray();
        var close = Task.Run(async () => { await admitted.Task; await scope.DisposeAsync(); });
        start.SetResult();
        await Task.WhenAll(workers.Append(close)).WaitAsync(Limit);
        Assert.NotEmpty(acquired);
        Assert.Equal(acquired.Keys.Order(), released.Keys.Order());
        Assert.All(acquired.Values, count => Assert.Equal(1, count));
        Assert.All(released.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, scope.RetainedCount);
    }

    private sealed class AsyncResource(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}
