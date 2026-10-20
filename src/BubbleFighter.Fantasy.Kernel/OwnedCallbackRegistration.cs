using System.Runtime.ExceptionServices;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Keeps registration ownership until both revocation and admitted callbacks have finished.</summary>
internal sealed class OwnedCallbackRegistration(PluginContext owner) : IAsyncDisposable
{
    private static readonly AsyncLocal<Invocation?> Current = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _registration;
    private int _running;
    private bool _closing;

    internal bool IsExecuting
    {
        get
        {
            for (var frame = Current.Value; frame is not null; frame = frame.Previous)
                if (Volatile.Read(ref frame.Active) != 0 && ReferenceEquals(frame.Registration, this)) return true;
            return false;
        }
    }

    internal void Attach(IDisposable registration) =>
        _registration = registration ?? throw new InvalidOperationException("A registration factory returned no ownership handle.");

    internal bool TryEnter(out IDisposable? invocation)
    {
        lock (_gate)
        {
            if (_closing || !owner.AcceptsCallbacks) { invocation = null; return false; }
            _running++;
        }
        invocation = new Invocation(this, owner.EnterCallback());
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closing = true;
            if (_running == 0) _drained.TrySetResult();
        }
        Exception? failure = null;
        try { await Task.Run(_registration!.Dispose).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        // A synchronous provider may stop waiting early; its return does not prove callback quiescence.
        await _drained.Task.ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Exit()
    {
        lock (_gate)
            if (--_running == 0 && _closing) _drained.TrySetResult();
    }

    private sealed class Invocation : IDisposable
    {
        private readonly IDisposable _contextFrame;
        internal readonly OwnedCallbackRegistration Registration;
        internal readonly Invocation? Previous;
        internal int Active = 1;

        internal Invocation(OwnedCallbackRegistration registration, IDisposable contextFrame)
        {
            Registration = registration;
            _contextFrame = contextFrame;
            Previous = Current.Value;
            Current.Value = this;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref Active, 0) == 0) return;
            _contextFrame.Dispose();
            Current.Value = Previous;
            Registration.Exit();
        }
    }
}
