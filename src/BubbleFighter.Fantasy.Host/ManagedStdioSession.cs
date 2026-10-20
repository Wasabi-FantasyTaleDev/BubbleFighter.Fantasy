namespace BubbleFighter.Fantasy.Host;

/// <summary>Owns one bounded standard-input control stream and serializes readiness against stop admission.</summary>
internal sealed class ManagedStdioSession : IAsyncDisposable
{
    internal const string ReadyMarker = "FT_HOST_READY 1";
    internal const int MaximumCommandBytes = 64;
    private readonly object _gate = new();
    private readonly Stream _input;
    private readonly TextWriter _output;
    private readonly OwnedShutdownSignal _shutdown;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _monitor;
    private Task? _completion;
    private Exception? _failure;
    private bool _stopped;
    private bool _ready;

    /// <summary>Starts one input owner whose read callback must support cancellation while waiting for a byte.</summary>
    internal ManagedStdioSession(Stream input, TextWriter output, OwnedShutdownSignal shutdown,
        Func<CancellationToken, ValueTask<int>> readByte)
    {
        _input = input;
        _output = output;
        _shutdown = shutdown;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        _monitor = MonitorAsync(readByte);
    }

    /// <summary>Writes and flushes the single readiness marker only after the caller proves startup convergence.</summary>
    internal void PublishReady()
    {
        lock (_gate)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            if (_stopped) throw new OperationCanceledException(_shutdown.Token);
            if (_ready) throw new InvalidOperationException("Managed readiness was already published.");
            _output.WriteLine(ReadyMarker);
            _output.Flush();
            _ready = true;
        }
    }

    private async Task MonitorAsync(Func<CancellationToken, ValueTask<int>> readByte)
    {
        var line = new byte[MaximumCommandBytes];
        var length = 0;
        try
        {
            while (true)
            {
                var value = await readByte(_lifetime.Token).ConfigureAwait(false);
                if (value == -1)
                {
                    Stop(null);
                    return;
                }
                if (value == '\n')
                {
                    if (length > 0 && line[length - 1] == '\r') length--;
                    if (!line.AsSpan(0, length).SequenceEqual("stop"u8))
                        throw new InvalidOperationException("Unknown managed-stdio command; expected stop.");
                    Stop(null);
                    return;
                }
                if (length == line.Length)
                    throw new InvalidOperationException($"Managed-stdio command exceeds {MaximumCommandBytes} bytes.");
                line[length++] = checked((byte)value);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Stop(error); }
    }

    private void Stop(Exception? failure)
    {
        lock (_gate)
        {
            _stopped = true;
            _failure = failure;
            _shutdown.RequestCancellation();
        }
    }

    /// <summary>Cancels and joins the input owner before disposing its stream and reporting a control-protocol failure.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopped = true;
            return new ValueTask(_completion ??= CompleteAsync());
        }
    }

    private async Task CompleteAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _monitor.ConfigureAwait(false);
            if (_failure is not null)
                throw new InvalidOperationException($"Managed-stdio input failed: {_failure.Message}", _failure);
        }
        finally
        {
            try { _input.Dispose(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
