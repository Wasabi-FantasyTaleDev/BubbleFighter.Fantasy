using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private readonly object _intentGate = new();
    private readonly Dictionary<string, TransitionRequest> _pendingIntents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _desiredRows = new(StringComparer.Ordinal);
    private readonly AsyncLocal<TransitionFrame?> _transitionFrame = new();
    private long _intentRevision;
    private bool _intentWorkerScheduled;
    private bool _shutdownInProgress;
    internal int PendingIntentCount { get { lock (_intentGate) return _pendingIntents.Count; } }

    /// <inheritdoc />
    public IPluginTransition RequestState(string rowId, bool enabled, bool restart = false)
    {
        if (restart && !enabled) throw new ArgumentException("Restart requires an enabled target.", nameof(restart));
        return RequestTargets(new HashSet<string>(StringComparer.Ordinal) { rowId }, enabled, restart);
    }

    /// <inheritdoc />
    public IPluginTransition RequestStates(IReadOnlyCollection<string> rowIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        var roots = rowIds.ToHashSet(StringComparer.Ordinal);
        if (roots.Count == 0) throw new ArgumentException("At least one row is required.", nameof(rowIds));
        return RequestTargets(roots, enabled, restart: false);
    }

    // One row is the case every caller had before groups; a set is one intent whose rows start in provider order and
    // stop in reverse, with no reconciliation between its members.
    private TransitionRequest RequestTargets(HashSet<string> roots, bool enabled, bool restart)
    {
        DefinitionSource[] stopSources = [];
        TransitionRequest accepted;
        lock (_intentGate)
        {
            var rows = roots.Order(StringComparer.Ordinal).Select(id => KnownRow(id, allowRetiring: !enabled)).ToArray();
            // An enabled target must be activatable; otherwise the row would poison every later
            // configuration revision through NeedsConfigurationConvergence. Shutdown keeps the
            // superseded contract instead of failing loud.
            var descriptors = rows.Select(DefinitionFor).ToArray();
            for (var index = 0; index < rows.Length; index++)
                if (enabled && descriptors[index] is null && !_shutdownInProgress)
                    throw new ConfigException($"Row '{rows[index].Id}' references unknown plugin '{rows[index].Plugin}'.");
            var revision = checked(++_intentRevision);
            var request = rows.Length == 1
                ? new TransitionRequest(this, rows[0].Id, revision, enabled, restart)
                : new TransitionRequest(this, rows[0].Id, revision, enabled, restart, roots: roots, key: GroupIntentKey(revision));
            if (_shutdownInProgress) { request.Supersede(); return request; }
            try { ValidateCodeRefreshPlan(request); }
            catch (Exception error) { request.Fail(error); return request; }
            if (!enabled)
            {
                try
                {
                    var stoppingRows = DescribeExecution(request).Rows;
                    stopSources = _ownedDefinitionSources.Where(source => source.State == PluginDefinitionSourceState.Preparing &&
                        stoppingRows.Contains(source.Owner.Row.Id)).ToArray();
                }
                catch (Exception error) { request.Fail(error); return request; }
            }
            request.PreviousDesiredTargets = roots.Where(_desiredRows.Contains).ToHashSet(StringComparer.Ordinal);
            request.OwnsDesiredTarget = true;
            if (enabled) _desiredRows.UnionWith(roots); else _desiredRows.ExceptWith(roots);
            // A queued single-row request for one of these rows states a target this request has just replaced.
            foreach (var rowId in roots)
                if (_pendingIntents.Remove(rowId, out var previous)) previous.Supersede();
            var writes = new HashSet<ServiceKey>();
            for (var index = 0; index < rows.Length; index++)
                if (descriptors[index] is { } descriptor)
                    writes.UnionWith(AdvertisedServices(descriptor).Concat(Metadata(descriptor).Decorates).Select(ScopeFor(rows[index]).Resolve));
            foreach (var execution in _runningExecutions)
                if ((execution.Request.Configuration && execution.Rows.Overlaps(roots)) ||
                    ((!enabled || restart) && execution.EntryReads.Overlaps(roots)) ||
                    roots.Contains(execution.Request.RowId) || (execution.Context is { } running && roots.Contains(running.Row.Id)) ||
                    ((!enabled || restart) && execution.Context?.NativeServices?.Access is { } access && access.View.Selections.Values.Any(selection => writes.Contains(selection.Key))))
                {
                    execution.Context?.Supersede(); execution.Request.Supersede();
                    _bindingDirtyRows.UnionWith(execution.Rows); _bindingChangePending = true;
                    if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                        _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            _pendingIntents[request.Key] = request;
            ScheduleIntents();
            accepted = request;
        }
        // Source cancellation can invoke backend callbacks; never issue it while holding the intent gate.
        foreach (var source in stopSources) RequestDefinitionSourceCancellation(source);
        return accepted;
    }

    // A group intent has no single row to be queued under; a row identity never starts with a NUL.
    private static string GroupIntentKey(long revision) => "\0group:" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ScheduleIntents()
    {
        if (_intentWorkerScheduled || _transitionBusy != 0 || (_pendingIntents.Count == 0 && !_bindingChangePending)) return;
        _intentWorkerScheduled = true;
        if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(DrainIntentsAsync);
        else { using (ExecutionContext.SuppressFlow()) _ = Task.Run(DrainIntentsAsync); }
    }

    private Task DrainIntentsAsync()
    {
        var launch = new List<IntentExecution>();
        var waiting = new List<IntentExecution>();
        lock (_intentGate)
        {
            if (_transitionBusy == 0)
            {
                foreach (var request in _pendingIntents.Values.OrderBy(item => item.Revision).ToArray())
                    TrySchedule(request, launch, waiting);
                foreach (var rowId in _bindingDirtyRows.ToArray())
                    if (!_configuration.ById.ContainsKey(rowId) ||
                        (!_desiredRows.Contains(rowId) && !_live.Any(item => item.Row.Id == rowId) && !_pendingIntents.ContainsKey(rowId)))
                        _bindingDirtyRows.Remove(rowId);
                    else if (!_pendingIntents.ContainsKey(rowId) && !_runningExecutions.Any(item => item.Rows.Contains(rowId)))
                        TrySchedule(new(this, rowId, checked(++_intentRevision), _desiredRows.Contains(rowId), false, true), launch, waiting);
            }
            _intentWorkerScheduled = false;
            CompleteBindingSettlement();
        }
        foreach (var execution in launch)
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(() => RunIntentAsync(execution));
            else { using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => RunIntentAsync(execution)); }
        return Task.CompletedTask;
    }

    private void TrySchedule(TransitionRequest request, List<IntentExecution> launch, List<IntentExecution> waiting)
    {
        IntentExecution execution;
        try { execution = DescribeExecution(request); }
        catch (Exception error)
        {
            _pendingIntents.Remove(request.Key); _bindingDirtyRows.Remove(request.RowId);
            request.Fail(error);
            lock (_info) _info[request.RowId] = InstanceInfo(KnownRow(request.RowId, allowRetiring: true), null, PluginState.Failed, error.Message);
            return;
        }
        if (HasSourceOperationConflict(execution) || _runningExecutions.Any(execution.Conflicts) || waiting.Any(execution.Conflicts))
        {
            // Preserve a waiting provider change ahead of new consumers of its old generation.
            waiting.Add(execution);
            return;
        }
        try
        {
            ValidateCodeRefreshPlan(request, execution);
            AdmitExecution(execution);
        }
        catch (Exception error)
        {
            RestoreRejectedDesiredTarget(request);
            _pendingIntents.Remove(request.Key);
            request.Fail(error);
            return;
        }
        _pendingIntents.Remove(request.Key);
        if (!request.Automatic) foreach (var rowId in execution.Rows) _automaticAttempts.Remove(rowId);
        launch.Add(execution);
    }

    private async Task RunIntentAsync(IntentExecution execution)
    {
        _execution.Value = execution; _transitionFrame.Value = new();
        var request = execution.Request; PluginTransitionResult? result = null; Exception? failure = null;
        try
        {
            ValidateCodeRefreshBeforeExecution(execution);
            if (request.Configuration) result = await ApplyConfigurationAsync(request).ConfigureAwait(false);
            else if (request.Automatic)
            {
                var changed = new HashSet<string>(StringComparer.Ordinal);
                await ReconcileBindingsAsync(request, changed).ConfigureAwait(false);
                result = new(PluginTransitionOutcome.Converged, Array.AsReadOnly(changed.ToArray()));
            }
            else result = await ApplyIntentAsync(request).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await ReleaseExecutionAsync(execution).ConfigureAwait(false); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        _transitionFrame.Value = null; _execution.Value = null;
        if (failure is not null)
        {
            if (!request.IsSuperseded || failure is not OperationCanceledException)
                try { _logger.LogError(failure, "Lifecycle revision {Revision} for '{RowId}' did not converge.", request.Revision, request.RowId); }
                catch (Exception loggingFailure) { failure = new AggregateException(failure, loggingFailure); }
            request.Fail(failure);
        }
        else request.Complete(result!);
    }

    private async ValueTask ReleaseExecutionAsync(IntentExecution execution)
    {
        lock (_intentGate) execution.Context = null;
        try { await execution.Request.ReleaseAsync().ConfigureAwait(false); }
        finally
        {
            if (_transitionFrame.Value is { } frame) Volatile.Write(ref frame.Active, false);
            try
            {
                lock (_intentGate)
                {
                    if (!BindingReconciliationNeeded())
                    {
                        _bindingDirtyRows.ExceptWith(execution.Rows);
                        foreach (var rowId in execution.Rows) _automaticAttempts.Remove(rowId);
                    }
                    Volatile.Write(ref execution.Active, false);
                    _runningExecutions.Remove(execution);
                    try
                    {
                        _modules.RemoveScheduledLeases(execution.Modules);
                        // Keep the error visible to the caller without stranding shutdown observers.
                        if (_runningExecutions.Count == 0 && _modules.HasScheduledLeases())
                            throw new InvalidOperationException("Module ledger retains scheduled leases after every execution settled; an admission site skipped its release.");
                    }
                    finally { CompleteBindingSettlement(); ScheduleIntents(); }
                }
            }
            finally
            {
                execution.Settled.TrySetResult();
                FinalizeRetiredEntries();
            }
        }
    }

    private async ValueTask<PluginTransitionResult> ApplyIntentAsync(TransitionRequest request)
    {
        var roots = request.Roots.Order(StringComparer.Ordinal).Select(id => KnownRow(id, allowRetiring: !request.Enabled)).ToArray();
        var descriptors = roots.Select(row => DefinitionFor(row) ?? throw new ConfigException($"Unknown plugin '{row.Plugin}'.")).ToArray();
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in roots)
            if (_live.FirstOrDefault(item => item.Row.Id == row.Id) is { } live &&
                (!request.Enabled || request.Restart || live.Context.IsClosing || live.Context.WorkFailure is not null))
            {
                affected.Add(row.Id);
                foreach (var dependent in DependentsClosure(row.Id)) affected.Add(dependent.Row.Id);
            }
        var changed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var item in _live.Where(item => affected.Contains(item.Row.Id)).Reverse().ToArray())
            {
                request.Token.ThrowIfCancellationRequested();
                await UnloadCoreAsync(item.Row.Id, request.Token).ConfigureAwait(false);
                changed.Add(item.Row.Id);
            }
            await StartDesiredRowsAsync(request, changed).ConfigureAwait(false);
        }
        catch (Exception failure) when (!request.IsSuperseded)
        {
            try { await StartDesiredRowsAsync(request, changed, recovering: true).ConfigureAwait(false); }
            catch (Exception recovery) { throw new AggregateException("Target change and dependent recovery both failed.", failure, recovery); }
            throw;
        }
        await ReconcileBindingsAsync(request, changed).ConfigureAwait(false);
        if (!request.Enabled)
            for (var index = 0; index < roots.Length; index++)
                if (!_live.Any(item => item.Row.Id == roots[index].Id))
                    lock (_info) _info[roots[index].Id] = InstanceInfo(roots[index], descriptors[index], PluginState.Unloaded, null);
        var waiting = request.Enabled && roots.Any(row => !_live.Any(item => item.Row.Id == row.Id && !item.Context.IsClosing));
        return new(waiting ? PluginTransitionOutcome.WaitingForDependencies : PluginTransitionOutcome.Converged,
            Array.AsReadOnly(changed.ToArray()));
    }

    private async ValueTask StartDesiredRowsAsync(TransitionRequest request, HashSet<string> changed, bool recovering = false, Dictionary<string, int>? automaticAttempts = null)
    {
        if (CurrentExecution is { } execution) ValidateCodeRefreshPlan(request, execution, includeRequestedOperation: false);
        HashSet<string> desired;
        lock (_intentGate) desired = _desiredRows.Where(id => !_pendingIntents.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        HashSet<string> failed;
        lock (_info) failed = _info.Values.Where(info => info.State == PluginState.Failed).Select(info => info.RowId).ToHashSet(StringComparer.Ordinal);
        // Live row ids as a set: checking each candidate against the live list made a many-build start cubic in rows.
        // Refreshed after every activation, which may itself change what is live.
        var live = LiveRowIds();
        var pending = _rows.Where(row => Touches(row.Id) && desired.Contains(row.Id) && !live.Contains(row.Id) &&
            (!failed.Contains(row.Id) || (!recovering && !request.Automatic && automaticAttempts is null && request.Roots.Contains(row.Id)))).ToList();
        foreach (var row in pending.ToArray())
            if (DefinitionFor(row) is null)
            {
                // Only an explicit start of this row fails; convergence leaves it Pending until its source returns.
                if (!request.Automatic && !request.Configuration && request.Roots.Contains(row.Id))
                    throw new ConfigException($"Unknown plugin '{row.Plugin}'.");
                lock (_info) _info[row.Id] = InstanceInfo(row, null, PluginState.Pending, $"Definition source for plugin '{row.Plugin}' is unavailable.");
                pending.Remove(row);
            }
            else if (request.Configuration && !IsReservedDescriptor(row, DefinitionFor(row)!))
            {
                // Published by a source this revision started; the configuration call starts it in a follow-up revision.
                lock (_info) _info[row.Id] = InstanceInfo(row, DefinitionFor(row), PluginState.Pending, $"Plugin '{row.Plugin}' was published during this configuration revision.");
                pending.Remove(row);
            }
        // A set of rows requested together starts the way a server does: providers before the rows that read them,
        // including a row that is already live and reads what a member is about to provide.
        var together = request.Roots.Count > 1 && !request.Configuration && !request.Automatic && !recovering;
        Dictionary<ServiceKey, HashSet<string>>? awaiting = null;
        var progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            foreach (var row in pending.ToArray())
            {
                var token = recovering ? CancellationToken.None : request.Token;
                token.ThrowIfCancellationRequested();
                if (!NamespaceIsReady(row)) continue;
                var descriptor = DefinitionFor(row)!;
                var declaration = Metadata(descriptor);
                var potential = _rows.Where(candidate => desired.Contains(candidate.Id) && !failed.Contains(candidate.Id) && !live.Contains(candidate.Id));
                if (!PluginServiceDeclarations.Required(declaration).All(type => _registry.IsRegistered(ScopeFor(row), type)) ||
                    PendingProviders(row, declaration, potential).Any() ||
                    RequiredEntryRows(row, declaration).Any(id => !EntryIsReady(id))) continue;
                if (together)
                {
                    awaiting ??= ServicesAwaitingProviders(potential.ToArray());
                    if (PluginServiceDeclarations.Required(declaration).Select(ScopeFor(row).Resolve)
                        .Any(key => awaiting.TryGetValue(key, out var providers) && providers.Any(id => id != row.Id))) continue;
                }
                if (automaticAttempts is not null)
                {
                    lock (_intentGate)
                    {
                    var count = automaticAttempts.GetValueOrDefault(row.Id);
                    if (count >= row.Lifecycle.ReconcileAttemptLimit)
                    {
                        var error = $"Automatic activation attempt limit {row.Lifecycle.ReconcileAttemptLimit} reached for row '{row.Id}'.";
                        lock (_info) _info[row.Id] = InstanceInfo(row, descriptor, PluginState.Failed, error);
                        throw new InvalidOperationException(error);
                    }
                    automaticAttempts[row.Id] = count + 1;
                    }
                }
                await ReactivateAsync(row, descriptor, "converge", token, refreshBackend: !request.Configuration && !request.Automatic).ConfigureAwait(false);
                live = LiveRowIds();
                changed.Add(row.Id);
                pending.Remove(row);
                progress = true;
                if (together)
                {
                    awaiting = null;
                    pending.AddRange(await SuspendStaleRowsAsync(request, changed, desired, failed, pending).ConfigureAwait(false));
                    // The rows just stopped are no longer live and are providers the rest must wait for again.
                    live = LiveRowIds();
                }
            }
        }
        foreach (var row in pending)
            {
                var descriptor = DefinitionFor(row);
                if (descriptor is null)
                {
                    lock (_info) _info[row.Id] = InstanceInfo(row, null, PluginState.Pending, $"Definition source for plugin '{row.Plugin}' is unavailable.");
                    continue;
                }
                var declaration = Metadata(descriptor);
                var missing = PluginServiceDeclarations.Required(declaration).Where(type => !_registry.IsRegistered(ScopeFor(row), type));
                var reason = NamespaceUnavailableReason(row) ??
                    $"Waiting for services: {string.Join(", ", missing.Select(type => type.FullName))}; provider rows: {string.Join(", ", PendingProviders(row, declaration, pending))}; override rows: {string.Join(", ", RequiredEntryRows(row, declaration).Where(id => !EntryIsReady(id)))}.";
                lock (_info) _info[row.Id] = InstanceInfo(row, descriptor, PluginState.Pending, reason);
            }
    }

    // A row that has just started may be one a live row reads: that reader's view is now out of date, and a later
    // member that requires the reader would activate against what it no longer provides correctly. Server start never
    // meets this because a reader waits for every pending provider; a set of rows started later does, so the stale
    // readers and their dependents are stopped here and started again in order with the rest of the set.
    private async ValueTask<IReadOnlyList<PluginRow>> SuspendStaleRowsAsync(TransitionRequest request, HashSet<string> changed,
        HashSet<string> desired, HashSet<string> failed, List<PluginRow> pending)
    {
        var stale = _live.Where(item => Touches(item.Row.Id) && !item.Context.IsClosing && HasStaleActivationView(item)).ToArray();
        if (stale.Length == 0) return [];
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in stale)
        {
            affected.Add(item.Row.Id);
            foreach (var dependent in DependentsClosure(item.Row.Id)) affected.Add(dependent.Row.Id);
        }
        var stopped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _live.Where(item => Touches(item.Row.Id) && affected.Contains(item.Row.Id)).Reverse().ToArray())
        {
            request.Token.ThrowIfCancellationRequested();
            await UnloadCoreAsync(item.Row.Id, request.Token).ConfigureAwait(false);
            changed.Add(item.Row.Id);
            stopped.Add(item.Row.Id);
        }
        return _rows.Where(row => stopped.Contains(row.Id) && desired.Contains(row.Id) && !failed.Contains(row.Id) &&
            !pending.Any(waiting => waiting.Id == row.Id) && !_live.Any(item => item.Row.Id == row.Id)).ToArray();
    }

    // The services of live rows that will be restarted once a pending row starts (the row reads what that pending row
    // provides), and of everything that depends on them, each with the pending rows it waits for.
    private Dictionary<ServiceKey, HashSet<string>> ServicesAwaitingProviders(IReadOnlyList<PluginRow> potential)
    {
        var result = new Dictionary<ServiceKey, HashSet<string>>();
        foreach (var live in _live.Where(item => Touches(item.Row.Id) && !item.Context.IsClosing).ToArray())
        {
            var providers = PendingProviders(live.Row, Metadata(live.Descriptor), potential).ToArray();
            if (providers.Length == 0) continue;
            foreach (var holder in DependentsClosure(live.Row.Id).Prepend(live))
                foreach (var key in AdvertisedServices(holder.Descriptor).Select(holder.Context.NativeServices!.Scope.Resolve))
                {
                    if (!result.TryGetValue(key, out var waits)) result[key] = waits = new(StringComparer.Ordinal);
                    waits.UnionWith(providers);
                }
        }
        return result;
    }

    private HashSet<string> LiveRowIds() => _live.Select(item => item.Row.Id).ToHashSet(StringComparer.Ordinal);

    private void ExitTransition()
    {
        if (_transitionFrame.Value is { } frame) Volatile.Write(ref frame.Active, false);
        _transitionFrame.Value = null;
        lock (_intentGate)
        {
            _transitionBusy = 0;
            if (_bindingChangePending && !BindingReconciliationNeeded())
            { _bindingChangePending = false; _bindingDirtyRows.Clear(); _automaticAttempts.Clear(); }
            CompleteBindingSettlement();
            ScheduleIntents();
        }
    }

    private void CompleteBindingSettlement()
    {
        _bindingChangePending = _bindingDirtyRows.Count > 0;
        if (!_bindingChangePending) _bindingSettlement?.TrySetResult();
    }

    private void NoteLegacyTarget(string rowId, bool enabled)
    {
        lock (_intentGate)
            if (!_pendingIntents.ContainsKey(rowId))
            { if (enabled) _desiredRows.Add(rowId); else _desiredRows.Remove(rowId); }
    }

    private void BeginShutdownTargets()
    {
        lock (_intentGate)
        {
            _shutdownInProgress = true;
            _desiredRows.Clear();
            foreach (var request in _pendingIntents.Values) request.Supersede();
            _pendingIntents.Clear();
        }
    }

    private sealed class TransitionFrame { internal bool Active = true; }

    private sealed class TransitionRequest(PluginHost host, string rowId, long revision, bool enabled, bool restart, bool automatic = false, bool compatibility = false, IReadOnlySet<string>? roots = null, bool configuration = false, string? key = null) : IPluginTransition
    {
        private readonly TaskCompletionSource<PluginTransitionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _cancellation;
        private CancellationTokenRegistration _callerCancellation;
        private Task _cancelled = Task.CompletedTask;
        private int _superseded;
        public string RowId { get; } = rowId;
        public long Revision { get; } = revision;
        public bool IsCompleted => _completion.Task.IsCompleted;
        internal bool Enabled { get; } = enabled;
        internal bool Restart { get; } = restart;
        internal bool Automatic { get; } = automatic;
        internal bool Compatibility { get; } = compatibility;
        internal IReadOnlySet<string> Roots { get; } = roots ?? new HashSet<string>(StringComparer.Ordinal) { rowId };
        internal bool Configuration { get; } = configuration;
        /// <summary>The identity this request is queued under: its row, or a private one for a set of rows.</summary>
        internal string Key { get; } = key ?? rowId;
        internal IReadOnlySet<string> PreviousDesiredTargets = new HashSet<string>(StringComparer.Ordinal);
        internal bool OwnsDesiredTarget;
        internal Task<PluginTransitionResult> Completion => _completion.Task;
        internal bool IsSuperseded => Volatile.Read(ref _superseded) != 0;
        internal CancellationToken Token => _cancellation!.Token;
        internal void Begin(CancellationToken ct = default)
        {
            _cancellation = new();
            try { _callerCancellation = ct.UnsafeRegister(static state => ((TransitionRequest)state!).CancelExecution(), this); }
            catch
            {
                _cancellation.Dispose();
                _cancellation = null;
                throw;
            }
        }
        private void CancelExecution()
        {
            lock (host._intentGate)
                if (_cancellation is { IsCancellationRequested: false } cancellation)
                    _cancelled = cancellation.CancelAsync();
        }
        internal void Supersede()
        {
            if (Interlocked.Exchange(ref _superseded, 1) != 0) return;
            _completion.TrySetResult(new(PluginTransitionOutcome.Superseded, []));
            CancelExecution();
        }
        internal void Complete(PluginTransitionResult result) => _completion.TrySetResult(result);
        internal void Fail(Exception error)
        {
            _completion.TrySetException(error);
            _ = _completion.Task.Exception;
        }
        internal async Task ReleaseAsync()
        {
            await _callerCancellation.DisposeAsync().ConfigureAwait(false);
            CancellationTokenSource? cancellation;
            Task cancelled;
            lock (host._intentGate)
            {
                cancellation = _cancellation;
                _cancellation = null;
                cancelled = _cancelled;
            }
            try { await cancelled.ConfigureAwait(false); }
            finally { cancellation?.Dispose(); }
        }
        public ValueTask<PluginTransitionResult> WaitAsync(CancellationToken cancellationToken = default)
        {
            if ((host._transitionFrame.Value is { } frame && Volatile.Read(ref frame.Active)) || EffectScope.IsInsideCallback)
                throw new InvalidOperationException("A lifecycle or owned callback may submit a target but cannot wait for lifecycle convergence.");
            return new(_completion.Task.WaitAsync(cancellationToken));
        }
    }
}
