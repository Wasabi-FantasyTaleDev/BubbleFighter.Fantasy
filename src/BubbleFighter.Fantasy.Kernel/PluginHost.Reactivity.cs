using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private IReadOnlyDictionary<ServiceKey, HashSet<string>> _bindingReaders = new Dictionary<ServiceKey, HashSet<string>>();
    private readonly HashSet<string> _bindingDirtyRows = new(StringComparer.Ordinal);
    private object _bindingChangeStamp = new();
    private bool _bindingChangePending;
    private TaskCompletionSource? _bindingSettlement;
    private readonly Dictionary<string, int> _automaticAttempts = new(StringComparer.Ordinal);

    private void InitializeBindingReconciliation()
    {
        _bindingReaders = BuildBindingWatches(_rows, _configuration.Tree);
        _registry.BindingChanged = OnBindingChanged;
    }

    private IReadOnlyDictionary<ServiceKey, HashSet<string>> BuildBindingWatches(IReadOnlyList<PluginRow> rows, PluginEntryTree tree, Func<PluginRow, PluginDescriptor?>? lookup = null)
    {
        var result = new Dictionary<ServiceKey, HashSet<string>>();
        foreach (var row in rows)
            if ((lookup is null ? DefinitionFor(row) : lookup(row)) is { } descriptor)
                foreach (var contract in PluginServiceDeclarations.Reads(Metadata(descriptor)))
                {
                    var key = tree.Entries[row.Id].Scope.Resolve(contract);
                    if (!result.TryGetValue(key, out var readers)) result[key] = readers = new(StringComparer.Ordinal);
                    readers.Add(row.Id);
                }
        return result;
    }

    private void OnBindingChanged(ServiceKey key)
    {
        lock (_intentGate)
        {
            if (!_bindingReaders.TryGetValue(key, out var readers)) return;
            _bindingChangeStamp = new();
            _bindingDirtyRows.UnionWith(readers);
            _bindingChangePending = true;
            if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var execution in _runningExecutions)
            {
                if (execution.Reads.Contains(key) || execution.Writes.Contains(key)) execution.Stamp = new();
                if (execution.Context is { } context && context.NativeServices!.Access is { } access && !access.MatchesCurrent())
                { context.Supersede(); execution.Request.Supersede(); _bindingDirtyRows.UnionWith(execution.Rows); }
            }
            ScheduleIntents();
        }
    }

    internal Task WaitForBindingChangesAsync(CancellationToken ct = default)
    {
        if (EffectScope.IsInsideCallback || (_transitionFrame.Value is { } frame && Volatile.Read(ref frame.Active)))
            throw new InvalidOperationException("A callback cannot wait for dependency reconciliation.");
        lock (_intentGate) return _bindingSettlement?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    private async ValueTask ReconcileBindingsAsync(TransitionRequest request, HashSet<string> changed)
    {
        var attempts = _automaticAttempts;
        try
        {
            while (true)
            {
                request.Token.ThrowIfCancellationRequested();
                if (CurrentExecution is { } activeExecution)
                    ValidateCodeRefreshPlan(request, activeExecution, includeRequestedOperation: false);
                object stamp;
                lock (_intentGate) stamp = CurrentExecution?.Stamp ?? _bindingChangeStamp;
                var stale = _live.Where(item => Touches(item.Row.Id) && !item.Context.IsClosing && HasStaleActivationView(item)).ToArray();
                var affected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in stale)
                {
                    affected.Add(item.Row.Id);
                    foreach (var dependent in DependentsClosure(item.Row.Id)) affected.Add(dependent.Row.Id);
                }
                foreach (var item in _live.Where(item => Touches(item.Row.Id) && affected.Contains(item.Row.Id)).Reverse().ToArray())
                {
                    request.Token.ThrowIfCancellationRequested();
                    await UnloadCoreAsync(item.Row.Id, request.Token).ConfigureAwait(false);
                    changed.Add(item.Row.Id);
                }
                await StartDesiredRowsAsync(request, changed, automaticAttempts: attempts).ConfigureAwait(false);
                lock (_intentGate)
                    if (ReferenceEquals(stamp, CurrentExecution?.Stamp ?? _bindingChangeStamp))
                    {
                        if (CurrentExecution is { } execution)
                        {
                            _bindingDirtyRows.ExceptWith(execution.Rows);
                            foreach (var rowId in execution.Rows) _automaticAttempts.Remove(rowId);
                        }
                        else { _bindingDirtyRows.Clear(); _automaticAttempts.Clear(); }
                        _bindingChangePending = _bindingDirtyRows.Count > 0;
                        return;
                    }
            }
        }
        catch (Exception error) when (!request.IsSuperseded)
        {
            lock (_intentGate)
            {
                if (CurrentExecution is { } execution) _bindingDirtyRows.ExceptWith(execution.Rows); else _bindingDirtyRows.Clear();
                _bindingChangePending = _bindingDirtyRows.Count > 0;
                _bindingSettlement?.TrySetException(error);
                _ = _bindingSettlement?.Task.Exception;
            }
            throw;
        }
    }

    private bool BindingReconciliationNeeded()
    {
        if (_live.Any(item => Touches(item.Row.Id) && !item.Context.IsClosing && HasStaleActivationView(item))) return true;
        foreach (var row in _rows)
        {
            if (!Touches(row.Id) || !_desiredRows.Contains(row.Id) || _live.Any(item => item.Row.Id == row.Id)) continue;
            lock (_info) if (_info[row.Id].State == PluginState.Failed) continue;
            if (NamespaceIsReady(row) && DefinitionFor(row) is { } descriptor && RequiredEntryRows(row, Metadata(descriptor)).All(EntryIsReady) &&
                PluginServiceDeclarations.Required(Metadata(descriptor)).All(type => _registry.IsRegistered(ScopeFor(row), type))) return true;
        }
        return false;
    }

    private bool HasStaleActivationView(LiveRow row) =>
        IsDefinitionSourceRevoked(row.Context) ||
        !NamespaceIsReady(row.Row) ||
        !ReferenceEquals(row.Context.NativeServices!.Scope, ScopeFor(row.Row)) ||
        !Volatile.Read(ref _configuration).EntryRevisions.TryGetValue(row.Row.Id, out var revision) || row.ConfigurationRevision != revision ||
        (row.Context.NativeServices!.Access is { } access && !access.MatchesCurrent());
}
