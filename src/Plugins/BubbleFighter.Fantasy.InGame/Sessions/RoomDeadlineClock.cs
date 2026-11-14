using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.RoomCore;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>One replaceable mode deadline, executed on the room queue and drained when its room stops.</summary>
internal sealed class RoomDeadlineClock
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping;
    private TaskCompletionSource _changed = NewSignal();
    private long _revision;
    private long _due;
    private Func<CancellationToken, ValueTask>? _step;
    private bool _disposed;

    internal RoomDeadlineClock(RoomExecutor executor, CancellationToken lifetime, Action<Exception> failed)
        : this(executor, lifetime, failure => { failed(failure); return ValueTask.CompletedTask; })
    {
    }

    internal RoomDeadlineClock(RoomExecutor executor, CancellationToken lifetime, Func<Exception, ValueTask> failed)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Completion = BackgroundWork.Detached(() => RunAsync(executor, failed));
    }

    internal Task Completion { get; }

    internal void Schedule(int delayMs, Func<CancellationToken, ValueTask> step)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested) return;
            _due = Environment.TickCount64 + delayMs;
            _step = step;
            _revision++;
            _changed.TrySetResult();
            _changed = NewSignal();
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping.Cancel();
            _step = null;
        }
    }

    private async Task RunAsync(RoomExecutor executor, Func<Exception, ValueTask> failed)
    {
        var token = _stopping.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task changed;
                long revision, due;
                Func<CancellationToken, ValueTask>? step;
                lock (_gate)
                {
                    changed = _changed.Task;
                    revision = _revision;
                    due = _due;
                    step = _step;
                }
                if (step is null)
                {
                    await changed.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var delay = Task.Delay((int)Math.Clamp(due - Environment.TickCount64, 0, int.MaxValue), wait.Token);
                    var completed = await Task.WhenAny(delay, changed).ConfigureAwait(false);
                    await wait.CancelAsync().ConfigureAwait(false);
                    try { await delay.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                    token.ThrowIfCancellationRequested();
                    if (completed == changed) continue;
                }
                await executor.ExecuteAsync(async ct =>
                {
                    lock (_gate)
                    {
                        if (_revision != revision || token.IsCancellationRequested) return;
                        _step = null;
                    }
                    await step(ct).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure) { await failed(failure).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                _step = null;
                _stopping.Dispose();
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
