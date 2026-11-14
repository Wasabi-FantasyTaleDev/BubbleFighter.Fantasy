namespace BubbleFighter.Fantasy.MatchConnection;

/// <summary>
/// A periodic loop that only runs while there is work. With thirteen builds loaded, the UDP setup loops used to wake
/// 480 times a second on a server nobody was playing on; this keeps the tick and its period exactly as they were
/// while something is present and holds no timer at all while nothing is.
/// </summary>
internal static class DemandLoop
{
    /// <summary>
    /// Runs <paramref name="tick"/> every <paramref name="period"/> for as long as the task from
    /// <paramref name="whenWork"/> is complete, and sleeps on that task while it is pending. The presence is read
    /// before every wait, so the tick after the last piece of work has gone still runs once and can clean up.
    /// </summary>
    /// <param name="period">The time between ticks while there is work.</param>
    /// <param name="whenWork">Returns a level-triggered task: complete while there is work, pending otherwise.</param>
    /// <param name="tick">One pass of the loop.</param>
    /// <param name="ct">Stops the loop; the method then throws <see cref="OperationCanceledException"/>.</param>
    internal static async ValueTask RunAsync(TimeSpan period, Func<Task> whenWork, Func<CancellationToken, ValueTask> tick,
        CancellationToken ct)
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
                timer ??= new PeriodicTimer(period);
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
