namespace BubbleFighter.Fantasy.Host;

/// <summary>Transfers operating-system cancellation requests into a joinable asynchronous callback lifetime.</summary>
internal sealed class OwnedShutdownSignal : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private Task? _cancellation;
    private Task? _completion;
    private bool _completing;
    private int _restartRequested;

    /// <summary>Captures the token before ownership can close or dispose its cancellation source.</summary>
    internal OwnedShutdownSignal() => Token = _source.Token;

    /// <summary>Gets the stable token used by the hosted startup and server wait operations.</summary>
    internal CancellationToken Token { get; }

    /// <summary>Gets whether shutdown was requested by an authenticated restart operation.</summary>
    internal bool RestartRequested => Volatile.Read(ref _restartRequested) != 0;

    /// <summary>Requests cancellation once without running or surfacing plugin callbacks on the signal-handler thread.</summary>
    internal void RequestCancellation()
    {
        lock (_gate)
        {
            if (_completing || _cancellation is not null) return;
            try
            {
                _cancellation = _source.CancelAsync();
            }
            catch (Exception error)
            {
                // Even an initiation failure belongs to the async owner, never the OS callback.
                _cancellation = Task.FromException(error);
            }
        }
    }

    /// <summary>Requests normal owned shutdown and marks the process for replacement after cleanup.</summary>
    internal void RequestRestart()
    {
        Interlocked.Exchange(ref _restartRequested, 1);
        RequestCancellation();
    }

    /// <summary>Closes signal admission, waits for actual callbacks and disposes the source with one shared completion result.</summary>
    internal ValueTask CompleteAsync()
    {
        lock (_gate)
        {
            if (_completion is not null) return new ValueTask(_completion);
            _completing = true;
            _completion = CompleteCoreAsync(_cancellation ?? Task.CompletedTask);
            return new ValueTask(_completion);
        }
    }

    /// <summary>Joins requested cancellation and reports callback failure after releasing the cancellation source.</summary>
    public ValueTask DisposeAsync() => CompleteAsync();

    private async Task CompleteCoreAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        finally
        {
            _source.Dispose();
        }
    }
}
