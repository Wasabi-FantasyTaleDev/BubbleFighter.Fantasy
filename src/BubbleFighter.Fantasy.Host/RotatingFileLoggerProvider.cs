using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

internal sealed class RotatingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly Channel<Entry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _drainLock = new();
    private Task? _drain;
    private TaskCompletionSource _recovery = CompletedSignal();
    private long _queuedBytes;
    private long _dropped;
    private int _closed;
    private int _broken;

    internal RotatingFileLoggerProvider(DurableLogOptions options, Func<CancellationToken, ValueTask>? beforeWrite = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        options.Validate();
        Options = options;
        retryDelay ??= Task.Delay;
        // Open synchronously before starting the worker so an unusable destination is a startup error.
        var sink = new RollingSink(options);
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (beforeWrite is not null) await beforeWrite(_shutdown.Token).ConfigureAwait(false);
                        await WriteDropsAsync(sink).ConfigureAwait(false);
                        if (entry.Bytes is null) continue;
                        await sink.WriteAsync(entry.Bytes, _shutdown.Token).ConfigureAwait(false);
                        if (entry.Level >= LogLevel.Warning) await sink.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception failure) when (IsIoFailure(failure))
                    {
                        if (entry.Bytes is not null) Interlocked.Increment(ref _dropped);
                        BreakSink(sink, failure);
                    }
                    finally
                    {
                        if (entry.Bytes is not null) Interlocked.Add(ref _queuedBytes, -entry.Bytes.Length);
                    }
                    if (Volatile.Read(ref _broken) != 0)
                        await RecoverAsync(sink, retryDelay).ConfigureAwait(false);
                }
                while (true)
                {
                    try
                    {
                        await WriteDropsAsync(sink).ConfigureAwait(false);
                        await sink.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception failure) when (IsIoFailure(failure))
                    {
                        BreakSink(sink, failure);
                        await RecoverAsync(sink, retryDelay).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception failure)
            {
                Interlocked.Exchange(ref _closed, 1);
                _channel.Writer.TryComplete(failure);
                Volatile.Read(ref _recovery).TrySetCanceled();
                if (failure is not OperationCanceledException) ReportFailure(failure);
                throw;
            }
            finally
            {
                DropQueuedEntries();
                sink.Dispose();
            }
        });
    }

    internal DurableLogOptions Options { get; }
    internal Task Completion => _worker;
    internal long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    internal Task Recovery => Volatile.Read(ref _recovery).Task;

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private static bool IsIoFailure(Exception failure) => failure is IOException or UnauthorizedAccessException;

    private static void ReportFailure(Exception failure)
    {
        var type = failure.GetType().Name;
        if (type.Length > 128) type = type[..128];
        try { Console.Error.WriteLine($"Durable log failure: {type}; records may be lost."); }
        catch (Exception stderrFailure) when (stderrFailure is IOException or ObjectDisposedException) { }
    }

    private void BreakSink(RollingSink sink, Exception failure)
    {
        Volatile.Write(ref _broken, 1);
        Volatile.Write(ref _recovery, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        ReportFailure(failure);
        sink.CloseStream();
        DropQueuedEntries();
    }

    private void DropQueuedEntries()
    {
        while (_channel.Reader.TryRead(out var entry))
        {
            if (entry.Bytes is null) continue;
            Interlocked.Increment(ref _dropped);
            Interlocked.Add(ref _queuedBytes, -entry.Bytes.Length);
        }
    }

    private async Task RecoverAsync(RollingSink sink, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var milliseconds = Options.RetryInitialMs;
        while (true)
        {
            await delay(TimeSpan.FromMilliseconds(milliseconds), _shutdown.Token).ConfigureAwait(false);
            try
            {
                sink.Reopen();
                DropQueuedEntries();
                Volatile.Write(ref _broken, 0);
                await WriteDropsAsync(sink, startNewLine: true).ConfigureAwait(false);
                Volatile.Read(ref _recovery).TrySetResult();
                return;
            }
            catch (Exception failure) when (IsIoFailure(failure))
            {
                // One stderr line describes the outage; retries retain its counter and rotation progress.
                Volatile.Write(ref _broken, 1);
                sink.CloseStream();
                DropQueuedEntries();
                milliseconds = Math.Min(milliseconds * 2, Options.RetryMaxMs);
            }
        }
    }

    /// <summary>Creates a category logger whose filtering is controlled by the owning logging factory.</summary>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    // ILoggerFactory disposes providers synchronously. Admission stops here; the host's async owner
    // waits for the same worker after the factory has finished disposing all providers.
    /// <summary>Stops accepting records and gives the single writer a bounded interval to drain them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _shutdown.CancelAfter(Options.ShutdownTimeoutMs);
    }

    /// <summary>Waits for retained records to drain and reports writer failures or an expired shutdown budget.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_drainLock) return new ValueTask(_drain ??= DrainAsync());
    }

    private async Task DrainAsync()
    {
        Dispose();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromMilliseconds(Options.ShutdownTimeoutMs)).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // A timeout must request cancellation before the caller can release its resources.
            if (!_worker.IsCompleted) _shutdown.Cancel();
            throw new IOException("The durable log did not drain cleanly during shutdown.", failure);
        }
        finally
        {
            // A timed-out writer retains its cancellation source until its IO has actually stopped.
            if (_worker.IsCompleted) _shutdown.Dispose();
            else
            {
                _ = _worker.ContinueWith(task =>
                {
                    _ = task.Exception;
                    _shutdown.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async ValueTask WriteDropsAsync(RollingSink sink, bool startNewLine = false)
    {
        var count = Interlocked.Exchange(ref _dropped, 0);
        if (count == 0) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} Warning bff.durable-log: dropped_entries={count}; records dropped after queue, byte-bound, formatting or I/O failure.\n");
        try
        {
            await sink.WriteAsync(Utf8.GetBytes((startNewLine ? "\n" : "") + line), _shutdown.Token).ConfigureAwait(false);
            await sink.FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Add(ref _dropped, count);
            throw;
        }
    }

    private void Drop()
    {
        Interlocked.Increment(ref _dropped);
        if (Volatile.Read(ref _broken) != 0) return;
        // A rejected oversized entry may be the only log call; wake the consumer for its loss report.
        _channel.Writer.TryWrite(new(null, LogLevel.Warning));
    }

    private bool Reserve(int bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _queuedBytes);
            if (bytes > Options.MaxQueuedBytes - current) return false;
            if (Interlocked.CompareExchange(ref _queuedBytes, current + bytes, current) == current) return true;
        }
    }

    private void Log<TState>(string category, LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (Volatile.Read(ref _closed) != 0 || level == LogLevel.None) return;
        if (Volatile.Read(ref _broken) != 0) { Drop(); return; }
        var reserved = 0;
        try
        {
            var message = formatter(state, exception);
            var details = exception?.ToString();
            // Admitted exceptions are complete. An oversized entry is counted as lost rather than
            // truncating its message, inner exceptions or stack into misleading evidence.
            var limit = Math.Min(Options.MaxFileBytes, Options.MaxQueuedBytes);
            if (message.Length > limit || details?.Length > limit || category.Length > limit)
            {
                Drop();
                return;
            }
            var prefix = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} {level} {category}[{eventId.Id}]: ");
            var byteCount = (long)Utf8.GetByteCount(prefix) + Utf8.GetByteCount(message) + 1
                + (details is null ? 0 : (long)Utf8.GetByteCount(details) + 1);
            if (byteCount > limit || !Reserve((int)byteCount))
            {
                Drop();
                return;
            }
            reserved = (int)byteCount;
            var text = prefix + message + "\n" + (details is null ? "" : details + "\n");
            var payload = Utf8.GetBytes(text);
            if (Volatile.Read(ref _broken) != 0 || !_channel.Writer.TryWrite(new(payload, level)))
            {
                Drop();
                return;
            }
            reserved = 0;
        }
        catch (Exception)
        {
            // Logging cannot replace the gameplay failure that it was asked to record.
            Drop();
        }
        finally
        {
            if (reserved != 0) Interlocked.Add(ref _queuedBytes, -reserved);
        }
    }

    private sealed record Entry(byte[]? Bytes, LogLevel Level);

    private sealed class FileLogger(RotatingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref provider._closed) == 0;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Log(category, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class RollingSink : IDisposable
    {
        private readonly string _path;
        private readonly DurableLogOptions _options;
        private readonly FileStream _lock;
        private FileStream? _stream;
        private int? _rotationIndex;
        private bool _deleteOldest;

        internal RollingSink(DurableLogOptions options)
        {
            _options = options;
            _path = Path.GetFullPath(options.File);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _lock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                // Retention also applies after a restart or a reduction in the configured slot count.
                // Only the bounded, exact file names owned by this sink are ever removed.
                Reopen();
                for (var index = options.FileCount; index < DurableLogOptions.MaximumFileCount; index++)
                    File.Delete(Slot(index));
            }
            catch
            {
                CloseStream();
                _lock.Dispose();
                throw;
            }
        }

        private string Slot(int index) => index == 0 ? _path : Path.Combine(Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.{index}{Path.GetExtension(_path)}");

        private FileStream Open()
        {
            var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            try
            {
                stream.Seek(0, SeekOrigin.End);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal void Reopen()
        {
            if (_rotationIndex is not null)
            {
                FinishRotation();
                return;
            }
            _stream = Open();
            if (_stream.Length > _options.MaxFileBytes) Rotate();
        }

        private void Rotate()
        {
            CloseStream();
            _rotationIndex = _options.FileCount - 2;
            _deleteOldest = true;
            FinishRotation();
        }

        private void FinishRotation()
        {
            if (_deleteOldest)
            {
                File.Delete(Slot(_options.FileCount - 1));
                _deleteOldest = false;
            }
            // A viewer can block only the last move. Resume there, retaining already shifted backups.
            while (_rotationIndex is >= 0)
            {
                var index = _rotationIndex.Value;
                if (System.IO.File.Exists(Slot(index))) File.Move(Slot(index), Slot(index + 1), true);
                _rotationIndex = index - 1;
            }
            _stream = Open();
            _rotationIndex = null;
        }

        internal async ValueTask WriteAsync(byte[] bytes, CancellationToken ct)
        {
            var stream = _stream ?? throw new IOException("The durable log stream is awaiting recovery.");
            if (stream.Length + bytes.Length > _options.MaxFileBytes)
            {
                await stream.FlushAsync(ct).ConfigureAwait(false);
                Rotate();
                stream = _stream!;
            }
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        internal Task FlushAsync(CancellationToken ct) =>
            (_stream ?? throw new IOException("The durable log stream is awaiting recovery.")).FlushAsync(ct);

        internal void CloseStream()
        {
            var stream = _stream;
            _stream = null;
            try { stream?.Dispose(); }
            catch (Exception failure) when (IsIoFailure(failure)) { }
        }

        public void Dispose()
        {
            try { CloseStream(); }
            finally { _lock.Dispose(); }
        }
    }
}
