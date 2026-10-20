namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>A child resource lifetime sharing its activation's capacity. This is not a service
/// registry or realm. Disposal cancels descendants and joins reverse-order cleanup exactly once.</summary>
public interface IResourceScope : IAsyncDisposable
{
    /// <summary>Gets the diagnostic name, independent of resource identity.</summary>
    string Name { get; }

    /// <summary>Gets a token canceled when this scope or an ancestor starts closing.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>Owns an already-created disposer. Rejection disposes an unowned resource;
    /// handles already owned by another scope are refused without changing their ownership.</summary>
    IDisposable Effect(IDisposable disposer);

    /// <summary>Owns a cleanup action, released in reverse order with other resources.</summary>
    IDisposable Effect(Action dispose);

    /// <summary>Reserves capacity before invoking a factory with the combined scope and caller
    /// token. Cancellation before admission waits for any late result to be released. After
    /// admission, cancellation signals the token without automatically disposing the resource.</summary>
    ValueTask<IAsyncDisposable> AcquireEffectAsync(
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellationToken = default);

    /// <summary>Starts owned work after capacity reservation. Faults are reported to the root
    /// activation; disposal requests cancellation and waits for actual completion.</summary>
    ValueTask<IOwnedTask> RunTaskAsync(
        string name, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default);

    /// <summary>Creates a child after reserving its ownership in this scope. The child itself
    /// consumes one activation-wide slot until cleanup succeeds or its failure is reported.</summary>
    ValueTask<IResourceScope> CreateResourceScopeAsync(string name);
}
