namespace BubbleFighter.Fantasy.Kernel;

/// <summary>Reports failed work only after its owned execution and cancellation resources have drained.</summary>
internal sealed class OwnedWorkException(string name, Exception failure, IAsyncDisposable source)
    : Exception($"Owned task '{name}' failed after its resources were drained.", failure)
{
    private IAsyncDisposable SourceResource { get; } = source;

    internal static bool IsDrained(Exception failure)
    {
        var pending = new Stack<Exception>();
        pending.Push(failure);
        while (pending.TryPop(out var error))
        {
            if (error is OwnedWorkException) continue;
            // Empty aggregates remain failures; flattening would erase them.
            if (error is not AggregateException aggregate || aggregate.InnerExceptions.Count == 0) return false;
            foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
        }
        return true;
    }

    internal static Exception FromCleanup(Exception failure, IAsyncDisposable? resource)
    {
        if (failure is OwnedWorkException own && ReferenceEquals(own.SourceResource, resource)) return failure;
        var pending = new Stack<Exception>();
        pending.Push(failure);
        while (pending.TryPop(out var error))
        {
            if (error is OwnedWorkException)
                return new InvalidOperationException("Cleanup propagated a work error without proving its own resource was drained.", failure);
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
        }
        return failure;
    }
}
