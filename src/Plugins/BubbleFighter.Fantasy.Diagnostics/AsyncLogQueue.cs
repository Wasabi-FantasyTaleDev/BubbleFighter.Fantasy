using System.Threading.Channels;

namespace BubbleFighter.Fantasy.Diagnostics;

// Network callbacks only enqueue; disk and console work belongs to the single consumer.
internal sealed class AsyncLogQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly Task _worker;
    private readonly TimeSpan _shutdownTimeout;
    private long _dropped;
    private int _closed;

    internal AsyncLogQueue(int capacity, TimeSpan shutdownTimeout, Func<T, ValueTask> write,
        Func<long, ValueTask> overflow, Func<ValueTask> flush, Action<Exception>? reportFailure = null,
        Action? close = null, Action<T>? discard = null)
    {
        _shutdownTimeout = shutdownTimeout;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(async () =>
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    var lost = Interlocked.Exchange(ref _dropped, 0);
                    if (lost != 0) await overflow(lost).ConfigureAwait(false);
                    for (var i = 0; i < 256 && _channel.Reader.TryRead(out var entry); i++)
                        await write(entry).ConfigureAwait(false);
                    await flush().ConfigureAwait(false);
                }
                var finalLost = Interlocked.Exchange(ref _dropped, 0);
                if (finalLost != 0) await overflow(finalLost).ConfigureAwait(false);
                await flush().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _channel.Writer.TryComplete(failure);
                reportFailure?.Invoke(failure);
                throw;
            }
            finally
            {
                // Failure stops admission first; reserved payload bytes for unwritten records must also be released.
                try
                {
                    while (_channel.Reader.TryRead(out var entry)) discard?.Invoke(entry);
                }
                finally { close?.Invoke(); }
            }
        });
    }

    internal bool Enqueue(T entry)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        // Never call WriteAsync here: waiting for capacity would couple logging to gameplay again.
        if (_channel.Writer.TryWrite(entry)) return true;
        RecordDrop();
        return false;
    }

    internal void RecordDrop() => Interlocked.Increment(ref _dropped);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _channel.Writer.TryComplete();
        await _worker.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
    }
}
