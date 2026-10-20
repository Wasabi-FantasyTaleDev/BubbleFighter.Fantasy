using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private void RequireCodeRefreshAllowed(PluginRow row)
    {
        var current = Volatile.Read(ref _configuration).ById.GetValueOrDefault(row.Id) ?? row;
        if (current.Lifecycle.CodeRefreshPolicy == PluginCodeRefreshPolicy.RestartRequired)
            throw new InvalidOperationException($"Row '{row.Id}' requires a process restart for code refresh (lifecycle.code_refresh_policy=restart_required).");
    }

    private void ValidateCodeRefreshPlan(TransitionRequest request, IntentExecution? execution = null, bool includeRequestedOperation = true)
    {
        if (request.Configuration || request.Automatic || request.IsSuperseded) return;
        lock (_intentGate)
        {
            var snapshot = _configuration;
            if (!snapshot.Rows.Any(row => row.Lifecycle.CodeRefreshPolicy == PluginCodeRefreshPolicy.RestartRequired)) return;
            execution ??= DescribeExecution(request);
            var live = _live.ToArray();
            var desired = _desiredRows.ToHashSet(StringComparer.Ordinal);
            if (request.Enabled) desired.Add(request.RowId); else desired.Remove(request.RowId);
            var failed = new HashSet<string>(StringComparer.Ordinal);
            lock (_info)
                foreach (var info in _info.Values)
                    if (info.State == PluginState.Failed && !request.Roots.Contains(info.RowId)) failed.Add(info.RowId);
            var affected = execution.Rows.ToHashSet(StringComparer.Ordinal);
            var removing = new HashSet<string>(StringComparer.Ordinal);
            var refresh = new HashSet<string>(StringComparer.Ordinal);
            var changing = new HashSet<ServiceKey>();
            var bindings = _registry.CaptureBindings();

            void RemoveWithDependents(string id)
            {
                removing.Add(id);
                affected.Add(id);
                var progress = true;
                while (progress)
                {
                    progress = false;
                    foreach (var consumer in live.Where(item => !removing.Contains(item.Row.Id)))
                        if (live.Any(provider => removing.Contains(provider.Row.Id) && consumer.DependsOn(provider)))
                        { removing.Add(consumer.Row.Id); affected.Add(consumer.Row.Id); progress = true; }
                }
            }

            if (includeRequestedOperation)
            {
                var root = live.FirstOrDefault(item => item.Row.Id == request.RowId);
                if (root is not null && (!request.Enabled || request.Restart || root.Context.IsClosing || root.Context.WorkFailure is not null))
                    RemoveWithDependents(root.Row.Id);
                if (request.Compatibility && request.Enabled && (request.Restart || root is null))
                {
                    refresh.Add(request.RowId);
                    if (request.Restart) refresh.UnionWith(removing);
                }
            }
            foreach (var item in live.Where(item => affected.Contains(item.Row.Id) && !item.Context.IsClosing && HasStaleActivationView(item)))
                RemoveWithDependents(item.Row.Id);

            // Treat conditional publications and optional-reader changes as prospective writes.
            // Only desired rows with reachable entry/service prerequisites can require a refresh.
            var expanded = true;
            while (expanded)
            {
                expanded = false;
                foreach (var item in live.Where(item => removing.Contains(item.Row.Id)))
                    changing.UnionWith(AdvertisedServices(item.Descriptor).Concat(Metadata(item.Descriptor).Decorates)
                        .Select(item.Context.NativeServices!.Scope.Resolve));
                var removedOwners = live.Where(item => removing.Contains(item.Row.Id))
                    .Select(item => item.Context.NativeServices!.Activation).ToHashSet();
                var ready = live.Where(item => !removing.Contains(item.Row.Id) && EntryIsReady(item.Row.Id))
                    .Select(item => item.Row.Id).ToHashSet(StringComparer.Ordinal);
                var supplied = new HashSet<ServiceKey>();
                var candidates = snapshot.Rows.Where(row => affected.Contains(row.Id) && desired.Contains(row.Id) &&
                    !failed.Contains(row.Id) && (row.Id == request.RowId || !_pendingIntents.ContainsKey(row.Id)) &&
                    (!live.Any(item => item.Row.Id == row.Id) || removing.Contains(row.Id))).ToList();
                var progress = true;
                while (progress)
                {
                    progress = false;
                    foreach (var row in candidates.ToArray())
                    {
                        if (DefinitionFor(row) is not { } descriptor) continue;
                        var declaration = Metadata(descriptor);
                        var scope = ScopeFor(row);
                        if (!RequiredEntryRows(row, declaration).All(ready.Contains) ||
                            !PluginServiceDeclarations.Required(declaration).Select(scope.Resolve).All(key => supplied.Contains(key) ||
                                bindings.Resolve(key).Any(binding => !removedOwners.Contains(binding.Registration.Provider)))) continue;
                        candidates.Remove(row);
                        ready.Add(row.Id);
                        refresh.Add(row.Id);
                        var publications = AdvertisedServices(descriptor).Select(scope.Resolve).ToArray();
                        supplied.UnionWith(publications);
                        changing.UnionWith(publications);
                        changing.UnionWith(declaration.Decorates.Select(scope.Resolve));
                        progress = true;
                    }
                }
                foreach (var consumer in live.Where(item => affected.Contains(item.Row.Id) && !removing.Contains(item.Row.Id) && !item.Context.IsClosing))
                    if (PluginServiceDeclarations.Reads(Metadata(consumer.Descriptor))
                        .Select(consumer.Context.NativeServices!.Scope.Resolve).Any(changing.Contains))
                    { RemoveWithDependents(consumer.Row.Id); expanded = true; }
            }
            foreach (var id in refresh.Order(StringComparer.Ordinal))
                if (snapshot.ById.TryGetValue(id, out var row)) RequireCodeRefreshAllowed(row);
        }
    }

    private void ValidateCodeRefreshBeforeExecution(IntentExecution execution)
    {
        try { ValidateCodeRefreshPlan(execution.Request, execution); }
        catch
        {
            lock (_intentGate) RestoreRejectedDesiredTarget(execution.Request);
            throw;
        }
    }

    private void RestoreRejectedDesiredTarget(TransitionRequest request)
    {
        if (!request.OwnsDesiredTarget || request.IsSuperseded || _shutdownInProgress) return;
        if ((_pendingIntents.TryGetValue(request.RowId, out var pending) && pending.Revision > request.Revision) ||
            _runningExecutions.Any(execution => execution.Request.RowId == request.RowId && execution.Request.Revision > request.Revision)) return;
        // A newer same-entry execution cannot finish while this queued/running owner still conflicts.
        if (request.PreviousDesiredTarget) _desiredRows.Add(request.RowId); else _desiredRows.Remove(request.RowId);
        request.OwnsDesiredTarget = false;
    }
}
