using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private async ValueTask AwaitRetiringDefinitionSourcesAsync(string? key, DefinitionSource? selected, CancellationToken cancellationToken)
    {
        if (key is null) return;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DefinitionSource[] draining;
            lock (_intentGate)
                draining = _ownedDefinitionSources.Where(source => !ReferenceEquals(source, selected) &&
                    (source.State is PluginDefinitionSourceState.Retiring or PluginDefinitionSourceState.CleanupFailed) &&
                    source.Keys.Contains(key, StringComparer.Ordinal)).ToArray();
            if (draining.Length == 0) return;
            // Every older revision remains owned; waiting only for the immediate predecessor misses chained replacements.
            var completion = Task.WhenAll(draining.Select(source => source.DisposeAsync().AsTask()));
            _ = completion.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
