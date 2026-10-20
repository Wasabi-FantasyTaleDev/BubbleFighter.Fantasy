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
        DefinitionSource[] stopSources = [];
        TransitionRequest accepted;
        lock (_intentGate)
        {
            var row = KnownRow(rowId, allowRetiring: !enabled);
            // An enabled target must be activatable; otherwise the row would poison every later
            // configuration revision through NeedsConfigurationConvergence. Shutdown keeps the
            // superseded contract instead of failing loud.
            var descriptor = DefinitionFor(row);
            if (enabled && descriptor is null && !_shutdownInProgress)
                throw new ConfigException($"Row '{rowId}' references unknown plugin '{row.Plugin}'.");
            var request = new TransitionRequest(this, rowId, checked(++_intentRevision), enabled, restart);
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
            request.PreviousDesiredTarget = _desiredRows.Contains(rowId);
            request.OwnsDesiredTarget = true;
            if (enabled) _desiredRows.Add(rowId); else _desiredRows.Remove(rowId);
            if (_pendingIntents.Remove(rowId, out var previous)) previous.Supersede();
            var writes = descriptor is null ? [] : AdvertisedServices(descriptor).Concat(Metadata(descriptor).Decorates)
                .Select(ScopeFor(row).Resolve).ToHashSet();
            foreach (var execution in _runningExecutions)
                if ((execution.Request.Configuration && execution.Rows.Contains(rowId)) ||
                    ((!enabled || restart) && execution.EntryReads.Contains(rowId)) ||
                    execution.Request.RowId == rowId || execution.Context?.Row.Id == rowId ||
                    ((!enabled || restart) && execution.Context?.NativeServices?.Access is { } access && access.View.Selections.Values.Any(selection => writes.Contains(selection.Key))))
                {
                    execution.Context?.Supersede(); execution.Request.Supersede();
                    _bindingDirtyRows.UnionWith(execution.Rows); _bindingChangePending = true;
                    if (_bindingSettlement is null || _bindingSettlement.Task.IsCompleted)
                        _bindingSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            _pendingIntents[rowId] = request;
            ScheduleIntents();
            accepted = request;
        }
        // Source cancellation can invoke backend callbacks; never issue it while holding the intent gate.
        foreach (var source in stopSources) RequestDefinitionSourceCancellation(source);
        return accepted;
    }

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
            _pendingIntents.Remove(request.RowId); _bindingDirtyRows.Remove(request.RowId);
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
            _pendingIntents.Remove(request.RowId);
            request.Fail(error);
            return;
        }
        _pendingIntents.Remove(request.RowId);
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
        var row = KnownRow(request.RowId, allowRetiring: !request.Enabled);
        var descriptor = DefinitionFor(row) ?? throw new ConfigException($"Unknown plugin '{row.Plugin}'.");
        var live = _live.FirstOrDefault(item => item.Row.Id == row.Id);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (live is not null && (!request.Enabled || request.Restart || live.Context.IsClosing || live.Context.WorkFailure is not null))
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
        if (!request.Enabled && !_live.Any(item => item.Row.Id == row.Id))
            lock (_info) _info[row.Id] = InstanceInfo(row, descriptor, PluginState.Unloaded, null);
        var waiting = request.Enabled && !_live.Any(item => item.Row.Id == row.Id && !item.Context.IsClosing);
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
        var pending = _rows.Where(row => Touches(row.Id) && desired.Contains(row.Id) && !_live.Any(item => item.Row.Id == row.Id) &&
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
                var potential = _rows.Where(candidate => desired.Contains(candidate.Id) && !failed.Contains(candidate.Id) && !_live.Any(item => item.Row.Id == candidate.Id));
                if (!PluginServiceDeclarations.Required(declaration).All(type => _registry.IsRegistered(ScopeFor(row), type)) ||
                    PendingProviders(row, declaration, potential).Any() ||
                    RequiredEntryRows(row, declaration).Any(id => !EntryIsReady(id))) continue;
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
                changed.Add(row.Id);
                pending.Remove(row);
                progress = true;
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

    private sealed class TransitionRequest(PluginHost host, string rowId, long revision, bool enabled, bool restart, bool automatic = false, bool compatibility = false, IReadOnlySet<string>? roots = null, bool configuration = false) : IPluginTransition
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
        internal bool PreviousDesiredTarget;
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
