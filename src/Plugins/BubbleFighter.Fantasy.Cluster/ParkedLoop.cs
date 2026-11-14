namespace BubbleFighter.Fantasy.Cluster;

/// <summary>
/// A periodic clean-up that only runs while there is something to clean up. The tick and its period are what they
/// were; while the table it looks at is empty the loop holds no timer and wakes for nothing.
/// </summary>
internal static class ParkedLoop
{
    /// <summary>
    /// Runs <paramref name="tick"/> every <paramref name="period"/> for as long as the task from
    /// <paramref name="whenWork"/> is complete, and sleeps on that task while it is pending. The task is read before
    /// every wait, so the tick after the last entry has gone still runs once.
    /// </summary>
    /// <param name="period">The time between ticks while there is work.</param>
    /// <param name="clock">The clock the timer runs on.</param>
    /// <param name="whenWork">Returns a level-triggered task: complete while there is work, pending otherwise.</param>
    /// <param name="tick">One pass of the loop.</param>
    /// <param name="ct">Stops the loop; the method then throws <see cref="OperationCanceledException"/>.</param>
    internal static async ValueTask RunAsync(TimeSpan period, TimeProvider clock, Func<Task> whenWork,
        Func<CancellationToken, ValueTask> tick, CancellationToken ct)
    {
        PeriodicTimer? timer = null;
        try
        {
            while (true)
            {
                var work = whenWork();
                if (!work.IsCompleted)
                {
                    // A periodic timer fires whether or not anyone waits on it, so it is released, not just ignored.
                    timer?.Dispose();
                    timer = null;
                    await work.WaitAsync(ct).ConfigureAwait(false);
                }
                timer ??= new PeriodicTimer(period, clock);
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    return;
                await tick(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            timer?.Dispose();
        }
    }
}
