namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Describes the outcome of one desired lifecycle revision.</summary>
public enum PluginTransitionOutcome
{
    /// <summary>The requested row reached its desired active or inactive state.</summary>
    Converged,
    /// <summary>The row remains desired but required providers are unavailable.</summary>
    WaitingForDependencies,
    /// <summary>A newer target or host shutdown replaced this revision before it converged. Previously started
    /// work may still be draining; this result does not certify resource release or business rollback.</summary>
    Superseded,
}

/// <summary>Reports convergence without treating acceptance as successful activation.</summary>
/// <param name="Outcome">Whether the revision converged, is waiting, or was replaced.</param>
/// <param name="ChangedRowIds">Rows stopped or started while applying this revision.</param>
public sealed record PluginTransitionResult(PluginTransitionOutcome Outcome, IReadOnlyList<string> ChangedRowIds);

/// <summary>Reports one host intent without owning activation resources. Its result is not a resource lease.</summary>
public interface IPluginTransition
{
    /// <summary>Gets the configured row whose desired state was changed.</summary>
    string RowId { get; }
    /// <summary>Gets the non-reused revision allocated by this host.</summary>
    long Revision { get; }
    /// <summary>Gets whether this receipt has a terminal result or error.</summary>
    bool IsCompleted { get; }
    /// <summary>Waits for this revision. Cancellation stops only observation; waiting from an
    /// owned or lifecycle callback is rejected to prevent waiting for that callback's own drain.</summary>
    /// <param name="cancellationToken">Cancels this observation without withdrawing the desired state.</param>
    /// <returns>The verified convergence outcome.</returns>
    ValueTask<PluginTransitionResult> WaitAsync(CancellationToken cancellationToken = default);
}
