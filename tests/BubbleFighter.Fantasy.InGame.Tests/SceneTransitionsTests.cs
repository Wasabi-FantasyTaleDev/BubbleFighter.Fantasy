using BubbleFighter.Fantasy.InGame.Handlers;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Overlapping party moves serialize while unrelated players keep entering scenes.</summary>
public sealed class SceneTransitionsTests
{
    /// <summary>A follower's own request waits for its group, without blocking an unrelated connection.</summary>
    [Fact]
    public async Task OverlappingMovesReserveParticipantsInOneOrder()
    {
        var moves = new SceneTransitions(4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = moves.RunAsync([2, 1], async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var called = false;
            var next = moves.RunAsync([1, 2], _ => { called = true; return ValueTask.CompletedTask; }, default).AsTask();
            await moves.RunAsync([3], _ => ValueTask.CompletedTask, default);
            Assert.False(called);
            Assert.False(next.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(called);
        }
        finally { release.TrySetResult(); await moves.StopAsync(); }
    }

    /// <summary>A cancelled partial reservation frees its first participant and does not consume capacity forever.</summary>
    [Fact]
    public async Task CancellationAndCapacityAreBounded()
    {
        var moves = new SceneTransitions(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = moves.RunAsync([2], async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); }, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var queued = moves.RunAsync([1, 2], _ => throw new InvalidOperationException("Cancelled callback ran."), cancellation.Token).AsTask();
            await Assert.ThrowsAsync<InvalidOperationException>(() => moves.RunAsync([3], _ => ValueTask.CompletedTask, default).AsTask());
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await moves.RunAsync([1], _ => ValueTask.CompletedTask, default);
        }
        finally { release.TrySetResult(); await first; await moves.StopAsync(); }
    }

    /// <summary>Unload cancels waiters, waits for active work to leave, and rejects new requests.</summary>
    [Fact]
    public async Task StopOwnsEveryAcceptedOperation()
    {
        var moves = new SceneTransitions(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = moves.RunAsync([1], async _ => { entered.SetResult(); await release.Task; }, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = moves.RunAsync([1], _ => throw new InvalidOperationException("Stopped callback ran."), default).AsTask();
        var stopping = moves.StopAsync();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => moves.RunAsync([2], _ => ValueTask.CompletedTask, default).AsTask());
        }
        finally { release.SetResult(); await first; await stopping; }
    }
}
