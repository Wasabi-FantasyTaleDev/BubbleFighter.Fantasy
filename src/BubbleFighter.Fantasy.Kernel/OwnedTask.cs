using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Supervises one effect-owned operation and joins work before releasing its resource slot.</summary>
internal sealed class OwnedTask : IOwnedTask, IResourceHandle
{
    private static readonly AsyncLocal<WorkFrame?> Current = new();
    private readonly EffectScope _scope;
    private readonly Action<string, Exception> _reportFailure;
    private readonly CancellationTokenSource _stopping;
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource<IAsyncDisposable?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _body = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private TaskCompletionSource? _closed;
    private IAsyncDisposable? _ownership;
    private Exception? _completionFailure;
    private bool _completionCanceled;

    internal OwnedTask(string name, EffectScope scope, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask> work, Action<string, Exception> reportFailure)
    {
        Name = name;
        _scope = scope;
        _reportFailure = reportFailure;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(scope.Lifetime, cancellationToken);
        _token = _stopping.Token;
        Resource = new TaskResource(this);
        _ = Task.Run(() => ExecuteAsync(work));
    }

    public string Name { get; }
    public Task Completion => _completion.Task;
    internal IAsyncDisposable Resource { get; }

    internal void Attach(IAsyncDisposable ownership)
    {
        _ownership = ownership;
        _ready.TrySetResult(ownership);
    }

    public ValueTask DisposeAsync()
    {
        for (var frame = Current.Value; frame is not null; frame = frame.Previous)
            if (Volatile.Read(ref frame.Active) && ReferenceEquals(frame.Owner, this))
                throw new InvalidOperationException($"Owned task '{Name}' cannot wait for its own termination.");
        return _ownership!.DisposeAsync();
    }

    private async Task ExecuteAsync(Func<CancellationToken, ValueTask> work)
    {
        var ownership = await _ready.Task.ConfigureAwait(false);
        try
        {
            _token.ThrowIfCancellationRequested();
            using (new WorkFrame(this))
            using (_scope.EnterCallback())
                await work(_token).ConfigureAwait(false);
            _body.TrySetResult();
        }
        catch (OperationCanceledException ex) when (_token.IsCancellationRequested && ex.CancellationToken == _token)
        {
            _body.TrySetCanceled(_token);
        }
        catch (Exception ex)
        {
            _body.TrySetException(Report(ex));
        }

        try
        {
            // Closure may take ownership before handover; it opens the start gate with no worker admission.
            if (ownership is null) await ReleaseAsync().ConfigureAwait(false);
            else await ownership.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) when (_closed?.Task.IsCompleted == true)
        {
            // Resource teardown prepared the result; the effect publishes it before closure can finish.
        }
        catch (Exception ex)
        {
            // A failure before resource teardown starts must remain observable on the public handle.
            _completion.TrySetException(Report(ex));
            _ = _completion.Task.Exception;
        }
    }

    private Exception Report(Exception failure)
    {
        try { _reportFailure(Name, failure); }
        catch (Exception reportingFailure)
        {
            return new AggregateException("Owned work and failure reporting both failed.", failure, reportingFailure);
        }
        return failure;
    }

    private ValueTask ReleaseAsync()
    {
        TaskCompletionSource closed;
        lock (_gate)
        {
            if (_closed is not null) return new ValueTask(_closed.Task);
            closed = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CloseAsync(closed);
        return new ValueTask(closed.Task);
    }

    private async Task CloseAsync(TaskCompletionSource closed)
    {
        List<Exception> failures = [];
        Exception? workFailure = null;
        var cancellation = _body.Task.IsCompleted ? Task.CompletedTask : _stopping.CancelAsync();
        _ready.TrySetResult(null);
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        try { await _body.Task.ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (_body.Task.IsCanceled && ex.CancellationToken == _token) { }
        catch (Exception ex) { workFailure = ex; }
        _stopping.Dispose();
        Exception? cleanupFailure;
        if (failures.Count == 0 && workFailure is not null)
            cleanupFailure = new OwnedWorkException(Name, workFailure, Resource);
        else if (failures.Count == 0) cleanupFailure = null;
        else if (workFailure is not null)
            cleanupFailure = new AggregateException("Owned work and cancellation cleanup failed.", failures.Append(workFailure));
        else if (failures.Count == 1) cleanupFailure = failures[0];
        else cleanupFailure = new AggregateException("Owned task termination failed.", failures);

        // Prepare reporting outside the effect gate; release notification only publishes this result.
        if (cleanupFailure is not null)
        {
            var normalized = OwnedWorkException.FromCleanup(cleanupFailure, Resource);
            _completionFailure = normalized is OwnedWorkException drained ? drained.InnerException! :
                workFailure is null ? Report(normalized) : normalized;
        }
        _completionCanceled = _body.Task.IsCanceled;
        if (cleanupFailure is null) closed.TrySetResult();
        else closed.TrySetException(cleanupFailure);
    }

    private void PublishCompletion()
    {
        if (_completionFailure is { } failure)
        {
            _completion.TrySetException(failure);
            // Reporting is already complete, so ignored handles never produce unobserved failures.
            _ = _completion.Task.Exception;
        }
        else if (_completionCanceled) _completion.TrySetCanceled(_token);
        else _completion.TrySetResult();
    }

    private sealed class TaskResource(OwnedTask task) : IAsyncDisposable, IResourceReleaseObserver
    {
        public ValueTask DisposeAsync() => task.ReleaseAsync();

        /// <summary>Publishes the prepared task result after cleanup and successful slot release.</summary>
        public void OnReleaseSettled() => task.PublishCompletion();
    }

    private sealed class WorkFrame : IDisposable
    {
        internal readonly OwnedTask Owner;
        internal readonly WorkFrame? Previous;
        internal bool Active = true;
        internal WorkFrame(OwnedTask owner)
        {
            Owner = owner;
            Previous = Current.Value;
            Current.Value = this;
        }
        public void Dispose()
        {
            Volatile.Write(ref Active, false);
            Current.Value = Previous;
        }
    }
}
