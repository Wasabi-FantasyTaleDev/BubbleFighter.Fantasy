namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Retains a native callback context until every admitted nested operation has actually completed.</summary>
internal class RemoteCallbackInvocation(ExecutionContext? context, CancellationToken cancellation)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _running;

    internal CancellationToken Cancellation { get; } = cancellation;

    internal bool TryEnter()
    {
        lock (_gate)
        {
            if (_closed) return false;
            _running++;
            return true;
        }
    }

    internal void Run(Action action)
    {
        if (context is null) throw new InvalidOperationException("An SDK callback requires a flowing native invocation context.");
        ExecutionContext.Run(context, _ => action(), null);
    }

    internal void Exit()
    {
        lock (_gate)
            if (--_running == 0 && _closed) _drained.TrySetResult();
    }

    internal Task CloseAsync()
    {
        lock (_gate)
        {
            _closed = true;
            if (_running == 0) _drained.TrySetResult();
            return _drained.Task;
        }
    }
}
