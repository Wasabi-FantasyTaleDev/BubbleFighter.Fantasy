namespace BubbleFighter.Fantasy.Contracts.Signals;

/// <summary>
/// Tells a periodic worker whether there is anything for it to look at, so it can sleep instead of polling. The
/// owner of some state (the rooms of a build, the registrations of a directory) calls <see cref="Refresh"/> after
/// each change; a worker awaits <see cref="WhenPresent"/> before it starts its timer. The signal is level-triggered:
/// the task stays completed for as long as the state is present, so a worker that looks just after a change still
/// sees it and no wake-up can be missed between its check and its wait.
/// </summary>
public sealed class PresenceSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _present = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isPresent;

    /// <summary>Gets whether the last <see cref="Refresh"/> found the state present.</summary>
    public bool IsPresent
    {
        get { lock (_gate) return _isPresent; }
    }

    /// <summary>
    /// Gets a task that is complete while the state is present and pending while it is absent. It never faults and
    /// is never cancelled; a worker that must stop waiting adds its own cancellation with
    /// <see cref="Task.WaitAsync(CancellationToken)"/>. Reading it costs nothing, so it may be checked on every tick.
    /// </summary>
    public Task WhenPresent
    {
        get { lock (_gate) return _present.Task; }
    }

    /// <summary>
    /// Evaluates whether the state is present and records it. The question is asked inside the signal's own lock,
    /// so when two changes race, the refresh that runs last also reads the state last and the signal cannot be left
    /// saying "absent" while something is there.
    /// </summary>
    /// <param name="present">Reads the owner's state; it must not wait on anything that could call this signal.</param>
    public void Refresh(Func<bool> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        lock (_gate)
        {
            var now = present();
            if (now == _isPresent)
                return;
            _isPresent = now;
            if (now)
                _present.TrySetResult();
            else
                _present = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
