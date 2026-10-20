using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    // Serializes every publication of rows, catalog and entry tree. Holders only build and publish
    // snapshots synchronously; they never await producers, readers or cleanup while holding it.
    private readonly SemaphoreSlim _metadataMutation = new(1, 1);
    private int _metadataWaiters;

    private async ValueTask<IDisposable> EnterDefinitionMetadataMutationAsync(CancellationToken cancellationToken)
    {
        // Each owned source contributes at most one admission and one retirement, plus one
        // configuration caller; anything beyond that is a caller bug, not a queue to grow.
        var limit = checked(2 * _budget.DefinitionSourceCapacity + 1);
        if (Interlocked.Increment(ref _metadataWaiters) > limit)
        {
            Interlocked.Decrement(ref _metadataWaiters);
            throw new InvalidOperationException("Definition metadata publication has more waiters than its fixed capacity.");
        }
        try
        {
            await _metadataMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _metadataWaiters);
            throw;
        }
        return new MetadataMutation(this);
    }

    private sealed class MetadataMutation(PluginHost host) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Interlocked.Decrement(ref host._metadataWaiters);
            host._metadataMutation.Release();
        }
    }

    /// <summary>
    /// Rejects metadata in which a source owner's own namespace depends on a source it owns:
    /// retiring that source would wait for the owner's scope while the owner's unwind waits for the source.
    /// </summary>
    private static void RejectSourceOwnerNamespaceCycles(IReadOnlyList<PluginRow> rows, PluginCatalog catalog,
        IReadOnlyDictionary<string, DefinitionSource> sources)
    {
        foreach (var group in sources.GroupBy(pair => pair.Value.Owner))
        {
            var owned = group.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var without = PluginCatalog.FromDefinitions(catalog.Plugins.Where(descriptor => !owned.Contains(descriptor.Id)));
            if (PluginEntryTree.IsolationDiffers(rows, group.Key.Row.Id, catalog, without) is { } name)
                throw new ConfigException(group.Key.Row.Id,
                    [$"isolated_services: '{name}' of this entry or an ancestor resolves through a definition source the entry itself owns."]);
        }
    }
}
