using BubbleFighter.Fantasy.Diagnostics;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class AsyncLogQueueTests
{
    /// <summary>Failure while flushing releases entries admitted after the completed write without writing them.</summary>
    [Fact]
    public async Task FlushFailureDiscardsEntriesAdmittedDuringFlush()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<int>();
        var discarded = new List<int>();
        var original = new IOException("flush failed");
        var closed = false;
        var queue = new AsyncLogQueue<int>(4, TimeSpan.FromSeconds(5), value =>
            { written.Add(value); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask,
            async () => { entered.SetResult(); await release.Task; throw original; },
            close: () => closed = true, discard: discarded.Add);
        try
        {
            Assert.True(queue.Enqueue(0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.Enqueue(1));
            Assert.True(queue.Enqueue(2));
            release.SetResult();
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(async () => await queue.DisposeAsync()));
            Assert.Equal(new[] { 0 }, written);
            Assert.Equal(new[] { 1, 2 }, discarded);
            Assert.True(closed);
            Assert.False(queue.Enqueue(3));
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>A failed writer releases queued payload credits exactly once and continues to report the original failure.</summary>
    [Fact]
    public async Task WriterFailureDiscardsUnwrittenEntriesAndClosesAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new List<int>();
        var written = new List<int>();
        var pending = 0;
        var closed = false;
        var original = new IOException("disk failed");
        var queue = new AsyncLogQueue<int>(4, TimeSpan.FromSeconds(5), async value =>
        {
            try
            {
                written.Add(value);
                entered.SetResult();
                await release.Task;
                throw original;
            }
            finally { Interlocked.Decrement(ref pending); }
        }, _ => ValueTask.CompletedTask, () => ValueTask.CompletedTask,
            close: () => closed = true,
            discard: value => { discarded.Add(value); Interlocked.Decrement(ref pending); });
        try
        {
            Interlocked.Increment(ref pending);
            Assert.True(queue.Enqueue(0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 1; i <= 4; i++)
            {
                Interlocked.Increment(ref pending);
                Assert.True(queue.Enqueue(i));
            }
            release.SetResult();
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(async () => await queue.DisposeAsync()));
            Assert.Equal(new[] { 0 }, written);
            Assert.Equal(new[] { 1, 2, 3, 4 }, discarded);
            Assert.Equal(0, pending);
            Assert.True(closed);
            Assert.False(queue.Enqueue(5));
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>Failure in the overflow notice also releases all entries without invoking the data writer.</summary>
    [Fact]
    public async Task OverflowNoticeFailureReleasesQueuedEntriesBeforeAnyWrite()
    {
        var overflowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new List<int>();
        var writes = 0;
        var queue = new AsyncLogQueue<int>(4, TimeSpan.FromSeconds(5), _ =>
            { writes++; return ValueTask.CompletedTask; }, async _ =>
            { overflowEntered.SetResult(); await release.Task; throw new IOException("overflow notice failed"); },
            () => ValueTask.CompletedTask, discard: discarded.Add);
        try
        {
            queue.RecordDrop();
            Assert.True(queue.Enqueue(1));
            await overflowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.Enqueue(2));
            release.SetResult();
            await Assert.ThrowsAsync<IOException>(async () => await queue.DisposeAsync());
            Assert.Equal(0, writes);
            Assert.Equal(new[] { 1, 2 }, discarded);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedDiskDoesNotBlockProducersAndOverflowIsCounted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<int>();
        long lost = 0;
        var queue = new AsyncLogQueue<int>(4, TimeSpan.FromSeconds(5), async value =>
        {
            if (value == 0) { entered.SetResult(); await resume.Task; }
            written.Add(value);
        }, count => { lost += count; return ValueTask.CompletedTask; }, () => ValueTask.CompletedTask);
        queue.Enqueue(0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // All producers complete while the consumer is deliberately held inside disk work.
        await Task.Run(() => { for (var i = 1; i <= 10000; i++) queue.Enqueue(i); })
            .WaitAsync(TimeSpan.FromSeconds(5));
        resume.SetResult();
        await queue.DisposeAsync();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, written);
        Assert.Equal(9996, lost);
    }

    [Fact]
    public async Task ShutdownDrainsAcceptedRecordsInOrderAndFlushes()
    {
        var lines = new List<int>();
        var flushes = 0;
        var closed = false;
        var queue = new AsyncLogQueue<int>(10000, TimeSpan.FromSeconds(5), value =>
        { lines.Add(value); return ValueTask.CompletedTask; }, _ => throw new InvalidOperationException("Unexpected overflow."),
            () => { flushes++; return ValueTask.CompletedTask; }, close: () => closed = true);
        for (var i = 0; i < 10000; i++) queue.Enqueue(i);
        await queue.DisposeAsync();
        Assert.Equal(Enumerable.Range(0, 10000), lines);
        Assert.True(flushes > 0);
        Assert.True(closed);
    }

    [Fact]
    public async Task WriterFailureIsReportedAndSurfacedAtShutdown()
    {
        Exception? reported = null;
        var queue = new AsyncLogQueue<int>(4, TimeSpan.FromSeconds(5), _ => throw new IOException("disk failed"),
            _ => ValueTask.CompletedTask, () => ValueTask.CompletedTask, failure => reported = failure);
        queue.Enqueue(1);
        var failure = await Assert.ThrowsAsync<IOException>(async () => await queue.DisposeAsync());
        Assert.Same(failure, reported);
    }
}
