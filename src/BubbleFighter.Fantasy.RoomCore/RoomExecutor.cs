using System.Threading.Channels;

namespace BubbleFighter.Fantasy.RoomCore;

/// <summary>
/// Executes accepted room operations one at a time, including their asynchronous continuations.
/// The owner supplies queue capacity and must await disposal before releasing room state.
/// </summary>
public sealed class RoomExecutor : IAsyncDisposable
{
    private static readonly AsyncLocal<ExecutionScope?> Current = new();
    private readonly object _gate = new();
    private readonly Channel<WorkItem> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _consumer;
    private Task? _stopTask;
    private bool _accepting = true;
    private int _disposed;

    /// <summary>Creates an executor with at most <paramref name="capacity"/> waiting operations and one running operation.</summary>
    /// <param name="capacity">Positive maximum number of queued operations; supplied by the owning plugin's validated configuration.</param>
    public RoomExecutor(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false, // Shutdown also removes queued work while a callback is running.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _consumer = RunWithoutContext(ConsumeAsync);
    }

    /// <summary>
    /// Accepts an operation or throws immediately if stopped, full, or called from an active room callback.
    /// The returned task observes the operation's result. Cancellation before execution prevents invocation;
    /// cancellation during execution is cooperative and completion waits for the callback to return.
    /// </summary>
    /// <param name="operation">Room work that awaits all of its state mutations and honors its cancellation token.</param>
    /// <param name="cancellationToken">Cancels this operation without stopping unrelated room work.</param>
    /// <returns>A task completed only when canceled before execution or after the callback finishes.</returns>
    /// <exception cref="InvalidOperationException">The queue is full or this call originates in an active room callback.</exception>
    /// <exception cref="ObjectDisposedException">Shutdown has begun.</exception>
    public Task ExecuteAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfReentrant();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            var item = new WorkItem(operation, cancellationToken);
            if (_queue.Writer.TryWrite(item))
                return item.Completion;
            item.Release();
            throw new InvalidOperationException("The room operation queue is full.");
        }
    }

    /// <summary>
    /// Waits for queue capacity before accepting an operation, then observes its completion with the same
    /// cancellation and execution-context semantics as <see cref="ExecuteAsync"/>. The owner must bound
    /// concurrent callers waiting for admission; an active room callback cannot wait for any executor.
    /// </summary>
    /// <param name="operation">Room work that awaits all of its state mutations and honors its cancellation token.</param>
    /// <param name="cancellationToken">Cancels admission or this operation without stopping unrelated room work.</param>
    /// <returns>A task completed after admission fails, cancellation prevents execution, or the callback finishes.</returns>
    /// <exception cref="InvalidOperationException">This call originates in an active room callback.</exception>
    /// <exception cref="ObjectDisposedException">Shutdown begins before the operation is accepted.</exception>
    public Task ExecuteWhenAvailableAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfReentrant();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            // Capture the caller's context before waiting; admission may resume on another thread.
            var item = new WorkItem(operation, cancellationToken);
            return _queue.Writer.TryWrite(item) ? item.Completion : AdmitWhenAvailableAsync(item, cancellationToken);
        }
    }

    private async Task AdmitWhenAvailableAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var accepted = false;
        try
        {
            while (await _queue.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(!_accepting, this);
                    if (!_queue.Writer.TryWrite(item)) continue;
                    accepted = true;
                    break;
                }
            }
            ObjectDisposedException.ThrowIf(!accepted, this);
        }
        finally
        {
            if (!accepted) item.Release();
        }
        await item.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects new work, cancels queued work, signals cancellation to running work, and waits for the consumer.
    /// Canceling the wait does not undo shutdown. A callback that ignores cancellation must finish before shutdown completes.
    /// </summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for shutdown.</param>
    /// <returns>The shared shutdown result, or a cancellable wait for that result.</returns>
    /// <exception cref="InvalidOperationException">Called from an active room callback.</exception>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfReentrant();
        Task stopped;
        lock (_gate)
        {
            if (_stopTask is null)
            {
                _accepting = false;
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out var item))
                {
                    item.CancelQueued(new CancellationToken(canceled: true));
                    item.Release();
                }
                _stopTask = RunWithoutContext(StopCoreAsync);
            }
            stopped = _stopTask;
        }
        return cancellationToken.CanBeCanceled ? stopped.WaitAsync(cancellationToken) : stopped;
    }

    /// <summary>Completes shutdown before releasing the executor's cancellation resources; repeated disposal is safe.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            // A reentrant stop fails before shutdown starts and must not dispose a live source.
            if (_stopTask?.IsCompleted == true && Interlocked.Exchange(ref _disposed, 1) == 0)
                _stopping.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await _consumer.ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            WorkItem? item;
            lock (_gate)
            {
                if (!_queue.Reader.TryRead(out item))
                    continue;
                if (!item.TryStart())
                {
                    item.Release();
                    continue;
                }
            }
            var previous = Current.Value;
            var scope = new ExecutionScope();
            Current.Value = scope;
            try
            {
                await item.RunAsync(_stopping.Token, scope).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref scope.Active, 0);
                Current.Value = previous;
                item.Release();
            }
        }
    }

    private void ThrowIfReentrant()
    {
        if (Current.Value is { } scope && Volatile.Read(ref scope.Active) != 0)
            throw new InvalidOperationException("A room callback cannot enqueue or stop any room executor; coordinate rooms outside callbacks.");
    }

    private static Task RunWithoutContext(Func<Task> action)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return Task.Run(action);
        using (ExecutionContext.SuppressFlow())
            return Task.Run(action);
    }

    private sealed class ExecutionScope
    {
        internal int Active = 1;
    }

    private sealed class WorkItem
    {
        private readonly Func<CancellationToken, ValueTask> _operation;
        private readonly CancellationToken _callerToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        // The caller's context, so ambient per-call state (the session's account mutation scope among it) reaches the
        // room callback as it would an awaited call; null when the caller suppressed flow.
        private readonly ExecutionContext? _context = ExecutionContext.Capture();
        private int _state;

        internal WorkItem(Func<CancellationToken, ValueTask> operation, CancellationToken callerToken)
        {
            _operation = operation;
            _callerToken = callerToken;
            _registration = callerToken.UnsafeRegister(static state =>
            {
                var item = (WorkItem)state!;
                item.CancelQueued(item._callerToken);
            }, this);
        }

        internal Task Completion => _completion.Task;
        internal bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        internal void CancelQueued(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                _completion.TrySetCanceled(token);
        }

        internal async ValueTask RunAsync(CancellationToken stoppingToken, ExecutionScope scope)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, stoppingToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                await Start(linked.Token, scope).ConfigureAwait(false);
                _completion.TrySetResult();
            }
            catch (OperationCanceledException exception) when (linked.IsCancellationRequested)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _state, 2);
            }
        }

        internal void Release() => _registration.Dispose();

        // Starts the callback inside the caller's context; the executor's own scope is set there too, so reentrancy is
        // still detected. Continuations keep the context the callback started in.
        private ValueTask Start(CancellationToken token, ExecutionScope scope)
        {
            if (_context is null) return _operation(token);
            var start = new Starter(_operation, token, scope);
            ExecutionContext.Run(_context, static state => ((Starter)state!).Run(), start);
            return start.Pending;
        }

        private sealed class Starter(Func<CancellationToken, ValueTask> operation, CancellationToken token, ExecutionScope scope)
        {
            internal ValueTask Pending;

            internal void Run()
            {
                Current.Value = scope;
                Pending = operation(token);
            }
        }
    }
}
