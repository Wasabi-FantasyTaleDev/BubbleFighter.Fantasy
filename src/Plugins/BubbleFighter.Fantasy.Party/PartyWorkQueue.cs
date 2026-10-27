using System.Threading.Channels;

namespace BubbleFighter.Fantasy.Party;

/// <summary>Serializes party mutations with their notices; close events wake a sweep without allocating queued work.</summary>
internal sealed class PartyWorkQueue(int capacity) : IDisposable
{
    private readonly Channel<Work> _requests = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.DropWrite,
    });
    private int _stopped;

    /// <summary>
    /// Queues one serialized mutation. A full or stopped queue is refused instead of blocking the
    /// caller; the caller's token is checked at admission and again before the work is accepted, so a
    /// disconnecting requester does not run its mutation. The returned task completes when the queued
    /// operation (and its peer notices) finish.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the queue is full or stopped.</exception>
    internal ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> operation, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
        var work = new Work(operation, caller);
        if (!_requests.Writer.TryWrite(work))
            throw new InvalidOperationException("The party request queue is full or stopped.");
        Wake();
        return new ValueTask(work.Completion.Task);
    }

    // The tracked connection remains the source of truth; repeated signals can be coalesced safely.
    internal void Wake() => _wake.Writer.TryWrite(0);

    /// <summary>
    /// The queue's single reader loop: wait for a wake signal, run the sweep, then drain every queued
    /// operation, sweeping again after each. Once accepted, an operation runs with the loop's lifetime
    /// token — peer notices finish even when the requester's token cancels mid-flight — and its
    /// failure or cancellation is reported to the queued task, not to the loop. When the lifetime ends,
    /// the loop stops itself and cancels every still-queued work with a cancelled token.
    /// </summary>
    internal async ValueTask RunAsync(Func<CancellationToken, ValueTask> sweep, CancellationToken lifetime)
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(lifetime).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                await sweep(lifetime).ConfigureAwait(false);
                while (_requests.Reader.TryRead(out var work))
                {
                    try
                    {
                        lifetime.ThrowIfCancellationRequested();
                        work.Caller.ThrowIfCancellationRequested();
                        // Once accepted, peer notices must finish even if the requester disconnects.
                        await work.Operation(lifetime).ConfigureAwait(false);
                        work.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException ex) when (lifetime.IsCancellationRequested || work.Caller.IsCancellationRequested)
                    {
                        work.Completion.TrySetCanceled(ex.CancellationToken);
                    }
                    catch (Exception ex) { work.Completion.TrySetException(ex); }
                    await sweep(lifetime).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Dispose();
            while (_requests.Reader.TryRead(out var work))
                work.Completion.TrySetCanceled(new CancellationToken(canceled: true));
        }
    }

    /// <summary>Stops new admissions and completes both channels; the reader loop drains what it can and cancels the rest. Idempotent.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _requests.Writer.TryComplete();
        _wake.Writer.TryComplete();
    }

    private sealed record Work(Func<CancellationToken, ValueTask> Operation, CancellationToken Caller)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
