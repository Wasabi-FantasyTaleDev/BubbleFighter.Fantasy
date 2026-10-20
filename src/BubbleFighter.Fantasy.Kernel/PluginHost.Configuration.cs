using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private sealed class ConfigurationSnapshot(long revision, IReadOnlyList<PluginRow> rows, IReadOnlyDictionary<string, long> entryRevisions, PluginEntryTree tree, PluginCatalog catalog, IReadOnlyDictionary<string, DefinitionSource> sources)
    {
        internal PluginEntryTree Tree { get; } = tree;
        internal PluginCatalog Catalog { get; } = catalog;
        internal IReadOnlyDictionary<string, DefinitionSource> Sources { get; } = sources;
        internal long Revision { get; } = revision;
        internal IReadOnlyList<PluginRow> Rows { get; } = rows;
        internal IReadOnlyDictionary<string, long> EntryRevisions { get; } = entryRevisions;
        internal IReadOnlyDictionary<string, PluginRow> ById { get; } = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
    }

    private Task<PluginConfigurationResult>? _configurationTask;

    /// <inheritdoc />
    public async ValueTask<PluginConfigurationResult> ReconcileConfigurationAsync(IReadOnlyList<PluginRow> candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EffectScope.IsInsideCallback || (_transitionFrame.Value is { } frame && Volatile.Read(ref frame.Active)))
            throw new InvalidOperationException("A lifecycle or owned callback cannot wait for configuration reconciliation.");
        ArgumentNullException.ThrowIfNull(candidate);
        Task<PluginConfigurationResult> completion;
        bool addsSource;
        using (await EnterDefinitionMetadataMutationAsync(cancellationToken).ConfigureAwait(false))
        {
        var baseline = Volatile.Read(ref _configuration);
        var prepared = PrepareConfiguration(candidate, baseline, out var changed);
        var candidateIds = prepared.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var removed = baseline.Rows.Where(row => !candidateIds.Contains(row.Id)).ToArray();
        var added = prepared.Where(row => !baseline.ById.ContainsKey(row.Id)).ToArray();
        lock (_intentGate)
        {
            if (!ReferenceEquals(baseline, _configuration))
                throw new InvalidOperationException("Configuration changed while the candidate was being captured; submit it again.");
            _budget.Validate(prepared, _retiring.Values.Select(entry => entry.Row).Concat(removed).ToArray(), OwnedConfigurationRows());
        }
        RejectSourceOwnerNamespaceCycles(prepared, baseline.Catalog, baseline.Sources);
        var configured = new HashSet<string>(changed, StringComparer.Ordinal);
        var tree = PluginEntryTree.Build(prepared, _registry.Runtime, baseline.Catalog, baseline.Tree);
        foreach (var entry in tree.Entries.Values)
            if (!baseline.Tree.Entries.TryGetValue(entry.Id, out var previous) || !ReferenceEquals(entry.Scope, previous.Scope)) changed.Add(entry.Id);
        var watches = BuildBindingWatches(prepared, tree);
        lock (_intentGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_transitionBusy != 0 || _shutdownInProgress)
                throw new InvalidOperationException("Configuration cannot be admitted during a host-wide lifecycle transition.");
            if (!ReferenceEquals(baseline, _configuration))
                throw new InvalidOperationException("Configuration changed while the candidate was being captured; submit it again.");
            _budget.Validate(prepared, _retiring.Values.Select(entry => entry.Row).Concat(removed).ToArray(), OwnedConfigurationRows());
            // Scope-only changes keep their accepted binding; only edited rows are validated again.
            addsSource = AddsDefinitionSource(prepared, baseline);
            foreach (var row in prepared.Where(row => configured.Contains(row.Id))) ValidateDesiredDefinition(row, baseline, addsSource);
            if (_configurationTask is { IsCompleted: false } running)
            {
                if (changed.Count != 0)
                    throw new InvalidOperationException("Another configuration revision is still settling.");
                completion = running;
            }
            else
            {
                var roots = new HashSet<string>(changed, StringComparer.Ordinal);
                roots.UnionWith(_retiring.Keys);
                foreach (var row in baseline.Rows)
                    if (NeedsConfigurationConvergence(row.Id, baseline)) roots.Add(row.Id);
                if (roots.Count == 0)
                    return new(baseline.Revision, PluginTransitionOutcome.Converged, []);

                var request = new TransitionRequest(this, roots.Order(StringComparer.Ordinal).First(),
                    checked(_intentRevision + 1), true, true, roots: roots, configuration: true);
                var execution = DescribeExecution(request, prepared, tree);
                if (HasSourceOperationConflict(execution) || _runningExecutions.Any(execution.Conflicts) ||
                    _pendingIntents.Values.Any(pending => execution.Conflicts(DescribeExecution(pending))))
                    throw new InvalidOperationException("Configuration conflicts with a queued or running lifecycle transition.");

                var revision = changed.Count == 0 ? baseline.Revision : checked(baseline.Revision + 1);
                var versions = prepared.ToDictionary(row => row.Id, row => baseline.EntryRevisions.GetValueOrDefault(row.Id), StringComparer.Ordinal);
                foreach (var id in changed.Where(candidateIds.Contains)) versions[id] = checked(versions[id] + 1);
                var accepted = new ConfigurationSnapshot(revision, Array.AsReadOnly(prepared), versions, tree, baseline.Catalog, baseline.Sources);
                AdmitExecution(execution);
                _intentRevision = request.Revision;
                foreach (var row in removed)
                {
                    _retiring[row.Id] = new(row, baseline.Tree.Entries[row.Id]);
                    _desiredRows.Remove(row.Id);
                    _bindingDirtyRows.Remove(row.Id);
                }
                foreach (var row in added)
                {
                    if (!row.Disabled) _desiredRows.Add(row.Id);
                    lock (_info) _info[row.Id] = InstanceInfo(row, DefinitionFor(row), PluginState.Unloaded, null);
                }
                foreach (var row in prepared.Where(row => baseline.ById.TryGetValue(row.Id, out var old) && old.Disabled != row.Disabled))
                {
                    if (row.Disabled) _desiredRows.Remove(row.Id); else _desiredRows.Add(row.Id);
                }
                Volatile.Write(ref _configuration, accepted);
                _bindingReaders = watches;
                foreach (var id in execution.Rows) _automaticAttempts.Remove(id);
                if (ExecutionContext.IsFlowSuppressed()) completion = Task.Run(() => RunConfigurationAsync(execution, revision));
                else
                {
                    using (ExecutionContext.SuppressFlow())
                        completion = Task.Run(() => RunConfigurationAsync(execution, revision));
                }
                _configurationTask = completion;
                _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        }
        var result = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!addsSource || result.Outcome == PluginTransitionOutcome.Superseded) return result;
        // Rows bound to plugins that the new source rows published start in one follow-up pass, whose execution
        // reserves their now-known dependencies; the sources are in the baseline then, so this does not repeat.
        await WaitForBindingChangesAsync(cancellationToken).ConfigureAwait(false);
        var followUp = await ReconcileConfigurationAsync(candidate, cancellationToken).ConfigureAwait(false);
        return new(followUp.Revision, followUp.Outcome, result.ChangedRowIds.Union(followUp.ChangedRowIds, StringComparer.Ordinal).ToArray());
    }

    private PluginRow[] PrepareConfiguration(IReadOnlyList<PluginRow> candidate, ConfigurationSnapshot baseline, out HashSet<string> changed)
    {
        var count = candidate.Count;
        if (count < 0 || count > _budget.EntryCapacity)
            throw new ConfigException($"Candidate exceeds runtime entry capacity of {_budget.EntryCapacity}.");
        var captured = new Dictionary<string, PluginRow>(StringComparer.Ordinal);
        changed = new(StringComparer.Ordinal);
        var addsSource = AddsDefinitionSource(candidate, baseline);
        for (var index = 0; index < count; index++)
        {
            var row = candidate[index];
            if (row is null || string.IsNullOrWhiteSpace(row.Id))
                throw new ConfigException($"Configuration row at index {index} has an invalid identity.");
            if (captured.ContainsKey(row.Id))
                throw new ConfigException($"Configuration repeats row '{row.Id}'.");
            PluginRow copy;
            if (baseline.ById.TryGetValue(row.Id, out var previous))
            {
                copy = PluginRowSnapshot.Capture(row with { Source = previous.Source });
                if (EquivalentConfiguration(previous, copy)) copy = previous;
                else changed.Add(row.Id);
            }
            else
            {
                copy = PluginRowSnapshot.Capture(row);
                changed.Add(row.Id);
            }
            if (changed.Contains(row.Id)) ValidateDesiredDefinition(copy, baseline, addsSource);
            captured.Add(row.Id, copy);
        }
        foreach (var row in baseline.Rows)
            if (!captured.ContainsKey(row.Id)) changed.Add(row.Id);
        // A surviving declaration may override or require an entry the candidate removes; admitting
        // that pair would park the survivor in permanent Pending, so reject the whole candidate.
        foreach (var row in captured.Values)
        {
            var descriptor = DefinitionFor(row);
            if (descriptor is null) continue;
            var missing = RequiredEntryRows(row, Metadata(descriptor)).Where(id => !captured.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
                throw new ConfigException($"Row '{row.Id}' requires entry row(s) the candidate does not contain: {string.Join(", ", missing)}.");
        }
        return baseline.Rows.Where(row => captured.ContainsKey(row.Id)).Select(row => captured[row.Id])
            .Concat(captured.Values.Where(row => !baseline.ById.ContainsKey(row.Id))).ToArray();
    }

    // A row added with definition-source reserves publishes only once it activates in this revision.
    private static bool AddsDefinitionSource(IEnumerable<PluginRow?> rows, ConfigurationSnapshot baseline) =>
        rows.Any(row => row is { Disabled: false } && row.Lifecycle.DefinitionSourceReserve > 0 && !baseline.ById.ContainsKey(row.Id));

    private void ValidateDesiredDefinition(PluginRow row, ConfigurationSnapshot baseline, bool addsSource)
    {
        var descriptor = DefinitionFor(row);
        if (descriptor is null)
        {
            // An accepted binding whose definition source is withdrawn stays admissible and waits as Pending,
            // as does a new binding whose source row is added by the same candidate.
            if (addsSource || (baseline.ById.TryGetValue(row.Id, out var old) && old.Plugin == row.Plugin)) return;
            throw new ConfigException($"Unknown plugin '{row.Plugin}' for row '{row.Id}'.");
        }
        ValidateCompatibility(row, descriptor, Metadata(descriptor));
    }

    private static bool EquivalentConfiguration(PluginRow left, PluginRow right) =>
        left.Plugin == right.Plugin && left.Version == right.Version && left.Disabled == right.Disabled &&
        left.Lifecycle == right.Lifecycle && left.ClientBuild == right.ClientBuild &&
        left.ParentId == right.ParentId && left.Lineage.SequenceEqual(right.Lineage, StringComparer.Ordinal) &&
        left.IsolatedServices.SequenceEqual(right.IsolatedServices, StringComparer.Ordinal) &&
        PluginConfigurationEquality.Equivalent(left.Config, right.Config);

    private PluginRow[] OwnedConfigurationRows() => _live.Select(live => live.Row)
        .Concat(_runningExecutions.Select(execution => execution.Context?.Row).OfType<PluginRow>()).ToArray();

    private bool NeedsConfigurationConvergence(string rowId, ConfigurationSnapshot snapshot)
    {
        var live = _live.FirstOrDefault(item => item.Row.Id == rowId);
        if (!snapshot.ById.ContainsKey(rowId)) return live is not null;
        if (live is null) return _desiredRows.Contains(rowId);
        return !_desiredRows.Contains(rowId) || live.Context.IsClosing || live.Context.WorkFailure is not null ||
            live.ConfigurationRevision != snapshot.EntryRevisions[rowId] || HasStaleActivationView(live);
    }

    private async Task<PluginConfigurationResult> RunConfigurationAsync(IntentExecution execution, long revision)
    {
        await RunIntentAsync(execution).ConfigureAwait(false);
        var result = await execution.Request.Completion.ConfigureAwait(false);
        return new(revision, result.Outcome, result.ChangedRowIds);
    }

    private async ValueTask<PluginTransitionResult> ApplyConfigurationAsync(TransitionRequest request)
    {
        // Withdraw removed providers before child cleanup; existing dependency leases stay held.
        foreach (var live in _live.Where(item => Touches(item.Row.Id) && _retiring.ContainsKey(item.Row.Id)))
            _ = _registry.RetireOwner(live.Context.NativeServices!.Activation);
        var affected = new HashSet<string>(request.Roots, StringComparer.Ordinal);
        foreach (var root in request.Roots)
            foreach (var dependent in DependentsClosure(root)) affected.Add(dependent.Row.Id);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var live in _live.Where(item => affected.Contains(item.Row.Id)).Reverse().ToArray())
        {
            request.Token.ThrowIfCancellationRequested();
            await UnloadCoreAsync(live.Row.Id, request.Token).ConfigureAwait(false);
            changed.Add(live.Row.Id);
        }
        await StartDesiredRowsAsync(request, changed).ConfigureAwait(false);
        await ReconcileBindingsAsync(request, changed).ConfigureAwait(false);
        lock (_intentGate)
        {
            // Retained activations keep their actual descriptor visible until ownership drains.
            foreach (var row in _rows.Where(row => CurrentExecution!.Rows.Contains(row.Id)))
            {
                if (_live.Any(live => live.Row.Id == row.Id) ||
                    _runningExecutions.Any(execution => execution.Context?.Row.Id == row.Id)) continue;
                lock (_info)
                {
                    var previous = _info[row.Id];
                    _info[row.Id] = InstanceInfo(row, DefinitionFor(row), previous.State, previous.Error);
                }
            }
            var waiting = CurrentExecution!.Rows.Any(id => NeedsConfigurationConvergence(id, _configuration));
            return new(waiting ? PluginTransitionOutcome.WaitingForDependencies : PluginTransitionOutcome.Converged,
                Array.AsReadOnly(changed.ToArray()));
        }
    }

    private void FinalizeRetiredEntries()
    {
        lock (_intentGate)
            foreach (var id in _retiring.Keys.ToArray())
            {
                if (_live.Any(live => live.Row.Id == id) || _pendingIntents.ContainsKey(id) ||
                    _runningExecutions.Any(execution => execution.Rows.Contains(id) || execution.EntryReads.Contains(id))) continue;
                _retiring.TryRemove(id, out _);
                if (_entryDefinitions.TryRemove(id, out var definition)) definition.Lease.Dispose();
                _desiredRows.Remove(id);
                _bindingDirtyRows.Remove(id);
                _automaticAttempts.Remove(id);
                lock (_info) { _info.Remove(id); _activationGenerations.Remove(id); }
            }
    }
}
