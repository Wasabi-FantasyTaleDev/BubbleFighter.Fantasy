using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private void CaptureSourceNamespaceReaders(DefinitionSource source, IReadOnlySet<string> changedScopes, PluginEntryTree target)
    {
        if (source.NamespaceReadersCaptured) return;
        var holders = _hostedContexts.Where(context => changedScopes.Contains(context.Row.Id) &&
            (!target.Entries.TryGetValue(context.Row.Id, out var next) ||
             !ReferenceEquals(context.NativeServices!.Scope, next.Scope))).ToArray();
        if (holders.Length > _budget.EntryCapacity)
            throw new ConfigException("Source namespace reader ownership exceeds startup entry capacity.");
        source.NamespaceReaders.UnionWith(holders);
        source.NamespaceReadersCaptured = true;
        if (source.NamespaceReaders.Count == 0) source.NamespaceReadersDrained.TrySetResult();
    }

    private void MarkChangedSourceNamespaces(IReadOnlySet<string> changedScopes, PluginEntryTree target)
    {
        foreach (var context in _hostedContexts.Where(context => changedScopes.Contains(context.Row.Id) &&
            (!target.Entries.TryGetValue(context.Row.Id, out var next) ||
             !ReferenceEquals(context.NativeServices!.Scope, next.Scope))))
            context.Supersede();
        foreach (var rowId in changedScopes)
        {
            _bindingDirtyRows.Add(rowId);
            foreach (var dependent in DependentsClosure(rowId)) _bindingDirtyRows.Add(dependent.Row.Id);
        }
    }

    private void ReleaseSourceNamespaceReaders(PluginContext context)
    {
        foreach (var source in _ownedDefinitionSources)
            if (source.NamespaceReaders.Remove(context) && source.NamespaceReadersCaptured && source.NamespaceReaders.Count == 0)
                source.NamespaceReadersDrained.TrySetResult();
    }
}
