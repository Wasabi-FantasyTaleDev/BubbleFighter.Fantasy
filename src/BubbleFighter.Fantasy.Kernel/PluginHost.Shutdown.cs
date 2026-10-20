using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Kernel;

public sealed partial class PluginHost
{
    private Task? _shutdownTask;

    private async Task CompleteShutdownAsync(IntentExecution[] executing, TaskCompletionSource completion)
    {
        _transitionFrame.Value = new();
        Exception? failure = null;
        try
        {
            // Producers may hold keys needed by consumer cleanup, so signal them before waiting.
            CancelPreparingDefinitionSources();
            await Task.WhenAll(executing.Select(execution => execution.Settled.Task)).ConfigureAwait(false);
            var errors = new List<Exception>();
            foreach (var live in _live.Reverse().ToArray())
            {
                try { await UnloadCoreAsync(live.Row.Id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error)
                {
                    errors.Add(error);
                    try { _logger.LogError(error, "Failed to unload row '{RowId}' during shutdown.", live.Row.Id); }
                    catch (Exception loggingError) { errors.Add(loggingError); }
                }
            }
            lock (_intentGate)
            lock (_info)
                foreach (var row in _rows)
                    if (_info[row.Id].State == PluginState.Pending && !_live.Any(live => live.Row.Id == row.Id))
                        _info[row.Id] = InstanceInfo(row, DefinitionFor(row), PluginState.Unloaded, null);
            if (errors.Count > 0) throw new AggregateException("Shutdown left owned resources or failed cleanup.", errors);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_intentGate) _shutdownInProgress = false;
            ExitTransition();
            FinalizeRetiredEntries();
        }
        if (failure is null) completion.TrySetResult();
        else
        {
            completion.TrySetException(failure);
            _ = completion.Task.Exception;
        }
    }

    /// <summary>Closes target admission, drains in-flight activations and unloads live rows in
    /// reverse activation order. Concurrent calls join the same shutdown. A canceled observer
    /// leaves shutdown running and its admission fence in place until owned work settles.</summary>
    /// <param name="ct">Cancels this caller's observation without abandoning shutdown.</param>
    public async ValueTask ShutdownAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (EffectScope.IsInsideCallback || (_transitionFrame.Value is { } frame && Volatile.Read(ref frame.Active)))
            throw new InvalidOperationException("A lifecycle or owned callback cannot wait for host shutdown.");
        Task shutdown; TaskCompletionSource? completion = null; IntentExecution[]? executing = null;
        lock (_intentGate)
        {
            if (_shutdownTask is { IsCompleted: false } running) shutdown = running;
            else
            {
                if (_transitionBusy != 0)
                    throw new InvalidOperationException("An immediate lifecycle operation is already in progress.");
                _transitionBusy = 1;
                BeginShutdownTargets();
                executing = _runningExecutions.ToArray();
                foreach (var execution in executing)
                {
                    execution.Context?.Supersede();
                    execution.Request.Supersede();
                }
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shutdown = _shutdownTask = completion.Task;
            }
        }
        if (completion is not null)
        {
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(() => CompleteShutdownAsync(executing!, completion));
            else
            {
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(() => CompleteShutdownAsync(executing!, completion));
            }
        }
        await shutdown.WaitAsync(ct).ConfigureAwait(false);
    }
}
