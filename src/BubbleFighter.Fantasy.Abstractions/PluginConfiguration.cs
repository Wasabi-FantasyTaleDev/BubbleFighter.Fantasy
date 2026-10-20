namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Reports an accepted configuration revision and the outcome of applying it.
/// Acceptance does not promise transactional rollback of plugin or business effects.</summary>
/// <param name="Revision">The host configuration revision, independent of target and activation revisions.</param>
/// <param name="Outcome">Whether application converged, awaits dependencies, or was superseded.</param>
/// <param name="ChangedRowIds">Rows whose activations stopped or started while applying this configuration.</param>
public sealed record PluginConfigurationResult(long Revision, PluginTransitionOutcome Outcome, IReadOnlyList<string> ChangedRowIds);
