using System.Runtime.ExceptionServices;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private IntentExecution AdmitCompatibility(string rowId, bool enabled, bool restart, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_transitionFrame.Value is { } current && Volatile.Read(ref current.Active))
            throw new InvalidOperationException("A lifecycle callback may submit a target but cannot enter an immediate lifecycle operation.");
        lock (_intentGate)
        {
            _ = KnownRow(rowId, allowRetiring: !enabled);
            if (_transitionBusy != 0 || _shutdownInProgress)
                throw new InvalidOperationException("A host-wide lifecycle transition is already in progress; use RequestState for deferred targets.");
            var request = new TransitionRequest(this, rowId, checked(++_intentRevision), enabled, restart, compatibility: true);
            var execution = DescribeExecution(request);
            if (HasSourceOperationConflict(execution) || _runningExecutions.Any(execution.Conflicts) ||
                _pendingIntents.Values.Any(pending => execution.Conflicts(DescribeExecution(pending))))
                throw new InvalidOperationException("A conflicting lifecycle transition is already in progress; use RequestState for deferred targets.");
            ValidateCodeRefreshPlan(request, execution);
            AdmitExecution(execution, ct);
            return execution;
        }
    }

    private async ValueTask<T> RunCompatibilityAsync<T>(string rowId, bool enabled, bool restart,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken ct,
        Func<T, IReadOnlyCollection<string>, T>? includeReconciled = null)
    {
        var execution = AdmitCompatibility(rowId, enabled, restart, ct);
        _execution.Value = execution; _transitionFrame.Value = new();
        T result = default!; Exception? failure = null;
        try
        {
            ValidateCodeRefreshBeforeExecution(execution);
            result = await operation(execution.Request.Token).ConfigureAwait(false);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            await ReconcileBindingsAsync(execution.Request, changed).ConfigureAwait(false);
            if (includeReconciled is not null) result = includeReconciled(result, changed);
        }
        catch (Exception error) { failure = error; }
        try { await ReleaseExecutionAsync(execution).ConfigureAwait(false); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        _transitionFrame.Value = null; _execution.Value = null;
        if (failure is not null)
        {
            execution.Request.Fail(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        execution.Request.Complete(new(PluginTransitionOutcome.Converged, []));
        return result;
    }
}
