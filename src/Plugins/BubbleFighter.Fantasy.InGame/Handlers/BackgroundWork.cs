namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>Room timers that run off the room executor and later queue their own operations.</summary>
internal static class BackgroundWork
{
    /// <summary>
    /// Starts timer work in a fresh execution context, so a timer armed inside a room operation can
    /// later queue its own operation without tripping the executor's reentry guard.
    /// </summary>
    /// <param name="work">The timer work.</param>
    /// <returns>The running work.</returns>
    internal static Task Detached(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return Task.Run(work);
        using (ExecutionContext.SuppressFlow())
            return Task.Run(work);
    }
}
